using System.Net;

using Clash.IO;
using Clash.Outbound;
using Clash.Rules;

namespace Clash.Inbound;

/// <summary>HTTP :7887 inbound + system proxy + traffic counters.</summary>
internal sealed class ProxyService : IAsyncDisposable
{
    private RuleDb? _rules;
    private InboundProxy? _inbound;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private OutboundDialer? _outbound;

    public ProxyService()
    {
    }

    public bool IsSystemProxyEnabled { get; private set; }
    public bool IsListening => _runTask is { IsCompleted: false };

    public int UploadLatencyMs { get; private set; }
    public int DownloadLatencyMs { get; private set; }
    public int UploadSpeedKBps { get; private set; }
    public int DownloadSpeedKBps { get; private set; }
    public int UploadTrafficMB { get; private set; }
    public int DownloadTrafficMB { get; private set; }
    public int TotalSpeedKBps { get; private set; }
    public int TotalTrafficMB { get; private set; }

    public void SetSystemProxy(bool enabled)
    {
        SystemProxy.SetEnabled(enabled);
        IsSystemProxyEnabled = enabled;
    }

    /// <summary>Drop every live inbound tunnel so the next dial uses the newly selected node.</summary>
    public void AbortActiveConnections() => _inbound?.AbortActiveConnections();

    public async Task StartAsync(RuleDb rules, OutboundDialer outbound, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _rules = rules;
        _outbound = outbound;
        TrafficCounters.Reset();
        _inbound = new InboundProxy(rules, outbound);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _runTask = Task.Run(async () =>
        {
            try
            {
                await _inbound.RunAsync(IPAddress.Loopback, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stop
            }
            catch
            {
                // inbound stopped unexpectedly
            }
        }, CancellationToken.None);
        await Task.Delay(50, ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _cts?.Dispose();
        _cts = null;
        _runTask = null;
        _inbound = null;
        _outbound = null;
    }

    public void PollStats()
    {
        var (upLat, downLat, upK, downK, upMb, downMb, totalK, totalMb) = TrafficCounters.Snapshot();
        UploadLatencyMs = upLat;
        DownloadLatencyMs = downLat;
        UploadSpeedKBps = upK;
        DownloadSpeedKBps = downK;
        UploadTrafficMB = upMb;
        DownloadTrafficMB = downMb;
        TotalSpeedKBps = totalK;
        TotalTrafficMB = totalMb;
    }

    public async ValueTask DisposeAsync()
    {
        SetSystemProxy(false);
        await StopAsync().ConfigureAwait(false);
        _rules?.Dispose();
        _rules = null;
    }
}
