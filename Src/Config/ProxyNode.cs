namespace Clash.Config;

internal sealed class ProxyNode
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Server { get; init; }
    public required int Port { get; init; }
    public string? Uuid { get; init; }
    public string? Password { get; init; }
    public bool Tls { get; init; }
    public string? ServerName { get; init; }
    public string? Flow { get; init; }
    public string Network { get; init; } = "tcp";
    public string? ClientFingerprint { get; init; }
    public string? RealityPublicKey { get; init; }
    public string? RealityShortId { get; init; }
    public bool SkipCertVerify { get; init; }
    public string? WsPath { get; init; }
    public string? WsHost { get; init; }
    /// <summary>Explicit security when known (none/tls/reality); otherwise derived.</summary>
    public string? Security { get; init; }

    /// <summary>Subscription banner rows (traffic/reset/expiry/site) — not real exit nodes.</summary>
    public bool IsSubscriptionInfo =>
        Name.Contains("剩余流量", StringComparison.Ordinal) ||
        Name.Contains("距离下次重置", StringComparison.Ordinal) ||
        Name.Contains("套餐到期", StringComparison.Ordinal) ||
        Name.Contains("官网", StringComparison.Ordinal);
}
