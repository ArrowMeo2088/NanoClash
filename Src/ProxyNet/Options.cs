namespace Clash.ProxyNet;

/// <summary>Transport security under the VLESS request header.</summary>
internal enum VlessSecurity
{
    None,
    Tls,
    Reality
}

/// <summary>Strongly-typed VLESS outbound options (from <see cref="Clash.Config.ProxyNode"/>).</summary>
internal sealed class VlessOptions
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public VlessSecurity Security { get; init; } = VlessSecurity.None;
    public string Transport { get; init; } = "tcp";
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? Sni { get; init; }
    public IReadOnlyList<string>? Alpn { get; init; }
    public string? Flow { get; init; }
    public string? Fingerprint { get; init; }
    public string? RealityPublicKey { get; init; }
    public string? RealityShortId { get; init; }
    public bool AllowInsecure { get; init; }

    internal TransportKind TransportKind => ProxyTransport.Resolve(Transport);
}

/// <summary>Strongly-typed Trojan outbound options.</summary>
internal sealed class TrojanOptions
{
    public required string Password { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string Transport { get; init; } = "tcp";
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? Sni { get; init; }
    public IReadOnlyList<string>? Alpn { get; init; }
    public bool AllowInsecure { get; init; }

    internal TransportKind TransportKind => ProxyTransport.Resolve(Transport);
}
