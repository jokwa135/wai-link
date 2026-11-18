using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace PhoneBridge;

/// <summary>
/// Standalone AI Agent &amp; CLI-Anything Control Server (WAILink.Agent.dll).
/// Listens on http://127.0.0.1:17380 so cli-anything-wailink, Claude, Codex, Hermes,
/// and Antigravity can inspect telemetry, capture screenshots, tap, swipe, type,
/// boost RAM, transfer files, and switch themes/views on both Android and iPhone.
/// </summary>
public sealed class WaiLinkAgentServer : IDisposable
{
    public const int Port = 17380;

    readonly Adb _adb;
    readonly AndroidGovernor _governor;
    readonly IphoneController _iphoneCtrl;
    readonly IphoneService _iphoneSvc;
    readonly Func<Form?> _getHostForm;
    readonly Func<string> _getViewMode;
    readonly Action<string> _setViewMode;
    readonly Func<string> _getThemeName;
    readonly Action<string> _setThemeName;
    readonly Func<(string Code, string Source)> _getLatestOtp;

    TcpListener? _listener;
    readonly CancellationTokenSource _cts = new();

    public WaiLinkAgentServer(
        Adb adb,
        AndroidGovernor governor,
        IphoneController iphoneCtrl,
        IphoneService iphoneSvc,
        Func<Form?> getHostForm,
        Func<string> getViewMode,
        Action<string> setViewMode,
        Func<string> getThemeName,
        Action<string> setThemeName,
        Func<(string Code, string Source)> getLatestOtp)
    {
        _adb = adb;
        _governor = governor;
        _iphoneCtrl = iphoneCtrl;
        _iphoneSvc = iphoneSvc;
        _getHostForm = getHostForm;
        _getViewMode = getViewMode;
        _setViewMode = setViewMode;
        _getThemeName = getThemeName;
        _setThemeName = setThemeName;
        _getLatestOtp = getLatestOtp;
    }

    public void Start()
    {
        _ = RunLoopAsync(_cts.Token);
    }

