// Serices/Tidal/SuperColliderBootService.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace archimedes.Services.Tidal;

public sealed class SuperColliderBootService : ISuperColliderBootService
{
  private readonly object _gate = new();
  private Process? _proc;
  private TaskCompletionSource<bool>? _readyTcs;
  private bool _isReady;
  private bool _stopRequested;

  public bool IsRunning { get { lock (_gate) return _proc is { HasExited: false }; } }
  public bool IsReady { get { lock (_gate) return _isReady; } }

  public event EventHandler<string>? StdoutLine;
  public event EventHandler<string>? StderrLine;
  public event EventHandler<bool>? ReadyChanged;

  public Task BootAsync(SuperColliderBootRequest request, CancellationToken ct = default)
  {
    lock (_gate)
    {
      if (_proc is { HasExited: false }) return _readyTcs?.Task ?? Task.CompletedTask;

      CleanupStaleProcess();
      _stopRequested = false;
      _isReady = false;
      _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

      var psi = new ProcessStartInfo
      {
        FileName = request.SclangExePath,
        WorkingDirectory = request.WorkingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
      };

      psi.Environment["ARCHIMEDES_BOOT"] = "1";
      psi.Environment["ARCHIMEDES_BOOTFILE"] = request.ArchimedesBootFilePath;

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
          _proc = null;

          if (!wasReady)
            _readyTcs?.TrySetException(new InvalidOperationException("sclang exited before SuperDirt became ready."));
          else if (!_stopRequested)
            reportLateExit = true;
        }

        ClearPid();
        if (reportLateExit)
        {
          StderrLine?.Invoke(this, "late: SuperCollider exited.");
          ReadyChanged?.Invoke(this, false);
        }
      };

      _proc = p;
      if (!p.Start())
        throw new InvalidOperationException("Failed to start sclang.");

      WritePid(p.Id);
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
      _readyTcs?.TrySetCanceled();
    }

    if (p is null) return Task.CompletedTask;

    try
    {
      if (!p.HasExited) p.Kill(entireProcessTree: true);
    }
    catch (Exception ex) { StderrLine?.Invoke(this, "Kill failed: " + ex.Message); }
    finally { p.Dispose(); }

    ClearPid();
    ReadyChanged?.Invoke(this, false);
    return Task.CompletedTask;
  }

  public void Dispose() => _ = StopAsync();

  private static string GetPidPath()
  {
    var dir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "archimedes");
    return Path.Combine(dir, "sclang.pid");
  }

  private static void WritePid(int pid)
  {
    try
    {
      var path = GetPidPath();
      var dir = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(dir))
        Directory.CreateDirectory(dir);
      File.WriteAllText(path, pid.ToString());
    }
    catch
    {
      // best effort
    }
  }

  private static void ClearPid()
  {
    try
    {
      var path = GetPidPath();
      if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
      // best effort
    }
  }

  private static void CleanupStaleProcess()
  {
    try
    {
      var path = GetPidPath();
      if (!File.Exists(path)) return;

      if (!int.TryParse(File.ReadAllText(path).Trim(), out var pid))
      {
        ClearPid();
        return;
      }

      try
      {
        var proc = Process.GetProcessById(pid);
        if (!proc.HasExited && proc.ProcessName.Equals("sclang", StringComparison.OrdinalIgnoreCase))
        {
          try { proc.Kill(entireProcessTree: true); }
          catch { /* best effort */ }
        }
        proc.Dispose();
      }
      catch
      {
        // Process does not exist; fall through to cleanup.
      }
    }
    finally
    {
      ClearPid();
    }
  }
}
