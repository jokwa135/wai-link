using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBridge;

/// <summary>Live phone telemetry snapshot collected in a single batched ADB socket call.</summary>
public sealed record PhoneTelemetry(
    int MemTotalMb,
    int MemAvailableMb,
    int SwapTotalMb,
    int SwapUsedMb,
    int BatteryPercent,
    bool IsCharging,
    long CurrentMa,
    string ForegroundPackage,
    string LatestSmsId = "",
    string LatestSmsSender = "",
    string LatestSmsBody = "",
    string LatestOtpCode = "",
    string LatestOtpSource = ""
);

/// <summary>
/// Standalone Android Bridge Engine (WAILink.Android.dll).
/// Talks directly to the ADB server over TCP socket 127.0.0.1:5037 for <5ms zero-process-spawn
/// shell execution, telemetry, touch/swipe/key injection, and screenshot capture, with automatic
/// daemon bootstrap via WAI Link's bundled dist\lib\adb.exe.
/// </summary>
public sealed class Adb(string adbPath)
{
    public const int WifiPort = 5555;
    public const int AdbServerPort = 5037;

    public string Path { get; } = adbPath;
    public string? Serial { get; private set; }
    public string? Model { get; private set; }

    readonly Dictionary<string, string> _models = new();
    int _telemetryTickCount;
    string _cachedSmsId = "";
    string _cachedSmsSender = "";
    string _cachedSmsBody = "";
    string _cachedOtpCode = "";
    string _cachedOtpSource = "";

    static readonly Regex OtpRegex = new(@"\b(\d{4,8})\b", RegexOptions.Compiled);
    static readonly string[] OtpKeywords = ["code", "otp", "verification", "pin", "password", "login", "security"];

    public static bool IsWifi(string serial) => serial.Contains(':') || serial.Contains("._adb");