    async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch { }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;
                using var stream = client.GetStream();
                var buf = new byte[16384];
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) return;

                using var ms = new MemoryStream();
                ms.Write(buf, 0, n);

                var raw = Encoding.UTF8.GetString(ms.ToArray());
                int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    int contentLen = 0;
                    foreach (var hLine in raw[..headerEnd].Split("\r\n"))
                    {
                        if (hLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(hLine["Content-Length:".Length..].Trim(), out var cl))
                        {
                            contentLen = cl;
                        }
                    }
                    int headerBytes = Encoding.UTF8.GetByteCount(raw[..(headerEnd + 4)]);
                    while (ms.Length - headerBytes < contentLen)
                    {
                        int more = await stream.ReadAsync(buf, ct);
                        if (more <= 0) break;
                        ms.Write(buf, 0, more);
                    }
                    raw = Encoding.UTF8.GetString(ms.ToArray());
                }

                var lines = raw.Split("\r\n");
                var reqParts = lines[0].Split(' ');
                string method = reqParts.Length > 0 ? reqParts[0] : "GET";
                string fullPath = reqParts.Length > 1 ? reqParts[1] : "/";
                int qIdx = fullPath.IndexOf('?');
                string route = (qIdx >= 0 ? fullPath[..qIdx] : fullPath).ToLowerInvariant();
                string query = qIdx >= 0 ? fullPath[(qIdx + 1)..] : "";

                int bodyIdx = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                string body = bodyIdx >= 0 ? raw[(bodyIdx + 4)..].Trim() : "";

                // Browsers add Origin (or Sec-Fetch-Site) to requests a web page makes. Scripts and
                // CLI tools do not, so refusing those stops websites from driving the phones.
                bool fromWebPage = lines.Take(Math.Max(1, Array.IndexOf(lines, ""))).Any(l =>
                    l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase) ||
                    (l.StartsWith("Sec-Fetch-Site:", StringComparison.OrdinalIgnoreCase) && !l.Contains("none", StringComparison.OrdinalIgnoreCase)));
                if (fromWebPage)
                {
                    var deny = Encoding.UTF8.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(deny, ct);
                    return;
                }

                switch (route)
                {
                    case "/api/status":
                    case "/status":
                    {
                        string currentMode = _getViewMode();
                        var t = currentMode.Contains("Android", StringComparison.OrdinalIgnoreCase)
                            ? await _adb.GetTelemetryAsync(ct)
                            : null;
                        var (otpCode, otpSource) = _getLatestOtp();
                        var payload = new
                        {
                            ok = true,
                            app = "WAI Link v2.0",
                            standaloneDlls = new[] { "WAILink.Android.dll", "WAILink.Apple.dll", "WAILink.Agent.dll" },
                            scrcpyExternalDependency = false,
                            viewMode = currentMode,
                            theme = _getThemeName(),
                            android = new
                            {
                                connected = _adb.Serial is not null,
                                serial = _adb.Serial,
                                model = _adb.Model,
                                hardwareTier = _governor.CurrentTier.ToString(),
                                memTotalMb = t?.MemTotalMb ?? 0,
                                memAvailableMb = t?.MemAvailableMb ?? 0,
                                swapUsedMb = t?.SwapUsedMb ?? 0,
                                batteryPercent = t?.BatteryPercent ?? -1,
                                isCharging = t?.IsCharging ?? false,
                                foregroundPackage = t?.ForegroundPackage ?? ""
                            },
                            iphone = new
                            {
                                airPlayRunning = _iphoneCtrl.IsAirPlayRunning,
                                wdaConnected = _iphoneCtrl.IsWdaConnected,
                                bluetoothPresent = _iphoneSvc.IsBluetoothIphonePresent,
                                bluetoothName = _iphoneSvc.BluetoothIphoneName,
                                bluetoothProfiles = _iphoneSvc.BluetoothProfilesSummary,
                                continuityUrl = IphoneService.PageUrl
                            },
                            otp = new
                            {
                                code = otpCode,
                                source = otpSource
                            }
                        };
                        await WriteJsonAsync(stream, payload, ct);
                        return;
                    }

                    case "/api/screenshot":
                    case "/screenshot":
                    {
                        string target = GetQueryVal(query, "target") ?? "window";
                        string? outPath = GetQueryVal(query, "out");
                        byte[]? png = null;

                        if (target.Equals("android", StringComparison.OrdinalIgnoreCase))
                            png = await _adb.CaptureScreenshotPngAsync(ct);
                        else if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                            png = await _iphoneCtrl.CaptureScreenshotPngAsync(ct);

                        if (png is null)
                            png = await CaptureHostWindowPngAsync(ct);

                        if (png is null)
                        {
                            await WriteJsonAsync(stream, new { ok = false, error = "Screenshot capture unavailable" }, ct);
                            return;
                        }

                        if (!string.IsNullOrEmpty(outPath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                            await File.WriteAllBytesAsync(outPath, png, ct);
                            await WriteJsonAsync(stream, new { ok = true, target, path = outPath, bytes = png.Length }, ct);
                            return;
                        }

                        var hdr = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\n" +
                            $"Content-Length: {png.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(hdr, ct);
                        await stream.WriteAsync(png, ct);
                        return;
                    }

                    case "/api/tap":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        int x = GetInt(root, "x", 360);
                        int y = GetInt(root, "y", 640);
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                            await _iphoneCtrl.SendRawTapAsync(x, y, ct);
                        else
                            await _adb.TapAsync(x, y, ct);
                        await WriteJsonAsync(stream, new { ok = true, action = "tap", target, x, y }, ct);
                        return;
                    }

                    case "/api/swipe":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        int x1 = GetInt(root, "x1", 360);
                        int y1 = GetInt(root, "y1", 900);
                        int x2 = GetInt(root, "x2", 360);
                        int y2 = GetInt(root, "y2", 300);
                        int dur = GetInt(root, "durationMs", 250);
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                            await _iphoneCtrl.SendRawSwipeAsync(x1, y1, x2, y2, dur / 1000.0, ct);
                        else
                            await _adb.SwipeAsync(x1, y1, x2, y2, dur, ct);
                        await WriteJsonAsync(stream, new { ok = true, action = "swipe", target, x1, y1, x2, y2 }, ct);
                        return;
                    }

                    case "/api/hid/touch":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string state = GetStr(root, "state", "down").ToLowerInvariant();
                        int x = GetInt(root, "x", 200);
                        int y = GetInt(root, "y", 400);
                        int w = GetInt(root, "w", 394);
                        int h = GetInt(root, "h", 768);
                        bool brake = GetInt(root, "brake", 1) != 0;
                        var sz = new Size(w, h);
                        bool ok = state switch
                        {
                            "down" => _iphoneCtrl.SendHidTouchDown(x, y, sz),
                            "move" => _iphoneCtrl.SendHidTouchMove(x, y, sz),
                            "up" => _iphoneCtrl.SendHidTouchUp(x, y, sz, brake),
                            _ => false
                        };
                        await WriteJsonAsync(stream, new { ok, state, x, y }, ct);
                        return;
                    }

                    case "/api/type":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        string text = GetStr(root, "text", "");
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                            await _iphoneCtrl.SendKeysAsync(text, ct);
                        else
                            await _adb.TypeTextAsync(text, ct);
                        await WriteJsonAsync(stream, new { ok = true, action = "type", target, text }, ct);
                        return;
                    }

                    case "/api/key":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        string key = GetStr(root, "key", "HOME").ToUpperInvariant();
                        bool ok = true;
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                        {
                            switch (key)
                            {
                                case "HOME" or "3":
                                    ok = await _iphoneCtrl.SendHomeAsync(ct);
                                    break;
                                case "BACK" or "4":
                                    ok = await _iphoneCtrl.NavigateBackAsync(ct);
                                    break;
                                case "RECENTS" or "APP_SWITCH" or "187":
                                    ok = await _iphoneCtrl.OpenAppSwitcherAsync(ct);
                                    break;
                                case "VOLUME_UP" or "24":
                                    ok = await _iphoneCtrl.PressButtonAsync("volumeUp", ct);
                                    break;
                                case "VOLUME_DOWN" or "25":
                                    ok = await _iphoneCtrl.PressButtonAsync("volumeDown", ct);
                                    break;
                                case "POWER" or "26":
                                    ok = await _iphoneCtrl.TogglePowerLockAsync(ct);
                                    break;
                                default:
                                    ok = false;
                                    break;
                            }
                        }
                        else
                        {
                            string code = key switch
                            {
                                "HOME" => "3",
                                "BACK" => "4",
                                "RECENTS" or "APP_SWITCH" => "187",
                                "ENTER" => "66",
                                "VOLUME_UP" => "24",
                                "VOLUME_DOWN" => "25",
                                "POWER" => "26",
                                _ => key
                            };
                            await _adb.SendKeyEventAsync(code, ct);
                        }
                        await WriteJsonAsync(stream, new { ok, action = "key", target, key }, ct);
                        return;
                    }

                    case "/api/app/launch":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        string app = GetStr(root, "app", "com.ss.android.ugc.trill");
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                        {
                            bool launched = await _iphoneCtrl.LaunchDeepLinkAsync(app, ct);
                            _iphoneSvc.PendingDeepLink = _iphoneCtrl.PendingDeepLinkUrl;
                            await WriteJsonAsync(stream, new { ok = true, target, app, deepLink = _iphoneCtrl.PendingDeepLinkUrl, wdaDirect = launched }, ct);
                        }
                        else
                        {
                            await _adb.ShellAsync($"monkey -p {app} -c android.intent.category.LAUNCHER 1", ct);
                            await WriteJsonAsync(stream, new { ok = true, target, app, launched = true }, ct);
                        }
                        return;
                    }

                    case "/api/boost":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                        {
                            var (ok, count) = await _iphoneCtrl.BoostIphoneRamAsync(ct);
                            await WriteJsonAsync(stream, new { ok, target = "iphone", terminatedApps = count }, ct);
                        }
                        else
                        {
                            var t = await _adb.GetTelemetryAsync(ct);
                            var (freed, avail) = await _governor.BoostRamAsync(t?.ForegroundPackage, ct);
                            await WriteJsonAsync(stream, new { ok = true, target = "android", freedMb = freed, availableMb = avail }, ct);
                        }
                        return;
                    }

                    case "/api/file/push":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var root = doc.RootElement;
                        string target = GetStr(root, "target", "android");
                        string filePath = GetStr(root, "path", "");
                        if (!File.Exists(filePath))
                        {
                            await WriteJsonAsync(stream, new { ok = false, error = $"File not found: {filePath}" }, ct);
                            return;
                        }
                        if (target.Equals("iphone", StringComparison.OrdinalIgnoreCase))
                        {
                            string url = _iphoneSvc.ShareFileToIphone(filePath);
                            await WriteJsonAsync(stream, new { ok = true, target = "iphone", downloadUrl = url }, ct);
                        }
                        else if (filePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            string res = await _adb.InstallApkAsync(filePath, ct);
                            await WriteJsonAsync(stream, new { ok = true, target = "android", installed = true, output = res }, ct);
                        }
                        else
                        {
                            string res = await _adb.PushFileToDownloadsAsync(filePath, ct);
                            await WriteJsonAsync(stream, new { ok = true, target = "android", pushed = true, output = res }, ct);
                        }
                        return;
                    }

                    case "/api/view":
                    case "/api/mode":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        string mode = GetQueryVal(query, "mode") ?? GetStr(doc.RootElement, "mode", "Dual");
                        _setViewMode(mode);
                        await WriteJsonAsync(stream, new { ok = true, viewMode = _getViewMode() }, ct);
                        return;
                    }

                    case "/api/theme":
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        string theme = GetQueryVal(query, "theme") ?? GetStr(doc.RootElement, "theme", "HeroUI");
                        _setThemeName(theme);
                        await WriteJsonAsync(stream, new { ok = true, theme = _getThemeName() }, ct);
                        return;
                    }

                    case "/api/drawer":
                    {
                        var form = _getHostForm();
                        if (form is not null)
                        {
                            form.Invoke(new Action(() =>
                            {
                                var sbField = form.GetType().GetField("_sidebar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (sbField?.GetValue(form) is Form sb)
                                {
                                    var toggleMethod = sb.GetType().GetMethod("ToggleExpanded");
                                    toggleMethod?.Invoke(sb, null);
                                }
                            }));
                        }
                        await WriteJsonAsync(stream, new { ok = true, sidebar = "toggled" }, ct);
                        return;
                    }

                    case "/api/otp":
                    {
                        var (code, source) = _getLatestOtp();
                        await WriteJsonAsync(stream, new { ok = true, code, source }, ct);
                        return;
                    }

                    default:
                        await WriteJsonAsync(stream, new { ok = true, service = "WAILink.Agent.dll", port = Port }, ct);
                        return;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    using var stream = client.GetStream();
                    await WriteJsonAsync(stream, new { ok = false, error = ex.Message }, ct);
                }
                catch { }
            }
        }
    }

    async Task<byte[]?> CaptureHostWindowPngAsync(CancellationToken ct)
    {
        var form = _getHostForm();
        if (form is null || form.IsDisposed) return null;
        try
        {
            byte[]? phonePng = null;
            string mode = _getViewMode();
            if (mode.Contains("android", StringComparison.OrdinalIgnoreCase) && _adb.Serial is not null)
            {
                phonePng = await _adb.CaptureScreenshotPngAsync(ct);
            }
            else if (mode.Contains("iphone", StringComparison.OrdinalIgnoreCase) && _iphoneCtrl.IsWdaConnected)
            {
                form.Invoke(() =>
                {
                    var ipField = form.GetType().GetField("_iphonePanel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var ipPanel = ipField?.GetValue(form);
                    var getFrameMethod = ipPanel?.GetType().GetMethod("GetCurrentFramePng");
                    if (getFrameMethod?.Invoke(ipPanel, null) is byte[] liveBytes && liveBytes.Length > 0)
                        phonePng = liveBytes;
                });
                if (phonePng is null)
                    phonePng = await _iphoneCtrl.CaptureScreenshotPngAsync(ct);
            }

            byte[]? result = null;
            form.Invoke(() =>
            {
                int totalW = form.Width;
                int totalH = form.Height;
                using var bmp = new Bitmap(totalW, totalH);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));

                if (phonePng is { Length: > 0 })
                {
                    var stageField = form.GetType().GetField("_stage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (stageField?.GetValue(form) is Control stage && stage.Width > 20 && stage.Height > 20)
                    {
                        using var phoneMs = new MemoryStream(phonePng);
                        using var phoneImg = Image.FromStream(phoneMs);
                        using var g = Graphics.FromImage(bmp);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(phoneImg, stage.Bounds);
                    }
                }

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                result = ms.ToArray();
            });
            return result;
        }
        catch
        {
            return null;
        }
    }

    static string? GetQueryVal(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(WebUtility.UrlDecode(kv[0]), key, StringComparison.OrdinalIgnoreCase))
                return WebUtility.UrlDecode(kv[1]);
        }
        return null;
    }

    static string GetStr(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

    static int GetInt(JsonElement el, string prop, int fallback) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    static async Task WriteJsonAsync(NetworkStream stream, object obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(json);
        var hdr = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(hdr, ct);
        await stream.WriteAsync(bytes, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
