using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Clash.Tun.Windows;

/// <summary>
/// Extracts embedded <c>wintun.dll</c> to <see cref="AppPaths.WintunDll"/> when missing or stale.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WintunBootstrap
{
    public const string EmbeddedResourceName = "NanoClash.wintun.dll";

    public static void EnsureExtracted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var asm = typeof(WintunBootstrap).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
            return;

        byte[] bytes;
        if (stream is MemoryStream ms)
        {
            bytes = ms.ToArray();
        }
        else
        {
            using var buf = new MemoryStream();
            stream.CopyTo(buf);
            bytes = buf.ToArray();
        }

        if (bytes.Length == 0)
            return;

        Directory.CreateDirectory(AppPaths.UserDataDir);
        var path = AppPaths.WintunDll;
        try
        {
            if (File.Exists(path) && FilesEqual(path, bytes))
                return;

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // ignore extract failures
        }
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
