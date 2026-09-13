using System.Text;

namespace Clash.Config;

/// <summary>Persists subscription index at <c>config.yaml</c> and content under <c>data/</c>.</summary>
internal sealed class ProfileStore
{
    private readonly List<ProfileEntry> _profiles = [];
    private string? _defaultName;

    public ProfileStore()
    {
    }

    public IReadOnlyList<ProfileEntry> Profiles => _profiles;
    public string? DefaultName => _defaultName;

    public ProfileEntry? Active =>
        _profiles.FirstOrDefault(p => string.Equals(p.Name, _defaultName, StringComparison.Ordinal))
        ?? _profiles.FirstOrDefault();

    public void LoadOrMigrate()
    {
        _profiles.Clear();
        _defaultName = null;

        if (File.Exists(AppPaths.AppConfigYaml))
            LoadFromFile(AppPaths.AppConfigYaml);
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(_defaultName))
            sb.Append("default: ").Append(YamlEscape(_defaultName)).Append('\n');
        sb.Append("profiles:\n");
        foreach (var p in _profiles)
        {
            sb.Append("  - name: ").Append(YamlEscape(p.Name)).Append('\n');
            sb.Append("    hash: ").Append(p.Hash).Append('\n');
            sb.Append("    type: ").Append(p.Kind == ProfileKind.Cloud ? "cloud" : "local").Append('\n');
            if (p.Kind == ProfileKind.Cloud)
            {
                if (!string.IsNullOrEmpty(p.Url))
                    sb.Append("    url: ").Append(YamlEscape(p.Url)).Append('\n');
                if (p.UsedBytes is { } used)
                    sb.Append("    used: ").Append(used).Append('\n');
                if (p.TotalBytes is { } total and > 0)
                    sb.Append("    total: ").Append(total).Append('\n');
                if (p.ExpireUnix is { } exp and > 0)
                    sb.Append("    expire: ").Append(exp).Append('\n');
            }
        }

