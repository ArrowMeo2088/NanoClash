using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using Clash.Config;

namespace Clash.Gui;

internal sealed class AddProfileDialog
{
    private readonly ProfileStore _store;
    private readonly SubscriptionClient _client;
    private readonly Action _onAdded;

    private readonly ObservableValue<bool> _isCloud = new(true);
    private readonly ObservableValue<string> _name = new("");
    private readonly ObservableValue<string> _url = new("");
    private readonly ObservableValue<string> _localPath = new("");
    private readonly ObservableValue<string> _status = new("");
    private readonly ObservableValue<bool> _busy = new(false);

    private Window? _window;
    private Window? _owner;
    private TextBox? _urlBox;
    private Button? _pickBtn;
    private Label? _sourceLabel;

    public AddProfileDialog(
        ProfileStore store, SubscriptionClient client, Action onAdded)
    {
        _store = store;
        _client = client;
        _onAdded = onAdded;
    }

    public Window CreateWindow(Window? owner = null)
    {
        _owner = owner;

        _sourceLabel = new Label().Text("订阅地址");

        _urlBox = new TextBox()
            .BindText(_url)
            .Width(360);

        _pickBtn = new Button()
            .Content("选择文件")
            .Width(360)
            .OnClick(PickFile);
        _pickBtn.IsVisible = false;

        var cloudRadio = new RadioButton()
            .Content("云端订阅")
            .GroupName("kind")
            .IsChecked(true)
            .OnCheckedChanged(v =>
            {
                if (v)
                    SetKind(cloud: true);
            });
        var localRadio = new RadioButton()
            .Content("本地订阅")
            .GroupName("kind")
            .OnCheckedChanged(v =>
            {
                if (v)
                    SetKind(cloud: false);
            });

        var nameBox = new TextBox()
            .BindText(_name)
            .Width(360);

        var okBtn = new Button()
            .Content("确定")
            .OnClick(() => _ = ConfirmAsync());

        var cancelBtn = new Button()
            .Content("取消")
            .OnClick(() => _window?.Close());

        // One source row: cloud → URL textbox; local → same slot becomes「选择文件」.
        var sourceSlot = new StackPanel()
            .Vertical()
            .Spacing(6)
            .Children(
                _sourceLabel,
                _urlBox,
                _pickBtn
            );

        _window = new Window()
            .Title("添加订阅")
            .Fixed(440, 260)
            .CanMaximize(false)
            .StartCenterScreen()
            .Padding(16)
            .Content(
                new StackPanel()
                    .Vertical()
                    .Spacing(10)
                    .Children(
                        new StackPanel()
                            .Horizontal()
                            .Spacing(16)
                            .Children(cloudRadio, localRadio),
                        new Label().Text("订阅名称（可留空）"),
                        nameBox,
                        sourceSlot,
                        new Label().BindText(_status),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(10)
                            .Children(okBtn, cancelBtn)
                    )
            );

        SetKind(cloud: true);
        return _window;
    }

    private void SetKind(bool cloud)
    {
        _isCloud.Value = cloud;
        if (_urlBox is not null)
            _urlBox.IsVisible = cloud;
        if (_pickBtn is not null)
            _pickBtn.IsVisible = !cloud;
        RefreshPickButtonLabel();
    }

    private void RefreshPickButtonLabel()
    {
        if (_pickBtn is null)
            return;
        var path = _localPath.Value.Trim();
        _pickBtn.Content(string.IsNullOrEmpty(path)
            ? "选择文件"
            : Path.GetFileName(path));
    }

    private void PickFile()
    {
        try
        {
            var files = FileDialog.OpenFiles(new OpenFileDialogOptions
            {
                Owner = _window ?? _owner,
                Filters = FileFilter.Parse(
                    "Config (*.yaml;*.yml;*.json;*.conf;*.txt)|*.yaml;*.yml;*.json;*.conf;*.txt|All Files (*.*)|*.*"),
            });
            if (files is { Length: > 0 })
            {
                _localPath.Value = files[0];
                RefreshPickButtonLabel();
            }
        }
        catch (Exception ex)
        {
            _status.Value = "选择文件失败: " + ex.Message;
        }
    }

    private async Task ConfirmAsync()
    {
        if (_busy.Value)
            return;

        _busy.Value = true;
        _status.Value = "处理中…";
        try
        {
            var typedName = _name.Value.Trim();
            if (_isCloud.Value)
            {
                var url = _url.Value.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    _status.Value = "请输入订阅地址";
                    return;
                }

                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _status.Value = "订阅地址仅支持 http/https";
                    return;
                }

                var fetched = await _client.FetchAsync(url, CancellationToken.None).ConfigureAwait(true);
                var name = string.IsNullOrEmpty(typedName)
                    ? (fetched.SuggestedName ?? "Proxy")
                    : typedName;
                _store.AddCloud(name, url, fetched.Body, fetched.UserInfo);
            }
            else
            {
                var path = _localPath.Value.Trim();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _status.Value = "请选择有效的本地文件";
                    return;
                }

                var info = new FileInfo(path);
                if (info.Length > SubscriptionClient.MaxBodyBytes)
                {
                    _status.Value = "本地文件超过 16 MB";
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
                var name = string.IsNullOrEmpty(typedName)
                    ? Path.GetFileNameWithoutExtension(path)
                    : typedName;
                if (string.IsNullOrWhiteSpace(name))
                    name = "Proxy";
                _store.AddLocal(name, bytes);
            }

            _onAdded();
            _window?.Close();
        }
        catch (Exception ex)
        {
            _status.Value = "失败: " + ex.Message;
        }
        finally
        {
            _busy.Value = false;
        }
    }
}
