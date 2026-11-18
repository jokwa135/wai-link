using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBridge;

// Passive device discovery for the start screen. It only reads state (adb
// device list, usbmux list, a WebDriverAgent status probe) and never connects.
public sealed partial class MainForm
{
    static readonly HttpClient s_probe = new() { Timeout = TimeSpan.FromMilliseconds(1300) };
    System.Windows.Forms.Timer? _deviceScan;
    bool _scanning;

    void StartDeviceScan()
    {
        _deviceScan = new System.Windows.Forms.Timer { Interval = 6000 };
        _deviceScan.Tick += async (_, _) => await ScanDevicesAsync();
        _deviceScan.Start();
        _ = ScanDevicesAsync();
    }

    async Task ScanDevicesAsync()
    {
        if (_scanning || _closing || IsDisposed || _viewMode != StudioViewMode.None) return;
        _scanning = true;
        try
        {
            var androidTask = DetectAndroidAsync();
            var iphoneTask = DetectIphoneAsync(_iphoneController.MjpegHost);
            var android = await androidTask;
            var iphone = await iphoneTask;
            if (!IsDisposed && !_closing) _chooserPanel.SetDevices(iphone, android);
        }
        catch { }
        finally { _scanning = false; }
    }

    async Task<DeviceCard> DetectAndroidAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(5000);
            var devices = await _adb.ReadyDevicesAsync(cts.Token);
            if (devices.Count == 0) return new DeviceCard("Android", "Not detected", false);
            var (serial, model) = devices[0];
            string status = "Ready · " + (Adb.IsWifi(serial) ? "Wi-Fi" : "USB");
            var battery = Regex.Match(await Adb.TrySocketShellAsync(serial, "dumpsys battery", cts.Token) ?? "", @"level:\s*(\d+)");
            if (battery.Success) status += $" · {battery.Groups[1].Value}%";
            if (devices.Count > 1) status += $" · +{devices.Count - 1} more";
            return new DeviceCard(string.IsNullOrWhiteSpace(model) ? "Android" : model, status, true);
        }
        catch { return new DeviceCard("Android", "Not detected", false); }
    }

    static async Task<DeviceCard> DetectIphoneAsync(string? lanHost)
    {
        var usb = await Task.Run(DetectUsbIphone);
        if (usb is not null) return usb;
        foreach (var host in new[] { lanHost, "127.0.0.1" })
        {
            if (string.IsNullOrWhiteSpace(host)) continue;
            try
            {
                using var response = await s_probe.GetAsync($"http://{host}:8100/status");
                if (response.IsSuccessStatusCode)
                    return new DeviceCard("iPhone", host == "127.0.0.1" ? "Ready · USB" : "Ready · Wi-Fi", true);
            }
            catch { }
        }
        return new DeviceCard("iPhone", "Tap to connect", false);
    }

    static DeviceCard? DetectUsbIphone()
    {
        if (!File.Exists(AppPaths.Python)) return null;
        try
        {
            var psi = new ProcessStartInfo(AppPaths.Python, $"{IphoneController.Pmd3} usbmux list")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            AppPaths.ApplyIphoneEnvironment(psi);
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(7000)) { try { process.Kill(true); } catch { } return null; }
            using var doc = JsonDocument.Parse(output.Result);
            foreach (var device in doc.RootElement.EnumerateArray())
            {
                string name = device.TryGetProperty("DeviceName", out var n) ? n.GetString() ?? "iPhone" : "iPhone";
                string link = device.TryGetProperty("ConnectionType", out var c) && c.GetString() == "Network" ? "Wi-Fi" : "USB";
                string version = device.TryGetProperty("ProductVersion", out var v) ? $" · iOS {v.GetString()}" : "";
                return new DeviceCard(name, $"Ready · {link}{version}", true);
            }
        }
        catch { }
        return null;
    }
}
