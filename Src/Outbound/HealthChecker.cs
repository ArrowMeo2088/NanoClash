using System.Text;

using Clash.Config;

namespace Clash.Outbound;

internal enum HealthStatus
{
    Idle,
    Checking,
    Ok,
    LatencyFailed,
}

internal readonly record struct HealthCheckResult(
    bool LatencyOk,
    int? LatencyMs,
    HealthStatus Status,
    string? Error);

/// <summary>Latency-only node probe via HTTP generate_204 through the node.</summary>
internal sealed class HealthChecker(OutboundDialer dialer)
{
    public const int LatencyTimeoutMs = 3000;
    public const int MaxConcurrency = 30;

    private static readonly byte[] LatencyRequest = Encoding.ASCII.GetBytes(
        "GET /generate_204 HTTP/1.1\r\nHost: www.google.com\r\nConnection: close\r\nUser-Agent: NanoClash\r\n\r\n");

    public async Task<HealthCheckResult> CheckAsync(ProxyNode node, CancellationToken ct)
    {
        try
        {
            var latencyMs = await MeasureLatencyAsync(node, ct).ConfigureAwait(false);
            if (latencyMs is null)
                return new HealthCheckResult(false, null, HealthStatus.LatencyFailed, "timeout");

            return new HealthCheckResult(true, latencyMs, HealthStatus.Ok, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(false, null, HealthStatus.LatencyFailed, ex.Message);
        }
    }

    private async Task<int?> MeasureLatencyAsync(ProxyNode node, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LatencyTimeoutMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var stream = await dialer.DialViaAsync(node, "www.google.com", 80, timeout.Token)
            .ConfigureAwait(false);
        await stream.WriteAsync(LatencyRequest, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

        var buf = new byte[512];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total), timeout.Token).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
            if (buf.AsSpan(0, total).IndexOf("\r\n"u8) >= 0)
                break;
        }

        sw.Stop();
        if (total == 0)
            return null;

        var head = Encoding.ASCII.GetString(buf, 0, total);
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = lineEnd >= 0 ? head[..lineEnd] : head;
        if (!statusLine.Contains(" 204") && !statusLine.Contains(" 200"))
            return null;

        return (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
    }
}
