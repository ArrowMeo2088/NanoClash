using System.IO.Compression;
using System.Security.Cryptography;

namespace Clash.Rules;

/// <summary>
/// Gunzips embedded <c>Rules.bin.gz</c> into <see cref="AppPaths.UserRulesBin"/> when missing or stale.
/// </summary>
internal static class RulesBootstrap
{
    public const string EmbeddedResourceName = "NanoClash.Rules.bin.gz";

    /// <summary>
    /// Ensure the uncompressed CFWR database exists under the user data directory.
    /// Returns the path to load (always <see cref="AppPaths.UserRulesBin"/> on success).
    /// </summary>
    public static string EnsureExtracted()
    {
        var asm = typeof(RulesBootstrap).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded rules database missing ({EmbeddedResourceName}). Rebuild with Res/Rules.bin.gz present.");

        byte[] plain;
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (var buf = new MemoryStream())
        {
            gzip.CopyTo(buf);
            plain = buf.ToArray();
        }

        if (plain.Length == 0)
            throw new InvalidOperationException("Embedded rules database is empty after GZip decompress.");

        Directory.CreateDirectory(AppPaths.UserDataDir);
        var path = AppPaths.UserRulesBin;
        try
        {
            if (File.Exists(path) && FilesEqual(path, plain))
                return path;

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, plain);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to extract rules database to '{path}'.", ex);
        }

        return path;
    }

    private static bool FilesEqual(string path, ReadOnlySpan<byte> expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != expected.Length)
                return false;
            var onDisk = File.ReadAllBytes(path);
            return CryptographicOperations.FixedTimeEquals(onDisk, expected);
        }
        catch
        {
            return false;
        }
    }
}
