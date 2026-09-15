namespace Clash;

internal static class AppPaths
{
    public static string BaseDir { get; } = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Per-user data root: <c>%APPDATA%\ArrowMeo\NanoClash</c> (Windows) /
    /// <c>~/.config/ArrowMeo/NanoClash</c> (Linux) / Application Support equivalent (macOS).
    /// </summary>
    public static string UserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArrowMeo",
        "NanoClash");

    /// <summary>Optional sidecar beside the exe (dev override; uncompressed CFWR).</summary>
    public static string RulesBin => Path.Combine(BaseDir, "Rules.bin");

    /// <summary>Runtime-extracted rules under the per-user data root.</summary>
    public static string UserRulesBin => Path.Combine(UserDataDir, "Rules.bin");

    public static string DataDir => Path.Combine(UserDataDir, "data");
    public static string AppConfigYaml => Path.Combine(UserDataDir, "config.yaml");
    public static string WintunDll => Path.Combine(UserDataDir, "wintun.dll");
    /// <summary>Crash-recovery snapshot of system proxy before NanoClash applied 127.0.0.1:7887.</summary>
    public static string ProxyUndoFile => Path.Combine(UserDataDir, "proxy-undo.json");

    /// <summary>
    /// Create user-data dirs; one-shot migrate <c>config.yaml</c> + <c>data/</c> from the exe folder
    /// when the AppData copy is missing.
    /// </summary>
    public static void EnsureUserData()
    {
        Directory.CreateDirectory(UserDataDir);
        Directory.CreateDirectory(DataDir);

        if (File.Exists(AppConfigYaml))
            return;

        var legacyConfig = Path.Combine(BaseDir, "config.yaml");
        if (!File.Exists(legacyConfig))
            return;

        try
        {
            File.Copy(legacyConfig, AppConfigYaml, overwrite: false);
            var legacyData = Path.Combine(BaseDir, "data");
            if (!Directory.Exists(legacyData))
                return;

            foreach (var src in Directory.EnumerateFiles(legacyData))
            {
                var name = Path.GetFileName(src);
                if (string.IsNullOrEmpty(name))
                    continue;
                var dest = Path.Combine(DataDir, name);
                if (!File.Exists(dest))
                    File.Copy(src, dest);
            }
        }
        catch
        {
            // Best-effort; ProfileStore will still start empty if migrate fails.
        }
    }
}
