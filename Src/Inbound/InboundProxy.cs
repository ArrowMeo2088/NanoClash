using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Clash.IO;
using Clash.Outbound;
using Clash.Rules;
using Clash.Utils;

namespace Clash.Inbound;

/// <summary>HTTP proxy on :7887 — CONNECT + forward, rules then dial.</summary>
internal sealed class InboundProxy
{
    public const int Port = 7887;
    private const int MaxConcurrent = 256;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(20);
    private static readonly ReadOnlyMemory<byte> ConnectOk =
        "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

    private readonly RuleDb _rules;
    private readonly OutboundDialer _outbound;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<Task, byte> _inflight = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource _lifecycle = new();

    public InboundProxy(RuleDb rules, OutboundDialer outbound)
    {
        _rules = rules;
        _outbound = outbound;
    }

    /// <summary>Cancel in-flight dials/relays and close every accepted client socket.</summary>
    public void AbortActiveConnections()
    {
        CancellationTokenSource old;
        lock (_lifecycleGate)
        {
            old = _lifecycle;
            _lifecycle = new CancellationTokenSource();
        }

        try
        {
            old.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            old.Dispose();
        }
        catch
        {
            // ignore
        }

        foreach (var tcp in _clients.Keys)
        {
            try
            {
                tcp.Close();
            }
            catch
            {
                // ignore
            }

            try
            {
                tcp.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task RunAsync(IPAddress bind, CancellationToken ct)
    {
        var listener = new TcpListener(bind, Port);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                SocketUtil.ConfigureNoDelay(client);
                var task = HandleClientAsync(client, ct);
                _inflight[task] = 0;
                _ = task.ContinueWith(t => _inflight.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            var pending = _inflight.Keys.ToArray();
            if (pending.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch
                {
                    // ignore individual failures
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken listenerCt)
    {
        CancellationToken lifecycleCt;
        lock (_lifecycleGate)
            lifecycleCt = _lifecycle.Token;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(listenerCt, lifecycleCt);
        var ct = linked.Token;

        _clients[tcp] = 0;
        try
        {
            try
            {
                await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                tcp.Dispose();
                return;
            }

            try
            {
                using (tcp)
                {
                    await using var client = tcp.GetStream();
                    try
                    {
                        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        headerCts.CancelAfter(HeaderTimeout);
                        var (method, target, headers, leftover) =
                            await ReadRequestAsync(client, headerCts.Token).ConfigureAwait(false);

                        if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                            await HandleConnectAsync(client, target, leftover, ct).ConfigureAwait(false);
                        else
                            await HandleForwardAsync(client, method, target, headers, leftover, ct)
                                .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // node switch / shutdown
                    }
                    catch
                    {
                        try
                        {
                            await Relay.WriteHttpErrorAsync(client, 503, "Unable to connect", CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }
        finally
        {
            _clients.TryRemove(tcp, out _);
        }
    }

    private async Task HandleConnectAsync(Stream client, string targetRaw, byte[] leftover, CancellationToken ct)
    {
        string target;
        try
        {
            target = Authority.WithPort(targetRaw, "443");
        }
        catch
        {
            await Relay.WriteHttpErrorAsync(client, 400, "Invalid host", ct).ConfigureAwait(false);
            return;
        }

        var action = _rules.DecideRoute(target);

        switch (action)
        {
            case RuleAction.Reject:
                await Relay.WriteHttpErrorAsync(client, 403, "Forbidden by rules", ct).ConfigureAwait(false);
                return;
            case RuleAction.Direct:
            {
                try
                {
                    await TunnelConnectAsync(
                        client,
                        leftover,
                        async token => await DirectDial.ConnectAsync(target, token).ConfigureAwait(false),
                        uploadLatency: false,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try
                    {
                        await Relay.WriteHttpErrorAsync(client, 503, "Unable to connect", ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return;
            }
            default:
            {
                try
                {
                    await TunnelConnectAsync(
                        client,
                        leftover,
                        async token => await _outbound.DialAsync(target, token).ConfigureAwait(false),
                        uploadLatency: true,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try
                    {
                        await Relay.WriteHttpErrorAsync(client, 503, "Unable to connect", ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return;
            }
        }
    }

    /// <summary>
    /// Dial outbound while buffering early client bytes (leftover + DataAvailable), reply 200, flush, relay.
    /// </summary>
    private async Task TunnelConnectAsync(
        Stream client,
        byte[] leftover,
        Func<CancellationToken, Task<Stream>> dial,
        bool uploadLatency,
        CancellationToken ct)
    {
        using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dialCts.CancelAfter(DialTimeout);

        await using var early = new MemoryStream();
        const int MaxEarlyBytes = 256 * 1024;
        void AppendEarly(ReadOnlySpan<byte> chunk)
        {
            if (early.Length + chunk.Length > MaxEarlyBytes)
                throw new InvalidOperationException("CONNECT early buffer limit exceeded");

            early.Write(chunk);
        }

        if (leftover.Length > 0)
            AppendEarly(leftover);

        var sw = Stopwatch.StartNew();
        var dialTask = dial(dialCts.Token);
        var buf = new byte[16 * 1024];
        var ns = client as NetworkStream;

        // Only read when bytes are already buffered — never leave an orphaned ReadAsync racing dial.
        while (!dialTask.IsCompleted)
        {
            if (ns is { DataAvailable: true })
            {
                var n = await ns.ReadAsync(buf.AsMemory(), dialCts.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                AppendEarly(buf.AsSpan(0, n));
                continue;
            }

            await Task.WhenAny(dialTask, Task.Delay(5, dialCts.Token)).ConfigureAwait(false);
        }

        Stream dest;
        try
        {
            dest = await dialTask.ConfigureAwait(false);
        }
        catch
        {
            throw;
        }

        sw.Stop();
        if (uploadLatency)
            TrafficCounters.NoteUploadLatency((int)sw.ElapsedMilliseconds);
        else
            TrafficCounters.NoteDownloadLatency((int)sw.ElapsedMilliseconds);

        // Drain anything that arrived in the last dial tick.
        while (ns is { DataAvailable: true })
        {
            var n = await ns.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            AppendEarly(buf.AsSpan(0, n));
        }

        await using (dest)
        {
            await client.WriteAsync(ConnectOk, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);

            var seed = early.Length > 0 ? early.ToArray() : [];
            await TlsHelloCoalesce.FlushFirstRecordAsync(client, dest, seed, ct)
                .ConfigureAwait(false);

            await Relay.CopyBidirectionalAsync(client, dest, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleForwardAsync(
        Stream client,
        string method,
        string target,
        Dictionary<string, string> headers,
        byte[] leftover,
        CancellationToken ct)
    {
        string hostHeader;
        Uri uri;
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri(target, UriKind.Absolute);
            hostHeader = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        else
        {
            if (!headers.TryGetValue("Host", out hostHeader!) || string.IsNullOrEmpty(hostHeader))
            {
                await Relay.WriteHttpErrorAsync(client, 400, "Invalid host", ct).ConfigureAwait(false);
                return;
            }

            uri = new Uri("http://" + hostHeader + (target.StartsWith('/') ? target : "/" + target));
        }

        string authority;
        try
        {
            authority = Authority.WithPort(hostHeader, "80");
        }
        catch
        {
            await Relay.WriteHttpErrorAsync(client, 400, "Invalid host", ct).ConfigureAwait(false);
            return;
        }

        var action = _rules.DecideRoute(authority);

        if (action == RuleAction.Reject)
        {
            await Relay.WriteHttpErrorAsync(client, 403, "Forbidden by rules", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var dest = action == RuleAction.Direct
                ? await DirectDial.ConnectAsync(authority, ct).ConfigureAwait(false)
                : await _outbound.DialAsync(authority, ct).ConfigureAwait(false);

            var path = uri.PathAndQuery;
            if (string.IsNullOrEmpty(path))
                path = "/";
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(hostHeader).Append("\r\n");
            foreach (var (k, v) in headers)
            {
                if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
            }

            sb.Append("Connection: close\r\n\r\n");
            await dest.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
            if (leftover.Length > 0)
                await dest.WriteAsync(leftover, ct).ConfigureAwait(false);

            await Relay.CopyBidirectionalAsync(client, dest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await Relay.WriteHttpErrorAsync(client, 503, "Failed to reach destination", ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers, byte[] Leftover)>
        ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (true)
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("client closed before headers");
            ms.Write(buf, 0, n);
            var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            var idx = span.IndexOf("\r\n\r\n"u8);
            if (idx < 0)
            {
                if (ms.Length > 64 * 1024)
                    throw new InvalidOperationException("headers too large");
                continue;
            }

            var headerBytes = span[..idx];
            var leftover = span[(idx + 4)..].ToArray();
            var text = Encoding.ASCII.GetString(headerBytes);
            var lines = text.Split("\r\n");
            if (lines.Length == 0)
                throw new InvalidOperationException("empty request");
            var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new InvalidOperationException("bad request line");
            var method = parts[0];
            var target = parts[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                    continue;
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            return (method, target, headers, leftover);
        }
    }
}
