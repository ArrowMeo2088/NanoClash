using System.Net;

namespace Clash.Tun.SystemStack;

internal sealed class TcpSession
{
    public required IPEndPoint Source { get; init; }
    public required IPEndPoint Destination { get; init; }
    public long LastActiveTicks { get; set; } = Environment.TickCount64;
}

/// <summary>Port-based TCP NAT table (sing-tun TCPNat algorithm).</summary>
internal sealed class TcpNat : IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private readonly Dictionary<IPEndPoint, ushort> _addrMap = new();
    private readonly Dictionary<ushort, TcpSession> _portMap = new();
    private readonly CancellationTokenSource _cts = new();
    private ushort _portIndex = 10000;

    public TcpNat(TimeSpan timeout)
    {
        _timeout = timeout;
        _ = Task.Run(TimeoutLoopAsync);
    }

    public ushort Lookup(IPEndPoint source, IPEndPoint destination)
    {
        lock (_gate)
        {
            if (_addrMap.TryGetValue(source, out var existing))
            {
                if (_portMap.TryGetValue(existing, out var s))
                {
                    // Same client tuple reused for a new destination (ephemeral port recycle).
                    // Replace the session object — never mutate Destination on a live instance
                    // that Accept may still hold (race → misroute).
                    if (!s.Destination.Equals(destination))
                    {
                        _portMap[existing] = new TcpSession
                        {
                            Source = s.Source,
                            Destination = destination,
                            LastActiveTicks = Environment.TickCount64,
                        };
                    }
                    else
                    {
                        s.LastActiveTicks = Environment.TickCount64;
                    }
                }

                return existing;
            }

            // Skip ports still in use (wrap-around safety; sing-tun omits this).
            ushort next = 0;
            for (var attempt = 0; attempt < 55535; attempt++)
            {
                next = _portIndex;
                if (next == 0)
                {
                    next = 10000;
                    _portIndex = 10001;
                }
                else
                {
                    _portIndex++;
                }

                if (!_portMap.ContainsKey(next))
                    break;
            }

            if (next == 0 || _portMap.ContainsKey(next))
                throw new InvalidOperationException("TCP NAT port space exhausted");

            _addrMap[source] = next;
            _portMap[next] = new TcpSession
            {
                Source = source,
                Destination = destination,
                LastActiveTicks = Environment.TickCount64,
            };
            return next;
        }
    }

    public bool HasDestination(IPAddress ip)
    {
        lock (_gate)
        {
            foreach (var s in _portMap.Values)
            {
                if (s.Destination.Address.Equals(ip))
                    return true;
            }
        }

        return false;
    }

    public TcpSession? LookupBack(ushort port)
    {
        lock (_gate)
        {
            if (!_portMap.TryGetValue(port, out var session))
                return null;
            session.LastActiveTicks = Environment.TickCount64;
            // Destination is init-only; Accept may keep this reference while Lookup replaces
            // the map entry with a new session for a recycled client tuple.
            return session;
        }
    }

    private async Task TimeoutLoopAsync()
    {
        try
        {
            // Sweep often; session idle timeout is still _timeout. Do not Delay(_timeout)
            // or Dispose Cancel waits up to that long if Cancel races poorly.
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // stop
        }
    }

    private void Sweep()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            List<ushort>? dead = null;
            foreach (var (port, session) in _portMap)
            {
                if (now - session.LastActiveTicks > _timeout.TotalMilliseconds)
                {
                    dead ??= [];
                    dead.Add(port);
                }
            }

            if (dead is null)
                return;
            foreach (var port in dead)
            {
                if (_portMap.Remove(port, out var s))
                    _addrMap.Remove(s.Source);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
    }
}
