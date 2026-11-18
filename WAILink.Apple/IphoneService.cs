using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PhoneBridge;

public sealed record IphoneMessageEvent(
    string Sender,
    string Body,
    string? OtpCode,
    string SourceChannel,
    DateTime ReceivedAt
);

/// <summary>
/// Standalone iPhone Continuity, Bluetooth ANCS/MAP/PBAP/HFP &amp; Two-Way File/Clipboard/Camera Hub (WAILink.Apple.dll).
/// Runs on port 51820 and provides:
/// 1) Instant SMS / Notification / OTP ingestion (/sms, /notify)
/// 2) Two-Way PC &lt;-&gt; iPhone File &amp; Photo Drop (/files, /upload, /download)
/// 3) Two-Way Universal Clipboard Sync (/clipboard)
/// 4) 1-Click "Snap from iPhone Camera" directly to PC &amp; Clipboard (/snap)
/// 5) 1-Click Deep-Link App &amp; Call Launcher (/open)
/// 6) Windows Bluetooth PnP &amp; BLE ANCS/MAP/PBAP/HFP presence monitor
/// </summary>
public sealed class IphoneService : IDisposable
{
    public const int WebhookPort = 51820;

    // The phone page listens on the local network, so every request must prove it is allowed:
    // it comes from the paired iPhone's address, or it carries this random key (in ?k= or the
    // cookie the page sets). Anyone else on the network gets 403.
    public static string AccessKey { get; } = LoadOrCreateKey();
    public static string PageUrl => $"http://{GetLocalLanIp()}:{WebhookPort}/?k={AccessKey}";

    static string LoadOrCreateKey()
    {
        string file = AppPaths.DataFile("phone-page-key.txt");
        try
        {
            if (File.Exists(file))
            {
                string saved = File.ReadAllText(file).Trim();
                if (saved.Length >= 16) return saved;
            }
            string key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            File.WriteAllText(file, key);
            return key;
        }
        catch { return Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(); }
    }

