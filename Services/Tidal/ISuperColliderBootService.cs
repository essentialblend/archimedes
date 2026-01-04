// Serices/Tidal/ISuperColliderBootService.cs

using System;
using System.Threading;
using System.Threading.Tasks;

namespace archimedes.Services.Tidal;

public sealed record SuperColliderBootRequest(
    string SclangExePath,
    string WorkingDirectory,
    string ArchimedesBootFilePath,
    string ReadyMarker = "SuperDirt: listening to Tidal on port 57120"
);

public interface ISuperColliderBootService : IDisposable
{
  bool IsRunning { get; }
  bool IsReady { get; }

  event EventHandler<string>? StdoutLine;
  event EventHandler<string>? StderrLine;
  event EventHandler<bool>? ReadyChanged;

  Task BootAsync(SuperColliderBootRequest request, CancellationToken ct = default);
  Task StopAsync(CancellationToken ct = default);
}