    /// <summary>
    /// Executes an ADB shell command directly over TCP 127.0.0.1:5037 without spawning adb.exe.
    /// Falls back to adb.exe only if the ADB server daemon is not yet running.
    /// </summary>
    public async Task<string> ShellAsync(string command, CancellationToken ct = default)
    {
        if (Serial is not null)
        {
            var sockRes = await TrySocketShellAsync(Serial, command, ct);
            if (sockRes is not null)
                return sockRes;
        }
        return await RunAsync($"shell \"{command.Replace("\"", "\\\"")}\"", ct);
    }

    public static async Task<string?> TrySocketShellAsync(string serial, string command, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3500);
            await tcp.ConnectAsync("127.0.0.1", AdbServerPort, timeoutCts.Token);
            using var stream = tcp.GetStream();

            if (!await SendAdbPacketAsync(stream, $"host:transport:{serial}", timeoutCts.Token))
                return null;
            if (!await SendAdbPacketAsync(stream, $"shell:{command}", timeoutCts.Token))
                return null;

            using var ms = new MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
                int n = await stream.ReadAsync(buf, timeoutCts.Token);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n").Trim();
        }
        catch
        {
            return null;
        }
    }

    static async Task<bool> SendAdbPacketAsync(NetworkStream stream, string payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes($"{body.Length:x4}");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);

        var status = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await stream.ReadAsync(status.AsMemory(read, 4 - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return Encoding.ASCII.GetString(status) == "OKAY";
    }

    public async Task EnsureServerRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(250);
            await tcp.ConnectAsync("127.0.0.1", AdbServerPort, checkCts.Token);
            return;
        }
        catch
        {
            // Server not running yet; start it without redirecting stdout/stderr so fork-server does not inherit pipes.
        }

        try
        {
            if (!File.Exists(Path)) return;
            var psi = new ProcessStartInfo(Path, "start-server")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startCts.CancelAfter(5000);
                await p.WaitForExitAsync(startCts.Token);
            }
        }
        catch { }
    }

    public async Task<string?> TryHostDevicesSocketAsync(CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(800);
            await tcp.ConnectAsync("127.0.0.1", AdbServerPort, timeoutCts.Token);
            using var stream = tcp.GetStream();

            if (!await SendAdbPacketAsync(stream, "host:devices", timeoutCts.Token))
                return null;

            var lenBuf = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int n = await stream.ReadAsync(lenBuf.AsMemory(read, 4 - read), timeoutCts.Token);
                if (n <= 0) return "";
                read += n;
            }
            if (!int.TryParse(Encoding.ASCII.GetString(lenBuf), System.Globalization.NumberStyles.HexNumber, null, out int bodyLen) || bodyLen <= 0)
                return "";

            var bodyBuf = new byte[bodyLen];
            int bodyRead = 0;
            while (bodyRead < bodyLen)
            {
                int n = await stream.ReadAsync(bodyBuf.AsMemory(bodyRead, bodyLen - bodyRead), timeoutCts.Token);
                if (n <= 0) break;
                bodyRead += n;
            }
            return Encoding.UTF8.GetString(bodyBuf, 0, bodyRead).Replace("\r\n", "\n");
        }
        catch
        {
            return null;
        }
    }

    public Task<string> RunAsync(string args, CancellationToken ct = default) =>
        RunRawAsync(Serial is null ? args : $"-s {Serial} {args}", ct);

    public async Task<string> RunRawAsync(string args, CancellationToken ct = default)
    {
        await EnsureServerRunningAsync(ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(6000);
            var psi = new ProcessStartInfo(Path, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await p.WaitForExitAsync(timeoutCts.Token);
            return output.Trim();
        }
        catch
        {
            return "";
        }
    }

    public async Task<List<(string Serial, string Model)>> ReadyDevicesAsync(CancellationToken ct)
    {
        var rawDevices = await TryHostDevicesSocketAsync(ct);
        if (rawDevices is null)
        {
            await EnsureServerRunningAsync(ct);
            rawDevices = await TryHostDevicesSocketAsync(ct);
        }

        IEnumerable<string> lines;
        if (rawDevices is not null)
        {
            lines = rawDevices.Split('\n');
        }
        else
        {
            lines = (await RunRawAsync("devices", ct)).Split('\n').Skip(1);
        }

        var serials = lines
            .Select(l => l.Trim().Split('\t'))
            .Where(p => p.Length == 2 && p[1] == "device")
            .Select(p => p[0])
            .ToList();
        foreach (var gone in _models.Keys.Except(serials).ToList()) _models.Remove(gone);

        var list = new List<(string, string)>();
        foreach (var s in serials)
        {
            if (!_models.TryGetValue(s, out var model) || model.Length == 0)
            {
                var prop = await TrySocketShellAsync(s, "getprop ro.product.model", ct)
                           ?? await RunRawAsync($"-s {s} shell getprop ro.product.model", ct);
                _models[s] = model = prop.Trim();
            }
            list.Add((s, model));
        }
        return list;
    }

    public async Task<bool> IsDeviceReadyAsync(string? model, CancellationToken ct)
    {
        var ready = (await ReadyDevicesAsync(ct)).Where(d => model is null || d.Model == model).ToList();
        var serials = ready.Select(d => d.Serial).ToList();
        var usbSerial = serials.FirstOrDefault(s => !IsWifi(s));
        Serial = usbSerial
              ?? serials.FirstOrDefault(s => s.EndsWith($":{WifiPort}"))
              ?? serials.FirstOrDefault();
        Model = Serial is null ? null : ready.First(d => d.Serial == Serial).Model;

        if (usbSerial is not null && Model is not null)
        {
            foreach (var wifiDup in ready.Where(d => d.Model == Model && IsWifi(d.Serial)).Select(d => d.Serial).ToList())
            {
                try { await DisconnectAsync(wifiDup, ct); } catch { }
            }
        }
        return Serial is not null;
    }

    public Task<string> ConnectAsync(string ip, CancellationToken ct = default) =>
        RunRawAsync($"connect {ip}:{WifiPort}", ct);

    public Task<string> DisconnectAsync(string serial, CancellationToken ct = default) =>
        RunRawAsync($"disconnect {serial}", ct);

    public async Task<string?> WifiIpAsync(CancellationToken ct)
    {
        var m = Regex.Match(await ShellAsync("ip -f inet addr show wlan0", ct), @"inet (\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    public async Task<int> SdkLevelAsync(CancellationToken ct) =>
        int.TryParse(await ShellAsync("getprop ro.build.version.sdk", ct), out var v) ? v : 0;

    public async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        var escaped = text.Replace(" ", "%s").Replace("'", "").Replace("\"", "").Replace(";", "").Replace("&", "");
        await ShellAsync($"input text '{escaped}'", ct);
    }

    public Task TapAsync(int x, int y, CancellationToken ct = default) =>
        ShellAsync($"input tap {x} {y}", ct);

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 250, CancellationToken ct = default) =>
        ShellAsync($"input swipe {x1} {y1} {x2} {y2} {durationMs}", ct);

    public Task SendKeyEventAsync(string keyOrCode, CancellationToken ct = default) =>
        ShellAsync($"input keyevent {keyOrCode}", ct);

    public Task<string> InstallApkAsync(string apkFilePath, CancellationToken ct = default) =>
        RunAsync($"install -r \"{apkFilePath}\"", ct);

    public Task<string> PushFileToDownloadsAsync(string localFilePath, CancellationToken ct = default)
    {
        var fileName = System.IO.Path.GetFileName(localFilePath);
        return RunAsync($"push \"{localFilePath}\" \"/sdcard/Download/{fileName}\"", ct);
    }

    public async Task<byte[]?> CaptureScreenshotPngAsync(CancellationToken ct = default)
    {
        if (Serial is null) return null;
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(5000);
            await tcp.ConnectAsync("127.0.0.1", AdbServerPort, timeoutCts.Token);
            using var stream = tcp.GetStream();

            if (!await SendAdbPacketAsync(stream, $"host:transport:{Serial}", timeoutCts.Token))
                return null;
            if (!await SendAdbPacketAsync(stream, "shell:screencap -p", timeoutCts.Token))
                return null;

            using var ms = new MemoryStream();
            var buf = new byte[16384];
            while (true)
            {
                int n = await stream.ReadAsync(buf, timeoutCts.Token);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            var raw = ms.ToArray();
            // Normalize CRLF -> LF if legacy shell translated newlines
            if (raw.Length > 8 && raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47)
            {
                if (raw[4] == 0x0D && raw[5] == 0x0D && raw[6] == 0x0A)
                {
                    using var clean = new MemoryStream(raw.Length);
                    for (int i = 0; i < raw.Length; i++)
                    {
                        if (i + 1 < raw.Length && raw[i] == 0x0D && raw[i + 1] == 0x0A)
                            continue;
                        clean.WriteByte(raw[i]);
                    }
                    return clean.ToArray();
                }
                return raw;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Zero-spike telemetry: reads /proc/meminfo, battery, and focused app over direct TCP socket,
    /// and only queries the heavy SMS ContentProvider every 4th tick so low-RAM phones like
    /// the Galaxy J5 Prime never experience background CPU spikes.
    /// </summary>
    public async Task<PhoneTelemetry?> GetTelemetryAsync(CancellationToken ct = default)
    {
        if (Serial is null) return null;
        bool querySms = (_telemetryTickCount++ % 4) == 0;

        string cmd =
            "cat /proc/meminfo | head -n 20; " +
            "echo '---SEP---'; " +
            "cat /sys/class/power_supply/battery/capacity 2>/dev/null || echo -1; " +
            "cat /sys/class/power_supply/battery/current_now 2>/dev/null || echo 0; " +
            "echo '---SEP---'; " +
            "dumpsys window windows 2>/dev/null | grep -E 'mCurrentFocus|mFocusedApp' | head -n 2";

        if (querySms)
        {
            cmd += "; echo '---SEP---'; " +
                   "dumpsys notification --noredact 2>/dev/null";
        }

        var raw = await ShellAsync(cmd, ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split("---SEP---");
        if (parts.Length < 3) return null;

        int memTotalKb = ParseMemKb(parts[0], "MemTotal");
        int memAvailKb = ParseMemKb(parts[0], "MemAvailable");
        if (memAvailKb == 0)
            memAvailKb = ParseMemKb(parts[0], "MemFree") + ParseMemKb(parts[0], "Cached");
        int swapTotalKb = ParseMemKb(parts[0], "SwapTotal");
        int swapFreeKb = ParseMemKb(parts[0], "SwapFree");

        var batLines = parts[1].Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int batPct = batLines.Length > 0 && int.TryParse(batLines[0], out var b) ? b : -1;
        long curMa = batLines.Length > 1 && long.TryParse(batLines[1], out var c) ? c : 0;
        if (Math.Abs(curMa) > 10000) curMa /= 1000;

        string fgPkg = "";
        var pkgMatch = Regex.Match(parts[2], @"([a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)+)/");
        if (pkgMatch.Success) fgPkg = pkgMatch.Groups[1].Value;

        if (querySms && parts.Length >= 4)
        {
            try
            {
                var notifLines = parts[3].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string currentTitle = "";
                foreach (var rawLine in notifLines)
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("android.title=", StringComparison.OrdinalIgnoreCase))
                    {
                        currentTitle = ExtractNotificationField(line, "android.title=");
                    }
                    else if (line.StartsWith("android.text=", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("android.bigText=", StringComparison.OrdinalIgnoreCase))
                    {
                        string key = line.StartsWith("android.text=", StringComparison.OrdinalIgnoreCase)
                            ? "android.text="
                            : "android.bigText=";
                        string text = ExtractNotificationField(line, key);
                        if (!string.IsNullOrEmpty(text))
                        {
                            string combined = $"{currentTitle} {text}";
                            bool hasOtpKeyword = OtpKeywords.Any(k => combined.Contains(k, StringComparison.OrdinalIgnoreCase));
                            if (hasOtpKeyword)
                            {
                                var match = OtpRegex.Match(text);
                                if (!match.Success && !string.IsNullOrEmpty(currentTitle))
                                    match = OtpRegex.Match(currentTitle);

                                if (match.Success)
                                {
                                    _cachedOtpCode = match.Groups[1].Value;
                                    _cachedOtpSource = !string.IsNullOrWhiteSpace(currentTitle) ? currentTitle : "Android Notification";
                                    _cachedSmsId = _cachedOtpCode;
                                    _cachedSmsSender = _cachedOtpSource;
                                    _cachedSmsBody = text;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback cleanly without throwing
            }
        }

        return new PhoneTelemetry(
            MemTotalMb: memTotalKb / 1024,
            MemAvailableMb: memAvailKb / 1024,
            SwapTotalMb: swapTotalKb / 1024,
            SwapUsedMb: Math.Max(0, (swapTotalKb - swapFreeKb) / 1024),
            BatteryPercent: batPct,
            IsCharging: curMa >= 0,
            CurrentMa: curMa,
            ForegroundPackage: fgPkg,
            LatestSmsId: _cachedSmsId,
            LatestSmsSender: _cachedSmsSender,
            LatestSmsBody: _cachedSmsBody,
            LatestOtpCode: _cachedOtpCode,
            LatestOtpSource: _cachedOtpSource
        );
    }

    static string ExtractNotificationField(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        string val = line.Substring(idx + key.Length).Trim();
        if (val.StartsWith("String (") && val.EndsWith(")"))
        {
            val = val.Substring(8, val.Length - 9).Trim();
        }
        return val;
    }

    static int ParseMemKb(string text, string key)
    {
        var m = Regex.Match(text, $@"{key}:\s*(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }
}
