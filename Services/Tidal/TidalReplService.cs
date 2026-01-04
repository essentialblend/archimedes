// Serices/Tidal/TidalReplService.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace archimedes.Services.Tidal;

public sealed class TidalReplService : ITidalReplService
{
  private readonly object _gate = new();
  private Process? _proc;
  private StreamWriter? _stdin;
  private TaskCompletionSource<bool>? _readyTcs;
  private bool _isReady;
  private bool _stopRequested;

  public bool IsRunning { get { lock (_gate) return _proc is { HasExited: false }; } }
  public bool IsReady { get { lock (_gate) return _isReady; } }

  public event EventHandler<string>? StdoutLine;
  public event EventHandler<string>? StderrLine;
  public event EventHandler<bool>? ReadyChanged;

  public Task StartAsync(TidalReplRequest request, CancellationToken ct = default)
  {
    lock (_gate)
    {
      if (_proc is { HasExited: false }) return _readyTcs?.Task ?? Task.CompletedTask;

      _stopRequested = false;
      _isReady = false;
      _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

      var psi = new ProcessStartInfo
      {
        FileName = request.ReplExePath,
        WorkingDirectory = request.WorkingDirectory,
        UseShellExecute = false,
        // Send code later
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
      };

      psi.ArgumentList.Add("-ghci-script");
      psi.ArgumentList.Add(request.BootTidalFilePath);

      var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
      p.OutputDataReceived += (_, e) => OnStdout(e.Data, request.ReadyMarker);
      p.ErrorDataReceived += (_, e) => OnStderr(e.Data);
      p.Exited += (_, __) =>
      {
        bool reportLateExit = false;
        lock (_gate)
        {
          var wasReady = _isReady;
          _isReady = false;
          _stdin = null;
          _proc = null;

          if (!wasReady)
            _readyTcs?.TrySetException(new InvalidOperationException("ghci exited before Tidal REPL became ready."));
          else if (!_stopRequested)
            reportLateExit = true;
        }

        if (reportLateExit)
        {
          StderrLine?.Invoke(this, "late: Tidal REPL exited.");
          ReadyChanged?.Invoke(this, false);
        }
      };

      _proc = p;
      if (!p.Start()) throw new InvalidOperationException("Failed to start ghci.");

      _stdin = p.StandardInput;
      _stdin.AutoFlush = true;

      p.BeginOutputReadLine();
      p.BeginErrorReadLine();
    }

    return WaitReadyAsync(ct);
  }

  private async Task WaitReadyAsync(CancellationToken ct)
  {
    Task readyTask;
    lock (_gate) readyTask = _readyTcs!.Task;

    using var reg = ct.Register(() =>
    {
      lock (_gate) _readyTcs?.TrySetCanceled(ct);
    });

    try { await readyTask.ConfigureAwait(false); }
    catch (OperationCanceledException)
    {
      await StopAsync(CancellationToken.None).ConfigureAwait(false);
      throw;
    }
  }

  private void OnStdout(string? line, string readyMarker)
  {
    if (string.IsNullOrEmpty(line)) return;
    StdoutLine?.Invoke(this, line);

    if (line.Contains(readyMarker, StringComparison.Ordinal))
    {
      bool raise = false;
      lock (_gate)
      {
        if (!_isReady)
        {
          _isReady = true;
          raise = true;
          _readyTcs?.TrySetResult(true);
        }
      }
      if (raise) ReadyChanged?.Invoke(this, true);
    }
  }

  private void OnStderr(string? line)
  {
    if (string.IsNullOrEmpty(line)) return;
    StderrLine?.Invoke(this, line);
  }

  public Task StopAsync(CancellationToken ct = default)
  {
    Process? p;
    lock (_gate)
    {
      _stopRequested = true;
      p = _proc;
      _proc = null;
      _isReady = false;
      _stdin = null;
      _readyTcs?.TrySetCanceled();
    }

    if (p is null) return Task.CompletedTask;

    try
    {
      if (!p.HasExited) p.Kill(entireProcessTree: true);
    }
    catch (Exception ex) { StderrLine?.Invoke(this, "Kill failed: " + ex.Message); }
    finally { p.Dispose(); }

    ReadyChanged?.Invoke(this, false);
    return Task.CompletedTask;
  }

  public Task EvalAsync(string code, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(code)) return Task.CompletedTask;

    StreamWriter? w;
    lock (_gate) w = _stdin;
    if (w is null) throw new InvalidOperationException("Tidal REPL stdin not available.");

    // GHCi accepts multiple lines if they're wrapped:
    // :{  ...  :}
    w.WriteLine(":{");
    foreach (var line in code.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
      w.WriteLine(line);
    w.WriteLine(":}");

    return Task.CompletedTask;
  }

  public void Dispose() => _ = StopAsync();
}
