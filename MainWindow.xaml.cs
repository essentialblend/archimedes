// MainWindow.xaml.cs

using archimedes.Services.Tidal;
using archimedes.Views;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace archimedes
{
  public partial class MainWindow : Window
  {
    private readonly Stopwatch _hushThrottle = Stopwatch.StartNew();
    private const int HushThrottleMs = 200;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private readonly ResourceDictionaryThemeToggler _themeToggler = new();
    private readonly ISuperColliderBootService _scBoot = new SuperColliderBootService();
    private readonly ITidalReplService _tidal = new TidalReplService();
    private readonly TidalOscContextListener _oscContext = new();
    private readonly object _logGate = new();
    private readonly DispatcherTimer _evalStatusTimer = new();
    private const string SourceSuperCollider = "SC";
    private const string SourceTidal = "Tidal";
    private const string SourceBoot = "Boot";
    private const string SourceOsc = "OSC";
    private StreamWriter? _bootLog;
    private string? _bootLogPath;
    private CancellationTokenSource? _bootCts;
    private bool _bootInProgress;
    private bool _autoBooted;

    public MainWindow()
    {
      InitializeComponent();
      ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
      CodeEditor.EvalRequested += OnEvalRequested;

      // Give TransportBar access to the service
      TransportBar.BootService = _scBoot;

      // Clicking the button is routed up to here
      TransportBar.BootTidalClicked += OnBootTidal;

      // Capture all output (UI + file)
      _scBoot.StdoutLine += (_, line) => OnBootLine(SourceSuperCollider, false, line);
      _scBoot.StderrLine += (_, line) => OnBootLine(SourceSuperCollider, true, line);

      _tidal.StdoutLine += (_, line) => OnBootLine(SourceTidal, false, line);
      _tidal.StderrLine += (_, line) => OnBootLine(SourceTidal, true, line);
      _oscContext.StdoutLine += (_, line) => OnBootLine(SourceOsc, false, line);
      _oscContext.StderrLine += (_, line) => OnBootLine(SourceOsc, true, line);
      _oscContext.ContextReceived += OnOscContext;

      _scBoot.ReadyChanged += (_, __) => UpdateReadyStatus();
      _tidal.ReadyChanged += (_, __) => UpdateReadyStatus();

      UpdateReadyStatus();
      Loaded += OnAutoBootTidal;
      Loaded += (_, __) => _oscContext.Start();
      _evalStatusTimer.Interval = TimeSpan.FromMilliseconds(900);
      _evalStatusTimer.Tick += OnEvalStatusTimerTick;

      Closed += async (_, __) =>
      {
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
        await StopServicesAsync(updateStatus: false);
        _oscContext.Dispose();
        _tidal.Dispose();
        _scBoot.Dispose();
      };
    }

    private void ExecuteHush()
    {
      if (_hushThrottle.ElapsedMilliseconds < HushThrottleMs) return;
      _hushThrottle.Restart();
      OnEvalRequested(this, "hush");
    }

    private static bool IsCtrlDown()
    {
      return Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
    }

    private static bool IsAltDown()
    {
      return Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
    }

    private static bool IsShiftDown()
    {
      return Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
      if (handled) return;
      if (msg.message != WmKeyDown && msg.message != WmSysKeyDown) return;

      var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
      if (key != Key.H) return;

      if (!IsCtrlDown()) return;
      if (IsAltDown() || IsShiftDown()) return;
      if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) return;

      try
      {
        ExecuteHush();
        handled = true;
      }
      catch (Exception ex)
      {
        ReportStatusError(SourceTidal, "Hush failed: " + ex.Message, "Hush failed (see log)");
      }
    }


    private bool IsTidalReady => _scBoot.IsReady && _tidal.IsReady;
    private bool IsAnyTidalRunning => _scBoot.IsRunning || _tidal.IsRunning;

    private void OnBootLine(string source, bool isError, string line)
    {
      LogOutputLine(source, isError, line);
    }

    private void LogOutputLine(string source, bool isError, string line)
    {
      if (string.IsNullOrWhiteSpace(line)) return;
      WriteBootLog($"{source}/{(isError ? "err" : "out")}", line);
    }

    private void ReportStatusError(string source, string message, string status)
    {
      if (string.IsNullOrWhiteSpace(message)) return;
      WriteBootLog($"{source}/err", message);
      _evalStatusTimer.Stop();
      SetEvalStatus(status);
    }

    private void SetEvalStatus(string text) => RunOnUi(() => CodeEditor.SetEvalStatus(text));

    private void RunOnUi(Action action)
    {
      if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
      if (Dispatcher.CheckAccess()) action();
      else Dispatcher.BeginInvoke(action);
    }

    private void SetTransientEvalStatus(string text)
    {
      SetEvalStatus(text);
      _evalStatusTimer.Stop();
      _evalStatusTimer.Start();
    }

    private void OnEvalStatusTimerTick(object? sender, EventArgs e)
    {
      _evalStatusTimer.Stop();
      UpdateReadyStatus(updateText: true);
    }

    private void UpdateReadyStatus(bool updateText = true)
    {
      string status;
      if (IsTidalReady)
        status = "Tidal ready";
      else if (_bootInProgress || IsAnyTidalRunning)
      {
        if (!_scBoot.IsReady)
          status = "Booting SuperCollider...";
        else if (!_tidal.IsReady)
          status = "Booting Tidal...";
        else
          status = "Tidal not ready";
      }
      else
      {
        status = "Stopped";
      }

      var state = IsTidalReady
        ? TidalBootState.Ready
        : (_bootInProgress || IsAnyTidalRunning) ? TidalBootState.Booting : TidalBootState.Off;

      TransportBar.SetTidalState(state);
      if (updateText)
      {
        _evalStatusTimer.Stop();
        SetEvalStatus(status);
      }
    }

    private void OpenBootLog()
    {
      var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var logDir = Path.Combine(lad, "archimedes", "logs");
      Directory.CreateDirectory(logDir);

      _bootLogPath = Path.Combine(logDir, $"tidal_boot_{DateTime.Now:yyyyMMdd_HHmmss}.log");
      _bootLog = new StreamWriter(_bootLogPath, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    private void WriteBootLog(string src, string line)
    {
      lock (_logGate)
      {
        if (_bootLog is null) return;
        try
        {
          _bootLog.WriteLine($"{DateTime.Now:O} [{src}] {line}");
        }
        catch
        {
          // best effort logging
        }
      }
    }

    private void CloseBootLog()
    {
      lock (_logGate)
      {
        _bootLog?.Dispose();
        _bootLog = null;
      }
    }

    private async void OnEvalRequested(object? _, string code)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(code)) return;

        if (!IsTidalReady)
        {
          SetEvalStatus(IsAnyTidalRunning ? "Tidal not ready (booting)" : "Tidal not ready");
          return;
        }

        await _tidal.EvalAsync(code);
        SetTransientEvalStatus("Sent");
      }
      catch (Exception ex)
      {
        ReportStatusError(SourceTidal, "Eval failed: " + ex.Message, "Error (see log)");
      }
    }

    private void OnOscContext(object? sender, OscContextEventArgs e)
    {
      try
      {
        var span = e.Span;
        RunOnUi(() => CodeEditor.FlashPatternSpan(span.StartColumn, span.StartLine, span.EndColumn, span.EndLine, e.Duration));
        RunOnUi(() => PerfVfxPanel.OnTidalPulse(e.Ident, span.StartLine, span.StartColumn, e.Delta));
      }
      catch (Exception ex)
      {
        ReportStatusError(SourceOsc, "Highlight failed: " + ex.Message, "Highlight error (see log)");
      }
    }

    private async void OnBootTidal(object sender, RoutedEventArgs e)
    {
      try
      {
        if (_bootInProgress)
        {
          await StopServicesAsync(updateStatus: true);
          return;
        }

        // Toggle: if running, stop.
        if (IsAnyTidalRunning)
        {
          await StopServicesAsync(updateStatus: true);
          return;
        }

        await BootTidalAsync();
      }
      catch (OperationCanceledException)
      {
        await StopServicesAsync(updateStatus: true);
      }
      catch (Exception ex)
      {
        var logPath = _bootLogPath;
        ReportStatusError(SourceBoot, "Boot failed: " + ex.Message, "Boot failed (see log)");
        await StopServicesAsync(updateStatus: false);

        var msg = logPath is null ? ex.Message : $"{ex.Message}\n\nLog: {logPath}";
        MessageBox.Show(msg, "Boot Tidal failed", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }

    private async void OnAutoBootTidal(object? sender, RoutedEventArgs e)
    {
      if (_autoBooted) return;
      _autoBooted = true;

      try
      {
        if (IsAnyTidalRunning || _bootInProgress) return;
        await BootTidalAsync();
      }
      catch (OperationCanceledException)
      {
        await StopServicesAsync(updateStatus: true);
      }
      catch (Exception ex)
      {
        var logPath = _bootLogPath;
        ReportStatusError(SourceBoot, "Boot failed: " + ex.Message, "Boot failed (see log)");
        await StopServicesAsync(updateStatus: false);

        var msg = logPath is null ? ex.Message : $"{ex.Message}\n\nLog: {logPath}";
        MessageBox.Show(msg, "Boot Tidal failed", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }

    private async Task StopServicesAsync(bool updateStatus)
    {
      try
      {
        _bootCts?.Cancel();
        _bootCts?.Dispose();
        _bootCts = null;
        _bootInProgress = false;

        try { await _tidal.StopAsync(); }
        catch (Exception ex) { ReportStatusError(SourceTidal, "Stop failed: " + ex.Message, "Stop failed (see log)"); }

        try { await _scBoot.StopAsync(); }
        catch (Exception ex) { ReportStatusError(SourceSuperCollider, "Stop failed: " + ex.Message, "Stop failed (see log)"); }

        try { CloseBootLog(); }
        catch { /* best effort */ }

        if (updateStatus) SetEvalStatus("Stopped");
        UpdateReadyStatus(updateText: updateStatus);
      }
      catch (Exception ex)
      {
        ReportStatusError(SourceBoot, "Stop failed: " + ex.Message, "Stop failed (see log)");
      }
    }

    private async Task BootTidalAsync()
    {
      CloseBootLog();
      OpenBootLog();
      _bootInProgress = true;
      UpdateReadyStatus();
      SetEvalStatus("Booting SuperCollider...");

      _bootCts?.Cancel();
      _bootCts?.Dispose();
      _bootCts = new CancellationTokenSource();
      var ct = _bootCts.Token;

      var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var bootFile = Path.Combine(lad, "archimedes", "archimedes_boot.scd");
      if (!File.Exists(bootFile))
        throw new FileNotFoundException("Missing archimedes_boot.scd", bootFile);

      var sclangExe = @"C:\Program Files\SuperCollider-3.12.1\sclang.exe";
      var scDir = Path.GetDirectoryName(sclangExe) ?? throw new InvalidOperationException("Bad sclang path.");

      await _scBoot.BootAsync(new SuperColliderBootRequest(
        SclangExePath: sclangExe,
        WorkingDirectory: scDir,
        ArchimedesBootFilePath: bootFile
      ), ct);

      ct.ThrowIfCancellationRequested();
      SetEvalStatus("Booting Tidal...");

      var bootHs = Path.Combine(lad, "archimedes", "ArchimedesBootTidal.hs");
      if (!File.Exists(bootHs))
        throw new FileNotFoundException("Missing ArchimedesBootTidal.hs", bootHs);

      await _tidal.StartAsync(new TidalReplRequest(
        ReplExePath: @"D:\ghcup\bin\ghci.exe",
        WorkingDirectory: @"D:\ghcup\bin",
        BootTidalFilePath: bootHs,
        ReadyMarker: "ARCHIMEDES_TIDAL_READY"
      ), ct);
      _bootInProgress = false;
      UpdateReadyStatus();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => _themeToggler.ToggleTheme();
  }
}
