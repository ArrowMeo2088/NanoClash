using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Clash.Inbound;

/// <summary>WinInet per-user HTTP(S) proxy → 127.0.0.1:7887.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSystemProxy : ISystemProxy
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private const string ProxyServerValue = "127.0.0.1:7887";

    private bool _applied;
    private int? _prevEnable;
    private string? _prevServer;
    private string? _prevOverride;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            Apply();
        else
            Restore();
    }

    public void ForceRestore()
    {
        if (_applied)
        {
            Restore();
            return;
        }

        // Crash recovery: in-process flag lost, but undo file + still pointing at us.
        TryRecoverFromDisk();
    }

    /// <summary>Startup: restore WinINET if a previous run died with proxy left on.</summary>
    public static void RecoverOrphanedProxy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        new WindowsSystemProxy().TryRecoverFromDisk();
    }

    private void TryRecoverFromDisk()
    {
        if (!TryLoadUndo(out var enable, out var server, out var ov))
            return;

        var current = ReadString(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer");
        var enabled = ReadDword(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable") ?? 0;
        if (enabled != 1 ||
            current is null ||
            !current.Contains("127.0.0.1:7887", StringComparison.OrdinalIgnoreCase))
        {
            ClearUndoFile();
            return;
        }

        _prevEnable = enable;
        _prevServer = server;
        _prevOverride = ov;
        _applied = true;
        Restore();
    }

    private void Apply()
    {
        if (_applied)
            return;

        _prevEnable = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable");
        _prevServer = ReadString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer");
        _prevOverride = ReadString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyOverride");
        WriteUndoFile(_prevEnable, _prevServer, _prevOverride);

        try
        {
            WriteDword(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", 1);
            WriteString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer", ProxyServerValue);
            WriteString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyOverride",
                "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;169.254.*;<local>");

            NotifyWinInet();
            _applied = true;
        }
        catch
        {
            try
            {
                if (_prevEnable is int pe)
                    WriteDword(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", pe);
                else
                    DeleteValue(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable");

                if (_prevServer is not null)
                    WriteString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer", _prevServer);
                else
                    DeleteValue(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer");

                if (_prevOverride is not null)
                    WriteString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyOverride", _prevOverride);
                else
                    DeleteValue(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyOverride");
            }
            catch
            {
                // ignore nested rollback failures
            }

            ClearUndoFile();
            _prevEnable = null;
            _prevServer = null;
            _prevOverride = null;
            _applied = false;
            throw;
        }
    }

    private void Restore()
    {
        if (!_applied)
            return;

        if (_prevEnable is int pe)
            WriteDword(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", pe);
        else
            DeleteValue(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable");

        if (_prevServer is not null)
            WriteString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer", _prevServer);
        else
            DeleteValue(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer");

        if (_prevOverride is not null)
            WriteString(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyOverride", _prevOverride);
        else
            DeleteValue(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyOverride");

        NotifyWinInet();
        ClearUndoFile();
        _applied = false;
        _prevEnable = null;
        _prevServer = null;
        _prevOverride = null;
    }

    private static void WriteUndoFile(int? enable, string? server, string? ov)
    {
        // Hand-written JSON — AOT-safe, no reflection.
        static string Esc(string? s)
        {
            if (s is null)
                return "null";
            return "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
        }

        var enableJson = enable is int e ? e.ToString() : "null";
        var json = $"{{\"enable\":{enableJson},\"server\":{Esc(server)},\"override\":{Esc(ov)}}}";
        File.WriteAllText(AppPaths.ProxyUndoFile, json, Encoding.UTF8);
    }

    private static bool TryLoadUndo(out int? enable, out string? server, out string? ov)
    {
        enable = null;
        server = null;
        ov = null;
        try
        {
            if (!File.Exists(AppPaths.ProxyUndoFile))
                return false;
            var text = File.ReadAllText(AppPaths.ProxyUndoFile);
            if (!TryParseUndoJson(text, out enable, out server, out ov))
            {
                ClearUndoFile();
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseUndoJson(
        string text, out int? enable, out string? server, out string? ov)
    {
        enable = null;
        server = null;
        ov = null;
        // Minimal parser for {"enable":N|null,"server":"... "|null,"override":"... "|null}
        if (!TryExtractJsonField(text, "enable", out var enRaw))
            return false;
        if (enRaw != "null" && int.TryParse(enRaw, out var enVal))
            enable = enVal;
        else if (enRaw != "null")
            return false;

        if (!TryExtractJsonStringOrNull(text, "server", out server))
            return false;
        if (!TryExtractJsonStringOrNull(text, "override", out ov))
            return false;
        return true;
    }

    private static bool TryExtractJsonField(string text, string name, out string raw)
    {
        raw = "";
        var key = "\"" + name + "\"";
        var i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return false;
        i = text.IndexOf(':', i + key.Length);
        if (i < 0)
            return false;
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length)
            return false;
        if (text[i] == '"')
            return false;
        var start = i;
        while (i < text.Length && text[i] is not (',' or '}' or ' '))
            i++;
        raw = text[start..i].Trim();
        return raw.Length > 0;
    }

    private static bool TryExtractJsonStringOrNull(string text, string name, out string? value)
    {
        value = null;
        var key = "\"" + name + "\"";
        var i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return false;
        i = text.IndexOf(':', i + key.Length);
        if (i < 0)
            return false;
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length)
            return false;
        if (text.AsSpan(i).StartsWith("null", StringComparison.Ordinal))
            return true;
        if (text[i] != '"')
            return false;
        i++;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '\\' && i < text.Length)
            {
                var n = text[i++];
                sb.Append(n switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => n,
                });
                continue;
            }

            if (c == '"')
            {
                value = sb.ToString();
                return true;
            }

            sb.Append(c);
        }

        return false;
    }

    private static void ClearUndoFile()
    {
        try
        {
            if (File.Exists(AppPaths.ProxyUndoFile))
                File.Delete(AppPaths.ProxyUndoFile);
        }
        catch
        {
            // ignore
        }
    }

    private static void NotifyWinInet()
    {
        if (!InternetSetOptionW(0, InternetOptionSettingsChanged, 0, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "InternetSetOptionW(SETTINGS_CHANGED)");
        if (!InternetSetOptionW(0, InternetOptionRefresh, 0, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "InternetSetOptionW(REFRESH)");
    }

    private static int? ReadDword(string subKey, string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        var v = key?.GetValue(name);
        return v is int i ? i : null;
    }

    private static string? ReadString(string subKey, string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    private static void WriteDword(string subKey, string name, int value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
    }

    private static void WriteString(string subKey, string name, string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }

    private static void DeleteValue(string subKey, string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey);
        try
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // ignore
        }
    }

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOptionW(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);
}
