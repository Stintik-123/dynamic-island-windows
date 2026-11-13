using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Services;
using Windows.Devices.Radios;

namespace DynamicIsland;

/// <summary>
/// Control centre (top of the System page): Wi-Fi, Bluetooth, airplane, VPN, clean desktop and power,
/// and, with the apps row on Home, lets the island stand in for the desktop clutter.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _controlTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, (Border Circle, TextBlock Sub, Color On)> _toggles = [];
    private bool _controlBusy;

    private void InitControlCenter()
    {
        AddToggle("wifi", "", L.T("Wi‑Fi"), Color.FromRgb(0x0A, 0x84, 0xFF), async () => await ToggleRadio(RadioKind.WiFi));
        AddToggle("bt", "", "Bluetooth", Color.FromRgb(0x0A, 0x84, 0xFF), async () => await ToggleRadio(RadioKind.Bluetooth));
        AddToggle("air", "", L.T("Режим полёта"), Color.FromRgb(0xFF, 0x9F, 0x0A), async () =>
        {
            var on = await ControlCenter.AirplaneAsync();
            if (on != null) await ControlCenter.SetAirplaneAsync(!on.Value);
        });
        AddToggle("vpn", "", "VPN", Color.FromRgb(0x30, 0xD1, 0x58), ToggleVpn);
        AddToggle("clean", "", L.T("Чистый стол"), Color.FromRgb(0xBF, 0x5A, 0xF2), () =>
        {
            SetCleanDesktop(!_settings.CleanDesktop);
            return Task.CompletedTask;
        });

        AddPower("", L.T("Блокировка"), () => Native.LockWorkStation());
        AddPower("", L.T("Сон"), ControlCenter.Sleep);
        AddPower("", L.T("Перезагрузка"), () => { if (ConfirmPower(L.T("Перезагрузить компьютер?"))) ControlCenter.Restart(); });
        AddPower("", L.T("Выключение"), () => { if (ConfirmPower(L.T("Выключить компьютер?"))) ControlCenter.ShutDown(); });

        _controlTimer.Tick += async (_, _) => await RefreshControlsAsync();
        IsVisibleChanged += (_, _) => UpdateControlTimer();
        SystemPage.IsVisibleChanged += (_, _) => UpdateControlTimer();

        if (_settings.CleanDesktop && !IsToolRun) CleanDesktop.Apply(true, _settings.TaskbarWasAutoHide, _settings.IconsWereVisible);
    }

    private void UpdateControlTimer()
    {
        if (SystemPage.IsVisible) { _controlTimer.Start(); _ = RefreshControlsAsync(); }
        else _controlTimer.Stop();
    }

    private bool ConfirmPower(string question) =>
        MessageBox.Show(this, question, "Atoll", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void AddToggle(string id, string glyph, string label, Color on, Func<Task> click)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var circle = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Child = icon, Cursor = Cursors.Hand };
        circle.SetResourceReference(Border.BackgroundProperty, "TileBgHover");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var name = new TextBlock { Text = label, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var sub = new TextBlock
        {
            FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 88,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(circle);
        stack.Children.Add(name);
        stack.Children.Add(sub);
        circle.MouseLeftButtonUp += async (_, _) =>
        {
            if (_controlBusy) return;
            _controlBusy = true;
            Squeeze(circle);
            try { await click(); }
            catch (Exception e) { Log.Write($"control {id}: {e.Message}"); }
            finally { _controlBusy = false; }
            await RefreshControlsAsync();
        };
        ControlTiles.Children.Add(stack);
        _toggles[id] = (circle, sub, on);
    }

    private void AddPower(string glyph, string label, Action click)
    {
        var b = new Button { Style = (Style)FindResource("GhostPill"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        b.Content = content;
        b.Click += (_, _) => click();
        PowerButtons.Children.Add(b);
    }

    private static void Squeeze(FrameworkElement e)
    {
        var s = new ScaleTransform(1, 1);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = s;
        var a = new DoubleAnimation(0.86, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void Paint(string id, bool? on, string sub)
    {
        if (!_toggles.TryGetValue(id, out var t)) return;
        if (on == true)
        {
            t.Circle.Background = new SolidColorBrush(t.On);
            ((TextBlock)t.Circle.Child).Foreground = Brushes.White;
        }
        else
        {
            t.Circle.SetResourceReference(Border.BackgroundProperty, "TileBgHover");
            ((TextBlock)t.Circle.Child).SetResourceReference(TextBlock.ForegroundProperty, on == null ? "Fg3" : "Fg");
        }
        t.Sub.Text = sub;
    }

    private async Task RefreshControlsAsync()
    {
        var wifi = await ControlCenter.IsOnAsync(RadioKind.WiFi);
        var net = ControlCenter.NetworkName();
        Paint("wifi", wifi, wifi == null ? L.T("нет адаптера") : wifi == true ? net ?? L.T("не подключено") : L.T("выкл."));
        var bt = await ControlCenter.IsOnAsync(RadioKind.Bluetooth);
        Paint("bt", bt, bt == null ? L.T("нет адаптера") : bt == true ? L.T("вкл.") : L.T("выкл."));
        var air = await ControlCenter.AirplaneAsync();
        Paint("air", air, air == true ? L.T("вкл.") : L.T("выкл."));
        var vpn = await Task.Run(ControlCenter.ConnectedVpn);
        Paint("vpn", vpn != null, vpn ?? (!ControlCenter.VpnAvailable() ? L.T("настроить") : L.T("выкл.")));
        Paint("clean", _settings.CleanDesktop, _settings.CleanDesktop ? L.T("вкл.") : L.T("выкл."));

        var battery = ControlCenter.Battery();
        BatteryLine.Visibility = battery == null ? Visibility.Collapsed : Visibility.Visible;
        if (battery is { } b)
            BatteryLine.Text = b.Charging ? L.F("Батарея {0}% · заряжается", b.Percent) : L.F("Батарея {0}%", b.Percent);
    }

    private async Task ToggleRadio(RadioKind kind)
    {
        var on = await ControlCenter.IsOnAsync(kind);
        if (on == null) { ShowToast("", L.T("Нет адаптера"), null); return; }
        if (!await ControlCenter.SetAsync(kind, !on.Value))
            ShowToast("", L.T("Windows не разрешил переключить"), L.T("Откройте «Параметры → Конфиденциальность → Радио»"));
    }

    private async Task ToggleVpn()
    {
        // On this machine the real tunnel is Amnezia's: switch that, not a Windows profile.
        if (ControlCenter.AmneziaAvailable())
        {
            Paint("vpn", false, L.T("переключаю…"));
            var switched = await ControlCenter.ToggleAmneziaAsync();
            var on = ControlCenter.AmneziaTunnelUp();
            Paint("vpn", on, on ? "Amnezia" : L.T("выкл."));
            if (!switched) ShowToast("", "Amnezia не переключилась", "Откройте AmneziaVPN и подключитесь вручную");
            return;
        }

        var profiles = ControlCenter.VpnProfiles();
        if (profiles.Count == 0) { ControlCenter.OpenVpnSettings(); return; }
        var connected = await Task.Run(ControlCenter.ConnectedVpn);
        if (connected != null)
        {
            await ControlCenter.DisconnectVpnAsync(connected);
            return;
        }
        Paint("vpn", false, L.T("подключаю…"));
        if (!await ControlCenter.ConnectVpnAsync(profiles[0]))
        {
            ShowToast("", L.T("VPN не подключился"), L.T("Откройте настройки VPN, чтобы ввести данные"));
            ControlCenter.OpenVpnSettings();
        }
    }

    // ------------------------------------------------------------------ clean desktop

    private void SetCleanDesktop(bool on)
    {
        if (on && !_settings.CleanDesktop)
        {
            // remember the user's own choices, to give exactly them back later
            _settings.TaskbarWasAutoHide = CleanDesktop.TaskbarAutoHide;
            _settings.IconsWereVisible = CleanDesktop.IconsVisible;
        }
        _settings.CleanDesktop = on;
        _settings.Save();
        CleanDesktop.Apply(on, _settings.TaskbarWasAutoHide, _settings.IconsWereVisible);
        CleanDesktopSwitch.IsChecked = on;
        ShowToastIfCompact("", on ? L.T("Чистый рабочий стол включён") : L.T("Значки рабочего стола вернулись"));
    }

    private void CleanDesktopSwitch_Click(object sender, RoutedEventArgs e) => SetCleanDesktop(CleanDesktopSwitch.IsChecked == true);

    /// <summary>On exit: give the desktop icons back.</summary>
    private void ShutdownControlCenter()
    {
        if (_settings.CleanDesktop) CleanDesktop.Apply(false, _settings.TaskbarWasAutoHide, _settings.IconsWereVisible);
    }
}
