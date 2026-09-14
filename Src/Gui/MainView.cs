using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using Clash.Config;
using Clash.Inbound;
using Clash.Outbound;
using Clash.Tun;

namespace Clash.Gui;

internal sealed partial class MainView
{
    private const double CardHeight = 36;
    private const double CardWidth = 214;
    private const double CardGap = 8;
    private const int Columns = 3;
    private const int VisibleRows = 6;
    private const double CardFontSize = 12;
    private const double ListWidth = CardWidth * Columns + CardGap * (Columns - 1);
    private const double ListMinHeight = CardHeight * VisibleRows + CardGap * (VisibleRows - 1);
    private const double ContentWidth = ListWidth + 16;
    private const double WindowPadH = 12;

    private readonly ProxyService _proxy;
    private readonly OutboundDialer _outbound;
    private readonly TunService _tun;
    private readonly HealthChecker _health;
    private readonly ProfileStore _profiles;
    private readonly SubscriptionClient _client;

    private readonly List<NodeCardVm> _ordered = [];
    private readonly Dictionary<NodeCardVm, Border> _cardBorders = [];

    private readonly ObservableValue<bool> _proxyMode = new(false);
    private readonly ObservableValue<bool> _enhanceMode = new(false);
    private readonly ObservableValue<string> _proxyStateText = new("关闭");
    private readonly ObservableValue<string> _enhanceStateText = new("关闭");
    private readonly ObservableValue<string> _speedText = new("传输速度 0 KB/s");
    private readonly ObservableValue<string> _errorText = new("");
    private readonly ObservableValue<string> _healthBtnText = new("健康检查");
    private readonly ObservableValue<string> _quotaText = new("");
    private readonly ObservableValue<string> _expireText = new("");
    private readonly ObservableValue<bool> _cloudMetaVisible = new(false);
    private readonly ObservableValue<bool> _updateVisible = new(false);
    private readonly ObservableValue<bool> _deleteVisible = new(false);

    private DispatcherTimer? _timer;
    private CancellationTokenSource? _batchCts;
    private bool _batchRunning;
    private bool _suppressProxyMode;
    private bool _suppressEnhanceMode;
    private bool _suppressProfileSelect;
    private ScrollViewer? _scroll;
    private ComboBox? _profileCombo;
    private Window? _window;
    private NodeCardVm? _selected;
    private Button? _updateBtn;
    private Button? _deleteBtn;
    private StackPanel? _quotaPanel;
    private int _tunNodeSwitchEpoch;

    public MainView(
        ProxyService proxy,
        OutboundDialer outbound,
        TunService tun,
        ProfileStore profiles,
        SubscriptionClient client)
    {
        _proxy = proxy;
        _outbound = outbound;
        _tun = tun;
        _profiles = profiles;
        _client = client;
        _health = new HealthChecker(outbound);
    }

    public Window CreateWindow()
    {
        _proxyMode.Changed += () =>
        {
            _proxyStateText.Value = _proxyMode.Value ? "开启" : "关闭";
            if (_suppressProxyMode)
                return;
            _ = ToggleProxyModeAsync(_proxyMode.Value);
        };

        _enhanceMode.Changed += () =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            {
                _enhanceStateText.Value = "不可用";
                return;
            }

            _enhanceStateText.Value = _enhanceMode.Value ? "开启" : "关闭";
            if (_suppressEnhanceMode)
                return;
            _ = ToggleEnhanceAsync(_enhanceMode.Value);
        };

        _scroll = new ScrollViewer()
            .Width(ContentWidth)
            .MinHeight(ListMinHeight)
            .AutoVerticalScroll()
            .NoHorizontalScroll()
            .Content(BuildWrapPanel());

        _profileCombo = new ComboBox()
            .Width(160)
            .MinWidth(120);

        RefreshProfileCombo();
        _profileCombo.OnSelectionChanged(_ =>
        {
            if (_suppressProfileSelect)
                return;
            if (_profileCombo.SelectedItem is string name)
                SwitchProfile(name);
        });

        _updateBtn = new Button()
            .Content("更新订阅")
            .OnClick(() => _ = UpdateActiveAsync());

        _deleteBtn = new Button()
            .Content("删除")
            .OnClick(DeleteActiveProfile);

        _quotaPanel = new StackPanel()
            .Horizontal()
            .Spacing(10)
            .CenterVertical()
            .Children(
                new Label().BindText(_quotaText).FontSize(12),
                new Label().BindText(_expireText).FontSize(12)
            );

