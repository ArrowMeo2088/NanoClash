using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using Clash.Config;
using Clash.Inbound;
using Clash.Outbound;
using Clash.Tun;

namespace Clash.Gui;

internal sealed partial class MainView
{
    private static IconSource? TryLoadAppIcon()
    {
        try
        {
            // Embedded at build time (ApplicationIcon still sets the Windows PE icon).
            var asm = typeof(MainView).Assembly;
            using var stream = asm.GetManifestResourceStream("NanoClash.icon.ico");
            if (stream is not null)
                return IconSource.FromStream(stream);
        }
        catch
        {
            // ignore missing/invalid icon
        }

        return null;
    }

    private static Border ModeBox(params Element[] children) =>
        new Border()
            .Padding(8, 4)
            .CornerRadius(6)
            .BorderThickness(1)
            .WithTheme((t, b) =>
            {
                b.BorderBrush(t.Palette.ControlBorder);
                b.Background(t.Palette.ControlBackground);
            })
            .Child(
                new StackPanel()
                    .Horizontal()
                    .Spacing(4)
                    .CenterVertical()
                    .Children(children)
            );

    private static void ApplyOnOffColor(Label label, bool on) =>
        label.WithTheme((_, c) => c.Foreground(
            on ? Color.FromRgb(80, 200, 120) : Color.FromRgb(220, 80, 80)));

    private static void ApplyDisabledColor(Label label) =>
        label.WithTheme((_, c) => c.Foreground(Color.FromRgb(150, 150, 150)));

    private void OpenAddDialog()
    {
        var dlg = new AddProfileDialog(_profiles, _client, () =>
        {
            RefreshProfileCombo();
            ReloadFromActiveProfile();
        });
        var w = dlg.CreateWindow(_window);
        w.Show();
    }

    private void RefreshProfileCombo()
    {
        if (_profileCombo is null)
            return;
        _suppressProfileSelect = true;
        try
        {
            var names = _profiles.Profiles.Select(p => p.Name).ToList();
            _profileCombo.Items(names, n => n);
            var active = _profiles.DefaultName;
            if (active is not null && names.Contains(active))
                _profileCombo.SelectedItem = active;
            else if (names.Count > 0)
                _profileCombo.SelectedItem = names[0];
        }
        finally
        {
            _suppressProfileSelect = false;
        }

        UpdateCloudChrome();
    }

    private void SwitchProfile(string name)
    {
        _profiles.SetDefault(name);
        ReloadFromActiveProfile();
    }

    private void ReloadFromActiveProfile()
    {
        if (_batchRunning)
        {
            _batchCts?.Cancel();
            _batchRunning = false;
            _healthBtnText.Value = "健康检查";
        }

        _ordered.Clear();
        _cardBorders.Clear();
        _selected = null;

        var active = _profiles.Active;
        if (active is not null)
        {
            var bytes = ContentStore.TryRead(active.Hash);
            if (bytes is not null)
            {
                var nodes = NodeCatalog.Parse(bytes);
                foreach (var n in nodes)
                    _ordered.Add(new NodeCardVm(n));
            }
        }

        _selected = _ordered.FirstOrDefault(c => !c.Node.IsSubscriptionInfo) ?? _ordered.FirstOrDefault();

        if (_tun.IsRunning)
        {
            _ = ReloadProfileWithTunAsync(_selected?.Node);
        }
        else
        {
            if (_selected is not null)
                _outbound.SetCurrent(_selected.Node);
            else
                _outbound.SetCurrent(null);
            _proxy.AbortActiveConnections();
        }

        RebuildWrapOrder();
        UpdateCloudChrome();
    }

    private async Task ReloadProfileWithTunAsync(ProxyNode? node)
    {
        var epoch = Interlocked.Increment(ref _tunNodeSwitchEpoch);
        var ok = false;
        try
        {
            ok = await _tun.OnNodeChangedAsync(node).ConfigureAwait(false);
        }
        catch
        {
            // ignore TUN route update failure
        }

        if (epoch != Volatile.Read(ref _tunNodeSwitchEpoch))
            return;

        if (!ok)
            return;


        if (node is not null)
            _outbound.SetCurrent(node);
        else
            _outbound.SetCurrent(null);
        _proxy.AbortActiveConnections();
    }

    private void UpdateCloudChrome()
    {
        var active = _profiles.Active;
        var cloud = active?.Kind == ProfileKind.Cloud;
        var hasProfile = active is not null;
        _updateVisible.Value = cloud;
        _deleteVisible.Value = hasProfile;
        _cloudMetaVisible.Value = cloud;
        if (_updateBtn is not null)
            _updateBtn.IsVisible = cloud;
        if (_deleteBtn is not null)
            _deleteBtn.IsVisible = hasProfile;
        if (_quotaPanel is not null)
            _quotaPanel.IsVisible = cloud;

        if (!cloud || active is null)
        {
            _quotaText.Value = "";
            _expireText.Value = "";
            return;
        }

        _quotaText.Value = FormatQuota(active.UsedBytes, active.TotalBytes);
        _expireText.Value = FormatExpire(active.ExpireUnix);
    }

    private void DeleteActiveProfile()
    {
        var active = _profiles.Active;
        if (active is null)
            return;

        try
        {
            if (!_profiles.Remove(active.Name))
                return;
            RefreshProfileCombo();
            ReloadFromActiveProfile();
        }
        catch
        {
            // ignore delete failure
        }
    }

    private static string FormatQuota(long? used, long? total)
    {
        if (used is null && total is null)
            return "-";
        var usedStr = used is null ? "-" : FormatBytes(used.Value);
        if (total is null or 0)
            return $"{usedStr}/不限";
        var pct = used is null ? 0.0 : Math.Clamp(used.Value * 100.0 / total.Value, 0, 100);
        return $"{pct:0.00}%  {usedStr}/{FormatBytes(total.Value)}";
    }

    private static string FormatBytes(long bytes)
    {
        const double gb = 1_000_000_000.0;
        const double mb = 1_000_000.0;
        if (bytes >= gb)
            return $"{bytes / gb:0.00} GB";
        if (bytes >= mb)
            return $"{bytes / mb:0.00} MB";
        return $"{bytes / 1000.0:0.00} KB";
    }

    private static string FormatExpire(long? unix)
    {
        if (unix is null or <= 0)
            return "到期 -";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unix.Value).LocalDateTime;
            return "到期 " + dt.ToString("yyyy-MM-dd");
        }
        catch
        {
            return "到期 -";
        }
    }

    private async Task UpdateActiveAsync()
    {
        var active = _profiles.Active;
        if (active is null || active.Kind != ProfileKind.Cloud || string.IsNullOrEmpty(active.Url))
            return;

        try
        {
            var fetched = await _client.FetchAsync(active.Url, CancellationToken.None).ConfigureAwait(true);
            _profiles.UpdateCloud(active, fetched.Body, fetched.UserInfo);
            ReloadFromActiveProfile();
            RefreshProfileCombo();
        }
        catch
        {
            // ignore update failure
        }
    }
}

