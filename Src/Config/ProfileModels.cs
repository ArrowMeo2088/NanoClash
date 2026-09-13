namespace Clash.Config;

internal enum ProfileKind
{
    Cloud,
    Local,
}

internal sealed class ProfileEntry
{
    public required string Name { get; set; }
    public required string Hash { get; set; }
    public required ProfileKind Kind { get; set; }
    public string? Url { get; set; }
    /// <summary>Used bytes (upload+download). Cloud only.</summary>
    public long? UsedBytes { get; set; }
    /// <summary>Total quota bytes. Cloud only; null/0 = unlimited.</summary>
    public long? TotalBytes { get; set; }
    /// <summary>Unix seconds. Cloud only.</summary>
    public long? ExpireUnix { get; set; }

    public string ContentPath => Path.Combine(AppPaths.DataDir, Hash);
}