        var proxyState = new Label()
            .BindText(_proxyStateText)
            .FontSize(13);
        ApplyOnOffColor(proxyState, _proxyMode.Value);
        _proxyMode.Changed += () => ApplyOnOffColor(proxyState, _proxyMode.Value);

        // Enhance (TUN) is Windows 10+ only — gray out on Linux/macOS.
        var enhanceSupported = OperatingSystem.IsWindowsVersionAtLeast(10);
        var enhanceTitle = new Label().Text("增强模式").FontSize(13);
        var enhanceToggle = new ToggleSwitch()
            .BindIsChecked(_enhanceMode)
            .IsEnabled(enhanceSupported);
        var enhanceState = new Label()
            .BindText(_enhanceStateText)
            .FontSize(13);
        if (enhanceSupported)
        {
            ApplyOnOffColor(enhanceState, _enhanceMode.Value);
            _enhanceMode.Changed += () => ApplyOnOffColor(enhanceState, _enhanceMode.Value);
        }
        else
        {
            _enhanceStateText.Value = "不可用";
            ApplyDisabledColor(enhanceTitle);
            ApplyDisabledColor(enhanceState);
        }

        var proxyBox = ModeBox(
            new Label().Text("代理模式").FontSize(13),
            new ToggleSwitch().BindIsChecked(_proxyMode),
            proxyState);

        var enhanceBox = ModeBox(enhanceTitle, enhanceToggle, enhanceState);
        if (!enhanceSupported)
            enhanceBox.IsEnabled(false);

        var speedLabel = new Label()
            .BindText(_speedText)
            .FontSize(13)
            .TextAlignment(TextAlignment.Right)
            .CenterVertical();
        var errorLabel = new Label()
            .BindText(_errorText)
            .FontSize(11)
            .Foreground(Color.FromRgb(200, 80, 80))
            .TextAlignment(TextAlignment.Right)
            .CenterVertical();

        var topChrome = new StackPanel()
            .Width(ContentWidth)
            .Vertical()
            .Spacing(8)
            .Children(
                new DockPanel()
                    .Width(ContentWidth)
                    .LastChildFill()
                    .Children(
                        _quotaPanel.DockRight(),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .CenterVertical()
                            .Children(
                                new Button()
                                    .Content("添加订阅")
                                    .OnClick(OpenAddDialog),
                                _profileCombo,
                                _updateBtn,
                                _deleteBtn
                            )
                    ),
                new DockPanel()
                    .Width(ContentWidth)
                    .LastChildFill()
                    .Children(
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .DockRight()
                            .Children(errorLabel, speedLabel),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(18)
                            .CenterVertical()
                            .Children(
                                new Button()
                                    .BindContent(_healthBtnText)
                                    .OnClick(() => _ = ToggleBatchHealthAsync()),
                                proxyBox,
                                enhanceBox
                            )
                    )
            );

        var winW = ContentWidth + WindowPadH * 2 + 16;
        const double winH = 460;
        var window = new Window()
            .Title("NanoClash")
            .Fixed(winW, winH)
            .CanMaximize(false)
            .StartCenterScreen()
            .Padding(WindowPadH, 12, WindowPadH, 8)
            .OnLoaded(OnLoaded)
            .OnClosed(OnClosed)
            .Content(
                new DockPanel()
                    .Width(ContentWidth)
                    .LastChildFill()
                    .Spacing(10)
                    .Children(
                        topChrome.DockTop(),
                        _scroll
                    )
            );

        var icon = TryLoadAppIcon();
        if (icon is not null)
            window.Icon(icon);

        _window = window;
        ReloadFromActiveProfile();
        return _window;
    }


    private void OnLoaded()
    {
        RefreshSelectionBorders();
        UpdateCloudChrome();
        _timer = new DispatcherTimer()
            .Interval(TimeSpan.FromSeconds(1))
            .OnTick(RefreshStats);
        _timer.Start();
    }

    private void OnClosed()
    {
        try
        {
            _batchCts?.Cancel();
            _timer?.Stop();
            _timer?.Dispose();
            _proxy.SetSystemProxy(false);
            SystemProxy.ForceRestore();
            // TUN/proxy full stop is owned by Program window.OnClosed + finally
            // (avoid double StopAsync on the UI thread).
        }
        catch
        {
            // ignore
        }
    }

    private void RefreshStats()
    {
        _proxy.PollStats();
        _speedText.Value = $"传输速度 {_proxy.TotalSpeedKBps} KB/s";
        if (!string.IsNullOrEmpty(_proxy.LastError))
            ShowError(_proxy.LastError);
    }

    private void ShowError(string message)
    {
        _errorText.Value = message;
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(action);
        else
            action();
    }
}
