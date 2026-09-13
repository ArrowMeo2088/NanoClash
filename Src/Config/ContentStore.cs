using System.Security.Cryptography;

namespace Clash.Config;

internal static class ContentStore
{
    public static string ComputeSha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Save(byte[] data)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var hash = ComputeSha256Hex(data);
        var path = Path.Combine(AppPaths.DataDir, hash);
        if (!File.Exists(path))
            File.WriteAllBytes(path, data);
        return hash;
    }

    public static byte[]? TryRead(string hash)
    {
        if (!IsSafeContentHash(hash))
            return null;

        var path = Path.GetFullPath(Path.Combine(AppPaths.DataDir, hash));
        var root = Path.GetFullPath(AppPaths.DataDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public static void DeleteIfUnreferenced(string hash, IEnumerable<string> referencedHashes)
    {
        if (!IsSafeContentHash(hash))
            return;
        foreach (var h in referencedHashes)
        {
            if (string.Equals(h, hash, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var path = Path.Combine(AppPaths.DataDir, hash);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Only lowercase/uppercase hex SHA-256 (64 chars); rejects path traversal.</summary>
    internal static bool IsSafeContentHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64)
            return false;
        if (hash.Contains("..", StringComparison.Ordinal) ||
            hash.Contains('/') ||
            hash.Contains('\\') ||
            hash.Contains(':'))
            return false;
        foreach (var c in hash)
        {
            if (c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
                continue;
            return false;
        }

        return true;
    }
}
