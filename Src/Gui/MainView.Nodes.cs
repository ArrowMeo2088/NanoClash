using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using Clash.Config;
using Clash.Inbound;
using Clash.Outbound;
using Clash.Tun;

namespace Clash.Gui;

internal sealed partial class MainView
{
    private WrapPanel BuildWrapPanel()
    {
        var cards = new Element[_ordered.Count];
        for (var i = 0; i < _ordered.Count; i++)
        {
            var vm = _ordered[i];
            if (!_cardBorders.TryGetValue(vm, out var border))
                border = BuildCard(vm);
            cards[i] = border;
        }

        return new WrapPanel()
            .Orientation(Orientation.Horizontal)
            .Spacing(CardGap)
            .ItemWidth(CardWidth)
            .ItemHeight(CardHeight)
            .Width(ListWidth)
            .Children(cards);
    }

    private Border BuildCard(NodeCardVm vm)
    {
        var name = new TextBlock()
            .Text(vm.Name)
            .FontSize(CardFontSize);

        var proto = new TextBlock()
            .Text(vm.Protocol)
            .FontSize(CardFontSize);

        var latency = new Label()
            .BindText(vm.LatencyText)
            .FontSize(CardFontSize)
            .MinWidth(36)
            .TextAlignment(TextAlignment.Right)
            .CenterVertical();

        var border = new Border()
            .Padding(8, 4)
            .CornerRadius(6)
            .BorderThickness(1)
            .WithTheme((t, b) =>
            {
                b.BorderBrush(ReferenceEquals(vm, _selected) ? t.Palette.Accent : t.Palette.ControlBorder);
                b.Background(t.Palette.ControlBackground);
            })
            .OnMouseDown(_ => SelectCard(vm))
            .Child(
                new DockPanel()
                    .LastChildFill()
                    .Children(
                        latency.DockRight(),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .CenterVertical()
                            .Children(name, proto)
                    )
            );

        _cardBorders[vm] = border;
        return border;
    }

    private void SelectCard(NodeCardVm vm)
    {
        if (vm.Node.IsSubscriptionInfo)
            return;

        var switched = !ReferenceEquals(_selected, vm);
        var previous = _selected;
        _selected = vm;
        RefreshSelectionBorders();

        if (!switched)
            return;

        if (_tun.IsRunning)
        {
            // Route /32 must be ready before SetCurrent — otherwise new node dials loop into TUN.
            _ = SwitchNodeWithTunAsync(vm, previous);
            return;
        }

        _outbound.SetCurrent(vm.Node);
        _proxy.AbortActiveConnections();
    }

    private async Task SwitchNodeWithTunAsync(NodeCardVm vm, NodeCardVm? previous)
    {
        var epoch = Interlocked.Increment(ref _tunNodeSwitchEpoch);
        var ok = false;
        try
        {
            ok = await _tun.OnNodeChangedAsync(vm.Node).ConfigureAwait(false);
        }
        catch
        {
            // ignore TUN route update failure
        }

        // A newer click/reload superseded this operation — do not clobber Current/selection.
        if (epoch != Volatile.Read(ref _tunNodeSwitchEpoch))
            return;

        if (!ok)
        {
            RunOnUi(() =>
            {
                if (ReferenceEquals(_selected, vm))
                {
                    _selected = previous;
                    RefreshSelectionBorders();
                }
            });
            return;
        }

        _outbound.SetCurrent(vm.Node);
        _proxy.AbortActiveConnections();
    }

    private async Task ToggleProxyModeAsync(bool enable)
    {
        try
        {
            if (enable && _enhanceMode.Value)
            {
                _suppressEnhanceMode = true;
                try
                {
                    _enhanceMode.Value = false;
                }
                finally
                {
                    _suppressEnhanceMode = false;
                }

                // Must tear down TUN routes before enabling system proxy (avoid dual hijack).
                // Never GetResult on the UI thread — netsh DNS restore can take seconds.
                try
                {
                    await _tun.StopAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            _proxy.SetSystemProxy(enable);
        }
        catch
        {
            RunOnUi(() =>
            {
                _suppressProxyMode = true;
                try
                {
                    _proxyMode.Value = !enable;
                }
                finally
                {
                    _suppressProxyMode = false;
                }
            });
        }
    }

    private async Task ToggleEnhanceAsync(bool enable)
    {
        try
        {
            if (enable)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10))
                    throw new PlatformNotSupportedException("增强模式仅支持 Windows 10/11");

                if (_proxyMode.Value)
                {
                    _suppressProxyMode = true;
                    try
                    {
                        _proxyMode.Value = false;
                    }
                    finally
                    {
                        _suppressProxyMode = false;
                    }

                    try
                    {
                        _proxy.SetSystemProxy(false);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                await _tun.StartAsync().ConfigureAwait(false);
            }
            else
            {
                await _tun.StopAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                await _tun.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore cleanup
            }

            RunOnUi(() =>
            {
                _suppressEnhanceMode = true;
                try
                {
                    _enhanceMode.Value = !enable;
                }
                finally
                {
                    _suppressEnhanceMode = false;
                }
            });
        }
    }

    private void RefreshSelectionBorders()
    {
        foreach (var (vm, border) in _cardBorders)
        {
            border.WithTheme((t, b) =>
            {
                b.BorderBrush(ReferenceEquals(vm, _selected) ? t.Palette.Accent : t.Palette.ControlBorder);
                b.Background(t.Palette.ControlBackground);
            });
        }
    }

    private void RebuildWrapOrder()
    {
        if (_scroll is null)
            return;
        _scroll.Content(BuildWrapPanel());
        RefreshSelectionBorders();
    }
    private async Task ToggleBatchHealthAsync()
    {
        if (_batchRunning)
        {
            _batchCts?.Cancel();
            return;
        }

        if (_ordered.Count == 0)
            return;

        _batchRunning = true;
        _healthBtnText.Value = "取消检查";
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        var gate = new SemaphoreSlim(HealthChecker.MaxConcurrency, HealthChecker.MaxConcurrency);

        try
        {
            var tasks = _ordered.Where(vm => !vm.Node.IsSubscriptionInfo).Select(async vm =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    RunOnUi(vm.SetChecking);
                    var result = await _health.CheckAsync(vm.Node, ct).ConfigureAwait(false);
                    RunOnUi(() => vm.Apply(result));
                }
                catch (OperationCanceledException)
                {
                    RunOnUi(vm.SetCancelled);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancelled
        }
        finally
        {
            RunOnUi(() =>
            {
                ResortByLatency();
                _batchRunning = false;
                _healthBtnText.Value = "健康检查";
            });
            _batchCts.Dispose();
            _batchCts = null;
        }
    }

    private void ResortByLatency()
    {
        var selected = _selected;
        var ordered = _ordered
            .OrderBy(c => c.Node.IsSubscriptionInfo ? 1 : 0)
            .ThenByDescending(c => c.LatencyMs.HasValue)
            .ThenBy(c => c.LatencyMs ?? int.MaxValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _ordered.Clear();
        _ordered.AddRange(ordered);
        RebuildWrapOrder();
        if (selected is not null)
            _selected = selected;
    }
}

