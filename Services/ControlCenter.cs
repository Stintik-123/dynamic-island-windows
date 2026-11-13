using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DynamicIsland.Services;

/// <summary>
/// The quick toggles of the control centre: Wi-Fi and Bluetooth radios, "airplane" (all radios),
/// Windows VPN connections and power. Everything runs with the user's rights, no admin needed.
/// </summary>
public static class ControlCenter
{
    // ------------------------------------------------------------------ radios

    private static bool _accessAsked;

    private static async Task<IReadOnlyList<Radio>> RadiosAsync()
    {
        if (!_accessAsked)
        {
            _accessAsked = true;
            try { await Radio.RequestAccessAsync(); } catch { }
        }
        try { return await Radio.GetRadiosAsync(); }
        catch { return []; }
    }

    /// <summary>null = this PC has no such radio.</summary>
    public static async Task<bool?> IsOnAsync(RadioKind kind)
    {
        var radios = (await RadiosAsync()).Where(r => r.Kind == kind).ToList();
        return radios.Count == 0 ? null : radios.Any(r => r.State == RadioState.On);
    }

    public static async Task<bool> SetAsync(RadioKind kind, bool on)
    {
        bool ok = false;
        foreach (var r in (await RadiosAsync()).Where(r => r.Kind == kind))
        {
            try { ok |= await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed; }
            catch (Exception e) { Log.Write($"radio {kind}: {e.Message}"); }
        }
        return ok;
    }

    /// <summary>"Airplane": every radio off; turning it off brings them back.</summary>
    public static async Task<bool?> AirplaneAsync()
    {
        var radios = (await RadiosAsync()).Where(r => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband).ToList();
        return radios.Count == 0 ? null : radios.All(r => r.State != RadioState.On);
    }

    public static async Task SetAirplaneAsync(bool on)
    {
        foreach (var kind in new[] { RadioKind.WiFi, RadioKind.Bluetooth, RadioKind.MobileBroadband })
            await SetAsync(kind, !on);
    }

    // ------------------------------------------------------------------ network

    /// <summary>Name of the network we're online through (Wi-Fi name or "Ethernet"), or null when offline.</summary>
    public static string? NetworkName()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null || p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) return null;
            return p.IsWlanConnectionProfile ? p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName : p.ProfileName;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ VPN (built-in Windows VPN profiles)

    /// <summary>VPN profiles the user created in Windows settings (rasphone phone book).</summary>
    public static List<string> VpnProfiles()
    {
        var names = new List<string>();
        foreach (var pbk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
                 })
        {
            try
            {
                if (!File.Exists(pbk)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(pbk), @"^\[(.+)\]\s*$", RegexOptions.Multiline))
                    if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
            }
            catch { }
        }
        return names;
    }

    private static string Rasdial(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("rasdial.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            return output;
        }
        catch { return ""; }
    }

    /// <summary>The connected VPN profile, if any.</summary>
    public static string? ConnectedVpn()
    {
        if (AmneziaTunnelUp()) return "Amnezia";
        var profiles = VpnProfiles();
        if (profiles.Count == 0) return null;
        var output = Rasdial("");
        return profiles.FirstOrDefault(n => output.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    public static Task<bool> ConnectVpnAsync(string name) => Task.Run(() =>
    {
        Rasdial($"\"{name}\"");
        return ConnectedVpn() == name;
    });

    public static Task DisconnectVpnAsync(string name) => Task.Run(() => Rasdial($"\"{name}\" /disconnect"));

    /// <summary>Windows' own VPN page (for adding a profile or for third-party VPN apps).</summary>
    public static void OpenVpnSettings() => Process.Start(new ProcessStartInfo("ms-settings:network-vpn") { UseShellExecute = true });

    // ------------------------------------------------------------------ AmneziaVPN (third-party tunnel)

    private const string AmneziaConnectButtonId = "AmneziaApplication.mainWindow.tabBarStackView.connectButton";

    private static readonly string[] AmneziaPaths =
    [
        @"C:\Program Files\AmneziaVPN\AmneziaVPN.exe",
        @"C:\Program Files (x86)\AmneziaVPN\AmneziaVPN.exe",
    ];

    /// <summary>Path to the AmneziaVPN executable, or null when the app is not installed.</summary>
    public static string? AmneziaPath()
    {
        foreach (var path in AmneziaPaths)
            if (File.Exists(path)) return path;
        return null;
    }

    public static bool AmneziaRunning() => Process.GetProcessesByName("AmneziaVPN").Length > 0;

    public static bool AmneziaAvailable() => AmneziaPath() != null || AmneziaRunning();

    /// <summary>True while a WireGuard / Amnezia tunnel adapter is up. The adapter is the source of truth:
    /// the button caption can lag behind, the adapter cannot.</summary>
    public static bool AmneziaTunnelUp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var d = ni.Description ?? "";
                if (d.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                    d.Contains("Amnezia", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static AutomationElement? AmneziaButton()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("AmneziaVPN"))
            {
                var win = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id));
                if (win == null) continue;
                var btn = win.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, AmneziaConnectButtonId));
                if (btn != null) return btn;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Press Amnezia's own connect button and wait until the tunnel really changes state.</summary>
    public static Task<bool> ToggleAmneziaAsync() => Task.Run(() =>
    {
        var was = AmneziaTunnelUp();
        if (!AmneziaRunning())
        {
            var exe = AmneziaPath();
            if (exe == null) return false;
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { return false; }
            for (var i = 0; i < 40 && !AmneziaRunning(); i++) Thread.Sleep(250);
            Thread.Sleep(2500);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var button = AmneziaButton();
            if (button == null) { Thread.Sleep(1200); continue; }
            try
            {
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) return false;
                ((InvokePattern)pattern).Invoke();
            }
            catch { }

            for (var i = 0; i < 50; i++)
            {
                Thread.Sleep(400);
                if (AmneziaTunnelUp() != was) return true;
            }
        }
        return false;
    });

    /// <summary>Any VPN this machine can switch from the island.</summary>
    public static bool VpnAvailable() => VpnProfiles().Count > 0 || AmneziaAvailable();

    // ------------------------------------------------------------------ power

    public static void Sleep() => System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);

    public static void Restart() => Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });

    public static void ShutDown() => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });

    /// <summary>Battery percent and charging, or null on a desktop PC.</summary>
    public static (int Percent, bool Charging)? Battery()
    {
        var s = System.Windows.Forms.SystemInformation.PowerStatus;
        if (s.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) || s.BatteryLifePercent > 1.0f) return null;
        return ((int)Math.Round(s.BatteryLifePercent * 100), s.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online);
    }
}
