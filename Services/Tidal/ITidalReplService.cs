// Resources/Tidal/ITidalReplService.cs

using System;
using System.Threading;
using System.Threading.Tasks;

namespace archimedes.Services.Tidal;

public sealed record TidalReplRequest
(
  string ReplExePath,
  string WorkingDirectory,
  string BootTidalFilePath,
  string ReadyMarker
);

public interface ITidalReplService : IDisposable
{
  bool IsRunning { get; }
  bool IsReady { get; }

  event EventHandler<string>? StdoutLine;
  event EventHandler<string>? StderrLine;
  event EventHandler<bool>? ReadyChanged;

  Task StartAsync(TidalReplRequest request, CancellationToken ct = default);
  Task EvalAsync(string code, CancellationToken ct = default);
  Task StopAsync(CancellationToken ct = default);
}