    static bool IsAllowed(TcpClient client, string path, string headers, out bool viaQuery)
    {
        viaQuery = ExtractQueryParam(path, "k") == AccessKey;
        if (viaQuery) return true;
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("wl=" + AccessKey, StringComparison.Ordinal)) return true;
        }
        try
        {
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            string phoneIp = File.ReadAllText(AppPaths.DataFile("iphone-ip.txt")).Trim();
            return phoneIp.Length > 0 && remote.ToString() == phoneIp;
        }
        catch { return false; }
    }

    public static string SharedDir => AppPaths.Shared;
    public static string DownloadsDir => AppPaths.Downloads;
    public static string SnapsDir => AppPaths.Snaps;

    TcpListener? _listener;
    readonly CancellationTokenSource _cts = new();

    public bool IsListening { get; private set; }
    public bool IsBluetoothIphonePresent { get; private set; }
    public string? BluetoothIphoneName { get; private set; }
    public string BluetoothProfilesSummary => IsBluetoothIphonePresent
        ? $"{BluetoothIphoneName} · ANCS/MAP/PBAP/HFP"
        : "BLE Continuity Ready";
    public IphoneMessageEvent? LastMessage { get; private set; }
    public string LastClipboardText { get; set; } = "";
    public string? PendingDeepLink { get; set; }

    public event Action<IphoneMessageEvent>? OnMessageReceived;
    public event Action<string>? OnFileReceivedFromIphone;
    public event Action<string>? OnCameraSnapReceived;
    public event Action<string>? OnClipboardReceived;

    public IphoneService()
    {
        Directory.CreateDirectory(SharedDir);
        Directory.CreateDirectory(DownloadsDir);
        Directory.CreateDirectory(SnapsDir);
    }

    public void SimulateTestOtp(string code, string sender, string body)
    {
        var evt = new IphoneMessageEvent(
            Sender: sender,
            Body: body,
            OtpCode: code,
            SourceChannel: "iPhone Continuity Relay",
            ReceivedAt: DateTime.Now
        );
        LastMessage = evt;
        OnMessageReceived?.Invoke(evt);
    }

    /// <summary>Stages a local PC file so the iPhone can immediately download/AirDrop it over the Continuity Hub.</summary>
    public string ShareFileToIphone(string localFilePath)
    {
        Directory.CreateDirectory(SharedDir);
        var name = Path.GetFileName(localFilePath);
        var dest = Path.Combine(SharedDir, name);
        File.Copy(localFilePath, dest, overwrite: true);
        return $"http://{GetLocalLanIp()}:{WebhookPort}/download?name={Uri.EscapeDataString(name)}&k={AccessKey}";
    }

    public void Start()
    {
        _ = RunWebhookServerAsync(_cts.Token);
        _ = RunBluetoothMonitorAsync(_cts.Token);
    }

    public static string GetLocalLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    {
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                            return ip;
                    }
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    async Task RunWebhookServerAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, WebhookPort);
            _listener.Start();
            IsListening = true;

            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { IsListening = false; }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using var stream = client.GetStream();
                var buffer = new byte[65536];
                int read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) return;

                var rawHeaderCheck = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 4096));
                var headerEnd = rawHeaderCheck.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var firstLine = rawHeaderCheck.Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "GET";
                string path = parts.Length > 1 ? parts[1] : "/";

                // Read remaining body bytes if Content-Length is present
                int headerByteLen = headerEnd >= 0 ? Encoding.UTF8.GetByteCount(rawHeaderCheck[..(headerEnd + 4)]) : read;
                int contentLength = 0;
                foreach (var line in rawHeaderCheck.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line["Content-Length:".Length..].Trim(), out var cl))
                    {
                        contentLength = cl;
                    }
                }

                using var bodyStream = new MemoryStream();
                if (read > headerByteLen)
                    bodyStream.Write(buffer, headerByteLen, read - headerByteLen);
                while (bodyStream.Length < contentLength)
                {
                    int n = await stream.ReadAsync(buffer, ct);
                    if (n <= 0) break;
                    bodyStream.Write(buffer, 0, n);
                }
                var bodyBytes = bodyStream.ToArray();
                string bodyText = Encoding.UTF8.GetString(bodyBytes);

                if (!IsAllowed(client, path, headerEnd >= 0 ? rawHeaderCheck[..headerEnd] : rawHeaderCheck, out bool viaQuery))
                {
                    await WriteHttpResponseAsync(stream, "403 Forbidden", "text/plain; charset=utf-8",
                        "WAI Link: open the phone page link shown by WAI Link (it includes the access key).", ct);
                    return;
                }

                // 1) Download shared PC file to iPhone
                if (path.StartsWith("/download", StringComparison.OrdinalIgnoreCase))
                {
                    var name = ExtractQueryParam(path, "name");
                    var safeName = Path.GetFileName(name ?? "");
                    var filePath = Path.Combine(SharedDir, safeName);
                    if (!string.IsNullOrEmpty(safeName) && File.Exists(filePath))
                    {
                        var bytes = await File.ReadAllBytesAsync(filePath, ct);
                        var hdr = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/octet-stream\r\n" +
                            $"Content-Disposition: attachment; filename=\"{safeName}\"\r\n" +
                            $"Content-Length: {bytes.Length}\r\n" +
                            "Connection: close\r\n\r\n");
                        await stream.WriteAsync(hdr, ct);
                        await stream.WriteAsync(bytes, ct);
                        return;
                    }
                }

                // 2) Upload file or photo from iPhone to PC
                if (path.StartsWith("/upload", StringComparison.OrdinalIgnoreCase) && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(ExtractQueryParam(path, "name") ?? $"iphone_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
                    var outPath = Path.Combine(DownloadsDir, name);
                    await File.WriteAllBytesAsync(outPath, bodyBytes, ct);
                    OnFileReceivedFromIphone?.Invoke(outPath);
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", $"{{\"status\":\"saved\",\"path\":\"{outPath.Replace("\\", "\\\\")}\"}}", ct);
                    return;
                }

                // 3) 1-Click Camera Snap from iPhone to PC
                if (path.StartsWith("/snap", StringComparison.OrdinalIgnoreCase) && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    var outPath = Path.Combine(SnapsDir, $"iphone_snap_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                    await File.WriteAllBytesAsync(outPath, bodyBytes, ct);
                    OnCameraSnapReceived?.Invoke(outPath);
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", $"{{\"status\":\"snapped\",\"path\":\"{outPath.Replace("\\", "\\\\")}\"}}", ct);
                    return;
                }

                // 4) Two-Way Universal Clipboard Sync
                if (path.StartsWith("/clipboard", StringComparison.OrdinalIgnoreCase))
                {
                    if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        LastClipboardText = bodyText.Trim();
                        OnClipboardReceived?.Invoke(LastClipboardText);
                        await WriteHttpResponseAsync(stream, "200 OK", "application/json", "{\"status\":\"synced\"}", ct);
                        return;
                    }
                    await WriteHttpResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", LastClipboardText, ct);
                    return;
                }

                // 5) Poll / Trigger Deep-Link on iPhone
                if (path.StartsWith("/pending-open", StringComparison.OrdinalIgnoreCase))
                {
                    var url = PendingDeepLink ?? "";
                    PendingDeepLink = null;
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", $"{{\"url\":\"{url}\"}}", ct);
                    return;
                }

                // 6) SMS / Notification / OTP Ingestion
                if (path.StartsWith("/sms", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/notify", StringComparison.OrdinalIgnoreCase) ||
                    method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    var (sender, msgText) = ParseIncomingPayload(path, bodyText);
                    if (!string.IsNullOrWhiteSpace(msgText))
                    {
                        var otp = ExtractOtpCode(msgText);
                        var evt = new IphoneMessageEvent(
                            Sender: string.IsNullOrWhiteSpace(sender) ? "iPhone SMS" : sender.Trim(),
                            Body: msgText.Trim(),
                            OtpCode: otp,
                            SourceChannel: "iPhone Continuity Hub",
                            ReceivedAt: DateTime.Now
                        );
                        LastMessage = evt;
                        OnMessageReceived?.Invoke(evt);
                    }

                    var jsonResp = "{\"status\":\"ok\",\"app\":\"WAI Link v2.0\"}";
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", jsonResp, ct);
                    return;
                }

                // 7) Serve clean iPhone Continuity mobile page (File Drop, Clipboard, OTP Sync)
                string ip = GetLocalLanIp();
                string html = BuildContinuityHtml(ip, WebhookPort);
                // Remember the key in a cookie so the page's own requests (upload, clipboard) are allowed.
                await WriteHttpResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, ct,
                    viaQuery ? $"Set-Cookie: wl={AccessKey}; Path=/; Max-Age=31536000; SameSite=Strict\r\n" : "");
            }
            catch { }
        }
    }

    static string? ExtractQueryParam(string path, string key)
    {
        int qIdx = path.IndexOf('?');
        if (qIdx < 0 || qIdx + 1 >= path.Length) return null;
        foreach (var kv in path[(qIdx + 1)..].Split('&'))
        {
            var pair = kv.Split('=', 2);
            if (pair.Length == 2 && string.Equals(WebUtility.UrlDecode(pair[0]), key, StringComparison.OrdinalIgnoreCase))
                return WebUtility.UrlDecode(pair[1]);
        }
        return null;
    }

    public static string? ExtractOtpCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?i)(?:code|otp|pin|passcode|verification|confirm|berifikasi|kode)[^\d]{0,18}(\b\d{4,8}\b)");
        if (m.Success) return m.Groups[1].Value;
        var fallback = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{4,8})\b");
        return fallback.Success ? fallback.Groups[1].Value : null;
    }

    static (string Sender, string Body) ParseIncomingPayload(string path, string body)
    {
        string sender = "iPhone";
        string text = "";

        int qIdx = path.IndexOf('?');
        if (qIdx >= 0 && qIdx + 1 < path.Length)
        {
            foreach (var kv in path[(qIdx + 1)..].Split('&'))
            {
                var pair = kv.Split('=', 2);
                if (pair.Length != 2) continue;
                var k = WebUtility.UrlDecode(pair[0]).ToLowerInvariant();
                var v = WebUtility.UrlDecode(pair[1]);
                if (k is "from" or "sender" or "title") sender = v;
                else if (k is "body" or "text" or "msg" or "message" or "code") text = v;
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            var trimmed = body.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sender", out var s) || root.TryGetProperty("from", out s) || root.TryGetProperty("title", out s))
                        sender = s.GetString() ?? sender;
                    if (root.TryGetProperty("body", out var b) || root.TryGetProperty("text", out b) || root.TryGetProperty("message", out b))
                        text = b.GetString() ?? text;
                }
                catch { text = trimmed; }
            }
            else if (trimmed.Contains('=') && !trimmed.Contains(' '))
            {
                foreach (var kv in trimmed.Split('&'))
                {
                    var pair = kv.Split('=', 2);
                    if (pair.Length == 2)
                    {
                        var k = WebUtility.UrlDecode(pair[0]).ToLowerInvariant();
                        var v = WebUtility.UrlDecode(pair[1]);
                        if (k is "from" or "sender") sender = v;
                        else if (k is "body" or "text" or "message") text = v;
                    }
                }
            }
            else
            {
                text = trimmed;
            }
        }
        return (sender, text);
    }

    static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string contentType, string content, CancellationToken ct, string extraHeaders = "")
    {
        var payload = Encoding.UTF8.GetBytes(content);
        // No CORS header: only the page itself (same origin) may read responses, never other websites.
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            extraHeaders +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    static string BuildSetupHtml(string ip, int port) => BuildContinuityHtml(ip, port);

    public static string BuildContinuityHtml(string ip, int port)
    {
        var sharedFiles = Directory.Exists(SharedDir)
            ? Directory.GetFiles(SharedDir).Select(Path.GetFileName).Where(f => !string.IsNullOrEmpty(f)).ToList()
            : [];
        var sbFiles = new StringBuilder();
        if (sharedFiles.Count == 0)
        {
            sbFiles.Append("<p style='color:#71717A;font-size:13px'>Drag any file onto the iPhone screen in WAI Link on your PC to download it here.</p>");
        }
        else
        {
            foreach (var f in sharedFiles)
            {
                var esc = Uri.EscapeDataString(f!);
                sbFiles.Append($"<a href='/download?name={esc}' style='display:block;background:#18181B;border:1px solid #27272A;color:#38BDF8;padding:10px 12px;border-radius:8px;margin:6px 0;text-decoration:none;font-weight:600'>{WebUtility.HtmlEncode(f)}</a>");
            }
        }

        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>WAI Link - iPhone Continuity</title>
        <style>
        body{background:#09090B;color:#EEF0F5;font-family:-apple-system,BlinkMacSystemFont,sans-serif;padding:18px;margin:0}
        .card{background:#18181B;border:1px solid #27272A;border-radius:14px;padding:16px;margin-bottom:14px}
        h2{margin:0 0 10px 0;font-size:16px;font-weight:600;color:#F4F4F5}
        button, .btn{background:#006FEE;color:#fff;border:none;border-radius:10px;padding:12px 16px;font-weight:600;font-size:14px;width:100%;cursor:pointer;display:block;text-align:center;box-sizing:border-box;margin-top:8px}
        .btn-secondary{background:#27272A;color:#E4E4E7}
        input[type=text]{width:100%;box-sizing:border-box;padding:10px 12px;border-radius:8px;border:1px solid #3F3F46;background:#09090B;color:#fff;font-size:14px;margin-bottom:8px}
        </style>
        </head>
        <body>
        <div class="card">
          <h2>File Transfer</h2>
          <input type="file" id="fileInput" style="display:none" onchange="uploadFile(this)" />
          <button onclick="document.getElementById('fileInput').click()">Send File to PC</button>
          <div style="margin-top:12px"><b>Files Shared from PC:</b>{{sbFiles}}</div>
        </div>
        <div class="card">
          <h2>Clipboard Sync</h2>
          <input type="text" id="clipBox" placeholder="Paste or type text to sync with PC..." />
          <button onclick="sendClip()">Send Clipboard to PC</button>
          <button class="btn-secondary" onclick="pullClip()">Pull PC Clipboard</button>
        </div>
        <div class="card">
          <h2>OTP and Verification Code Relay</h2>
          <input type="text" id="otpBox" placeholder="Enter verification code or OTP..." />
          <button onclick="sendOtp()">Send OTP to PC</button>
        </div>
        <script>
        async function uploadFile(inp){
          if(!inp.files.length)return;
          const f=inp.files[0];
          await fetch('/upload?name='+encodeURIComponent(f.name),{method:'POST',body:f});
          alert('Saved '+f.name+' to PC.');
        }
        async function sendClip(){
          const v=document.getElementById('clipBox').value;
          await fetch('/clipboard',{method:'POST',body:v});
          alert('Synced to PC clipboard.');
        }
        async function pullClip(){
          const r=await fetch('/clipboard');
          document.getElementById('clipBox').value=await r.text();
        }
        async function sendOtp(){
          const v=document.getElementById('otpBox').value.trim();
          if(!v)return;
          await fetch('/sms?from=iPhone&body='+encodeURIComponent('Your verification code is '+v));
          alert('Sent OTP '+v+' to PC.');
        }
        setInterval(async()=>{
          try{
            const r=await fetch('/pending-open');
            const j=await r.json();
            if(j.url) window.location.href=j.url;
          }catch(e){}
        },2000);
        </script>
        </body>
        </html>
        """;
    }

    async Task RunBluetoothMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? foundName = null;
                using var bthKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
                if (bthKey is not null)
                {
                    foreach (var subName in bthKey.GetSubKeyNames())
                    {
                        using var devKey = bthKey.OpenSubKey(subName);
                        if (devKey?.GetValue("Name") is byte[] nameBytes && nameBytes.Length > 0)
                        {
                            int len = Array.IndexOf(nameBytes, (byte)0);
                            if (len < 0) len = nameBytes.Length;
                            string devName = Encoding.UTF8.GetString(nameBytes, 0, len).Trim();
                            if (devName.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
                                devName.Contains("Apple", StringComparison.OrdinalIgnoreCase))
                            {
                                foundName = devName;
                                break;
                            }
                        }
                    }
                }
                IsBluetoothIphonePresent = !string.IsNullOrEmpty(foundName);
                BluetoothIphoneName = foundName;
            }
            catch { }
            try { await Task.Delay(15000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