        File.WriteAllText(AppPaths.AppConfigYaml, sb.ToString(), Encoding.UTF8);
    }

    public void SetDefault(string name)
    {
        if (_profiles.All(p => !string.Equals(p.Name, name, StringComparison.Ordinal)))
            return;
        _defaultName = name;
        Save();
    }

    public ProfileEntry? FindByName(string name) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public bool NameExists(string name) =>
        _profiles.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>Returns preferred if free; otherwise preferred#1, preferred#2… (empty → Proxy).</summary>
    public string AllocateUniqueName(string? preferred)
    {
        var baseName = string.IsNullOrWhiteSpace(preferred) ? "Proxy" : preferred.Trim();
        if (!NameExists(baseName))
            return baseName;
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{baseName}#{i}";
            if (!NameExists(candidate))
                return candidate;
        }

        return $"{baseName}#{Guid.NewGuid():N}";
    }

    public ProfileEntry AddLocal(string name, byte[] content)
    {
        name = AllocateUniqueName(name);

        var hash = ContentStore.Save(content);
        var entry = new ProfileEntry
        {
            Name = name,
            Hash = hash,
            Kind = ProfileKind.Local,
        };
        _profiles.Add(entry);
        _defaultName = name;
        Save();
        return entry;
    }

    public ProfileEntry AddCloud(string name, string url, byte[] content, SubscriptionUserInfo? info)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("订阅地址不能为空");
        name = AllocateUniqueName(name);

        var hash = ContentStore.Save(content);
        var entry = new ProfileEntry
        {
            Name = name,
            Hash = hash,
            Kind = ProfileKind.Cloud,
            Url = url.Trim(),
        };
        ApplyUserInfo(entry, info);
        _profiles.Add(entry);
        _defaultName = name;
        Save();
        return entry;
    }

    public void UpdateCloud(ProfileEntry entry, byte[] content, SubscriptionUserInfo? info)
    {
        if (entry.Kind != ProfileKind.Cloud)
            throw new InvalidOperationException("本地订阅不支持更新");

        var oldHash = entry.Hash;
        var newHash = ContentStore.Save(content);
        entry.Hash = newHash;
        ApplyUserInfo(entry, info);
        Save();
        ContentStore.DeleteIfUnreferenced(oldHash, _profiles.Select(p => p.Hash));
    }

    /// <summary>
    /// Remove a profile by name. If it was default/active, select the next entry
    /// (same index after removal); if it was last, select the first.
    /// </summary>
    public bool Remove(string name)
    {
        var idx = _profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (idx < 0)
            return false;

        var oldHash = _profiles[idx].Hash;
        var wasDefault = string.Equals(_defaultName, name, StringComparison.Ordinal);
        _profiles.RemoveAt(idx);

        if (_profiles.Count == 0)
            _defaultName = null;
        else if (wasDefault || _defaultName is null ||
                 _profiles.All(p => !string.Equals(p.Name, _defaultName, StringComparison.Ordinal)))
            _defaultName = idx < _profiles.Count ? _profiles[idx].Name : _profiles[0].Name;

        Save();
        ContentStore.DeleteIfUnreferenced(oldHash, _profiles.Select(p => p.Hash));
        return true;
    }

    private static void ApplyUserInfo(ProfileEntry entry, SubscriptionUserInfo? info)
    {
        if (info is null)
            return;
        if (info.Upload is not null || info.Download is not null)
        {
            var used = (info.Upload ?? 0) + (info.Download ?? 0);
            entry.UsedBytes = used;
        }

        if (info.Total is not null)
            entry.TotalBytes = info.Total;
        if (info.ExpireUnix is not null)
            entry.ExpireUnix = info.ExpireUnix;
    }

    private void LoadFromFile(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            ProfileEntry? current = null;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#'))
                    continue;
                var line = raw.TrimEnd();
                if (line.StartsWith("default:", StringComparison.Ordinal))
                {
                    _defaultName = Unquote(line["default:".Length..].Trim());
                    continue;
                }

                if (line.TrimStart().StartsWith("- "))
                {
                    if (current is not null && !string.IsNullOrEmpty(current.Name) &&
                        !string.IsNullOrEmpty(current.Hash))
                        _profiles.Add(current);
                    current = new ProfileEntry { Name = "", Hash = "", Kind = ProfileKind.Local };
                    var rest = line.TrimStart()[2..].Trim();
                    if (rest.StartsWith("name:", StringComparison.Ordinal))
                        current.Name = Unquote(rest["name:".Length..].Trim());
                    continue;
                }

                if (current is null)
                    continue;

                var t = line.Trim();
                var colon = t.IndexOf(':');
                if (colon < 0)
                    continue;
                var key = t[..colon].Trim();
                var val = Unquote(t[(colon + 1)..].Trim());
                switch (key)
                {
                    case "name":
                        current.Name = val;
                        break;
                    case "hash":
                        current.Hash = val;
                        break;
                    case "type":
                        current.Kind = val.Equals("cloud", StringComparison.OrdinalIgnoreCase)
                            ? ProfileKind.Cloud
                            : ProfileKind.Local;
                        break;
                    case "url":
                        if (current.Kind == ProfileKind.Cloud)
                            current.Url = val;
                        break;
                    case "used":
                        if (current.Kind == ProfileKind.Cloud && long.TryParse(val, out var used))
                            current.UsedBytes = used;
                        break;
                    case "total":
                        if (current.Kind == ProfileKind.Cloud && long.TryParse(val, out var total))
                            current.TotalBytes = total;
                        break;
                    case "expire":
                        if (current.Kind == ProfileKind.Cloud && long.TryParse(val, out var exp))
                            current.ExpireUnix = exp;
                        break;
                }
            }

            if (current is not null && !string.IsNullOrEmpty(current.Name) &&
                !string.IsNullOrEmpty(current.Hash))
                _profiles.Add(current);

            // Strip traffic fields from local entries.
            foreach (var p in _profiles.Where(p => p.Kind == ProfileKind.Local))
            {
                p.Url = null;
                p.UsedBytes = null;
                p.TotalBytes = null;
                p.ExpireUnix = null;
            }

            if (_defaultName is null && _profiles.Count > 0)
                _defaultName = _profiles[0].Name;
        }
        catch
        {
            // ignore load errors
        }
    }

    private static string YamlEscape(string s)
    {
        if (s.Length == 0)
            return "\"\"";
        if (s.Contains(':') || s.Contains('#') || s.Contains('"') || s.Contains('\'') ||
            s.Contains('\n') || s.StartsWith(' ') || s.EndsWith(' '))
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return s;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return s;
    }
}
