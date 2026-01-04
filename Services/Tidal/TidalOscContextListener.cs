// Services/Tidal/TidalOscContextListener.cs

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace archimedes.Services.Tidal;

public readonly record struct OscContextSpan(int StartColumn, int StartLine, int EndColumn, int EndLine);

public sealed class OscContextEventArgs : EventArgs
{
  public OscContextEventArgs(string ident, double delta, double cycle, OscContextSpan span, TimeSpan? duration)
  {
    Ident = ident;
    Delta = delta;
    Cycle = cycle;
    Span = span;
    Duration = duration;
  }

  public string Ident { get; }
  public double Delta { get; }
  public double Cycle { get; }
  public OscContextSpan Span { get; }
  public TimeSpan? Duration { get; }
}

public sealed class TidalOscContextListener : IDisposable
{
  private const int DefaultPort = 6013;
  private const string DefaultPath = "/editor/highlights";
  private static readonly byte[] BundleHeader = Encoding.ASCII.GetBytes("#bundle\0");

  private readonly object _gate = new();
  private readonly int _port;
  private readonly string _path;
  private UdpClient? _client;
  private CancellationTokenSource? _cts;
  private bool _stopRequested;

  public TidalOscContextListener(int port = DefaultPort, string path = DefaultPath)
  {
    _port = port;
    _path = path;
  }

  public bool IsRunning { get { lock (_gate) return _client is not null; } }

  public event EventHandler<OscContextEventArgs>? ContextReceived;
  public event EventHandler<string>? StdoutLine;
  public event EventHandler<string>? StderrLine;

  public void Start()
  {
    lock (_gate)
    {
      if (_client is not null) return;

      _stopRequested = false;
      _cts = new CancellationTokenSource();
      try
      {
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
      }
      catch (Exception ex)
      {
        _stopRequested = true;
        _cts.Dispose();
        _cts = null;
        StderrLine?.Invoke(this, "late: OSC highlight listener failed to start: " + ex.Message);
        return;
      }

      _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    StdoutLine?.Invoke(this, $"OSC highlight listener active on 127.0.0.1:{_port}.");
  }

  public void Stop()
  {
    UdpClient? client;
    CancellationTokenSource? cts;
    lock (_gate)
    {
      _stopRequested = true;
      client = _client;
      _client = null;
      cts = _cts;
      _cts = null;
    }

    try { cts?.Cancel(); } catch { /* best effort */ }
    try { client?.Dispose(); } catch { /* best effort */ }
  }

  public void Dispose() => Stop();

  private async Task ListenLoop(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        UdpReceiveResult result;
        try
        {
          result = await _client!.ReceiveAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          break;
        }
        catch (ObjectDisposedException)
        {
          break;
        }

        if (result.Buffer.Length == 0) continue;
        HandlePacket(result.Buffer);
      }
    }
    catch (Exception ex)
    {
      if (!_stopRequested)
        StderrLine?.Invoke(this, "late: OSC highlight listener stopped: " + ex.Message);
    }
  }

  private void HandlePacket(ReadOnlySpan<byte> data)
  {
    try
    {
      ParsePacket(data);
    }
    catch (Exception ex)
    {
      StderrLine?.Invoke(this, "OSC highlight parse failed: " + ex.Message);
    }
  }

  private void ParsePacket(ReadOnlySpan<byte> data)
  {
    if (IsBundle(data))
    {
      if (data.Length < 16) return;
      var offset = 16;
      while (offset + 4 <= data.Length)
      {
        var size = ReadInt32(data, offset);
        offset += 4;
        if (size <= 0 || offset + size > data.Length) break;

        ParsePacket(data.Slice(offset, size));
        offset += size;
      }

      return;
    }

    if (TryParseContextMessage(data, out var msg))
    {
      var duration = ToDuration(msg.Delta);
      ContextReceived?.Invoke(this, new OscContextEventArgs(msg.Ident, msg.Delta, msg.Cycle, msg.Span, duration));
    }
  }

  private static bool IsBundle(ReadOnlySpan<byte> data)
  {
    return data.Length >= 8 && data.Slice(0, 8).SequenceEqual(BundleHeader);
  }

  private bool TryParseContextMessage(ReadOnlySpan<byte> data, out OscContextMessage message)
  {
    message = default;
    var offset = 0;

    if (!TryReadOscString(data, ref offset, out var address)) return false;
    if (!TryReadOscString(data, ref offset, out var tags)) return false;
    if (!string.Equals(address, _path, StringComparison.Ordinal)) return false;
    if (!tags.StartsWith(",sffiiii", StringComparison.Ordinal)) return false;

    if (!TryReadOscString(data, ref offset, out var ident)) return false;
    if (!TryReadOscFloat(data, ref offset, out var delta)) return false;
    if (!TryReadOscFloat(data, ref offset, out var cycle)) return false;
    if (!TryReadOscInt(data, ref offset, out var x)) return false;
    if (!TryReadOscInt(data, ref offset, out var y)) return false;
    if (!TryReadOscInt(data, ref offset, out var x2)) return false;
    if (!TryReadOscInt(data, ref offset, out var y2)) return false;

    message = new OscContextMessage(ident, delta, cycle, new OscContextSpan(x, y, x2, y2));
    return true;
  }

  private static bool TryReadOscString(ReadOnlySpan<byte> data, ref int offset, out string value)
  {
    value = string.Empty;
    if (offset >= data.Length) return false;

    var slice = data.Slice(offset);
    var zeroIndex = slice.IndexOf((byte)0);
    if (zeroIndex < 0) return false;

    value = Encoding.UTF8.GetString(slice.Slice(0, zeroIndex));
    var total = zeroIndex + 1;
    offset += Align4(total);
    return offset <= data.Length;
  }

  private static bool TryReadOscInt(ReadOnlySpan<byte> data, ref int offset, out int value)
  {
    value = 0;
    if (offset + 4 > data.Length) return false;
    value = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
    offset += 4;
    return true;
  }

  private static bool TryReadOscFloat(ReadOnlySpan<byte> data, ref int offset, out double value)
  {
    value = 0;
    if (offset + 4 > data.Length) return false;
    var raw = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
    value = BitConverter.Int32BitsToSingle(raw);
    offset += 4;
    return true;
  }

  private static int Align4(int size) => (size + 3) & ~3;

  private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
  {
    return BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
  }

  private static TimeSpan? ToDuration(double delta)
  {
    if (double.IsNaN(delta) || double.IsInfinity(delta)) return null;
    var ms = delta < 1000 ? delta * 1000 : delta / 1000;
    if (ms <= 0) return null;
    ms = Math.Clamp(ms, 60, 1500);
    return TimeSpan.FromMilliseconds(ms);
  }

  private readonly struct OscContextMessage
  {
    public OscContextMessage(string ident, double delta, double cycle, OscContextSpan span)
    {
      Ident = ident;
      Delta = delta;
      Cycle = cycle;
      Span = span;
    }

    public string Ident { get; }
    public double Delta { get; }
    public double Cycle { get; }
    public OscContextSpan Span { get; }
  }
}
