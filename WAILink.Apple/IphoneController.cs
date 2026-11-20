using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PhoneBridge;

/// <summary>
/// iPhone Remote Control Engine (WAILink.Apple.dll):
/// - Direct touch, swipe, Home, Back, App Switcher, and keyboard control via WebDriverAgent (WDA).
/// - Automatically starts the local USB WDA bridge (tidevice / pymobiledevice3) when available
///   and auto-discovers WDA on 127.0.0.1:8100 or the iPhone's LAN Wi-Fi IP on port 8100.
/// </summary>
public sealed class IphoneController : IDisposable
{
    readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8,
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromMilliseconds(2500)
    };
    readonly SemaphoreSlim _inputLock = new(1, 1);
    readonly SemaphoreSlim _connectLock = new(1, 1);
    readonly object _bridgeProcessLock = new();
    readonly object _keyQueueLock = new();
    readonly StringBuilder _pendingKeys = new();
    bool _keyFlushRunning;
    bool _selectAllPending;
    string _wdaBaseUrl = "http://127.0.0.1:8100";
    string _mjpegHost = "127.0.0.1";
    // The phone's Wi-Fi address, learned from WebDriverAgent and remembered for the next start.
    static string LanIpFile => AppPaths.DataFile("iphone-ip.txt");
    string _lastKnownLanIp = ReadLanIp();

    static string ReadLanIp()
    {
        try { return File.Exists(LanIpFile) ? File.ReadAllText(LanIpFile).Trim() : ""; }
        catch { return ""; }
    }

    void RememberLanIp(string ip)
    {
        if (string.Equals(ip, _lastKnownLanIp, StringComparison.Ordinal)) return;
        _lastKnownLanIp = ip;
        try { File.WriteAllText(LanIpFile, ip); } catch { }
    }
    string? _sessionId;
    string? _configuredSessionId;
    bool _wdaConnected;
    int _logicalWidth = 414;
    int _logicalHeight = 896;
    Process? _wdaBridgeProcess;
    Process? _wdaForwardProcess;
    Process? _wdaMjpegForwardProcess;
    bool _discovering;

    public string WdaBaseUrl
    {
        get => _wdaBaseUrl;
        set => _wdaBaseUrl = value.TrimEnd('/');
    }

    public string MjpegHost => _mjpegHost;

    public bool WdaConnected => _wdaConnected;
    public bool IsWdaConnected => _wdaConnected;
    public bool IsAirPlayRunning { get; set; }
    public string? PendingDeepLinkUrl { get; set; }
    public string? WdaSessionId => _sessionId;
    public int LogicalWidth => _logicalWidth;
    public int LogicalHeight => _logicalHeight;

    public event Action<string>? OnStatusLog;

    public void SetWdaEndpoint(string hostOrUrl, int port = 8100)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl)) return;
        hostOrUrl = hostOrUrl.Trim();
        if (!hostOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !hostOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _wdaBaseUrl = hostOrUrl.Contains(':')
                ? $"http://{hostOrUrl}"
                : $"http://{hostOrUrl}:{port}";
        }
        else
        {
            _wdaBaseUrl = hostOrUrl.TrimEnd('/');
        }
    }

    static bool IsLocalPortOpen(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var ar = tcp.BeginConnect("127.0.0.1", port, null, null);
            return ar.AsyncWaitHandle.WaitOne(120) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    readonly ManualResetEventSlim _wdaRunnerReady = new(false);
    readonly object _hidWriteLock = new();
    StreamWriter? _wdaBridgeStdin;
    bool _disposed;
    bool _bridgeWanted;
    volatile bool _hidBridgeReady;
    volatile bool _hidTouchReady;
    volatile bool _hidContactReady;
    volatile bool _hidButtonAckReady;
    volatile bool _clipboardReady;
    volatile bool _videoPaused;
    readonly ManualResetEventSlim _videoLive = new(false);

    /// <summary>
    /// True while the iOS 27+ hardware screen stream is paused. iOS blocks the camera,
    /// microphone and Face ID while that stream runs; pausing frees them and the mirror
    /// falls back to WDA screenshots. Kept across bridge restarts.
    /// </summary>
    public bool IsVideoPaused => _videoPaused;

    public void SetVideoPaused(bool paused)
    {
        _videoPaused = paused;
        TryWriteHidCommand(paused ? "VIDEO_OFF" : "VIDEO_ON");
    }

    // After QUIT the bridge ends the screen stream first (HID_VIDEO_PAUSED). Wait for that
    // before killing it, or the phone can stay "observed remotely" with its camera blocked.
    void WaitForVideoStop() => SpinWait.SpinUntil(() => !_videoLive.IsSet || _wdaBridgeProcess is null or { HasExited: true }, 5000);
    string? _phoneClipboard;
    TaskCompletionSource<bool>? _clipboardRead;
    readonly SemaphoreSlim _clipboardReadLock = new(1, 1);
    public event Action<string>? ClipboardTextChanged;
    readonly ConcurrentQueue<(string Button, TaskCompletionSource<bool>? Completion)> _hidButtons = new();

    void FailPendingHidButtons()
    {
        while (_hidButtons.TryDequeue(out var pending)) pending.Completion?.TrySetResult(false);
    }

    // Set when a bridge process starts; cleared once its runner reports ready.
    volatile bool _awaitingRunnerReady;
    volatile bool _runnerEverReady;
    static string BridgeLogPath => Path.Combine(AppPaths.Logs, "wda-bridge.log");
    readonly object _bridgeLogLock = new();

    void LogBridgeLine(string text)
    {
        try
        {
            lock (_bridgeLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BridgeLogPath)!);
                var info = new FileInfo(BridgeLogPath);
                if (info.Exists && info.Length > 4_000_000) File.Move(BridgeLogPath, BridgeLogPath + ".old", true);
                File.AppendAllText(BridgeLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch { }
    }

    // A relaunched WDA runner is a new process: no session and default MJPEG
    // settings (10 fps). Re-create the session and re-apply the tuning as soon
    // as it answers, instead of waiting for the video stream to stall.
    async Task ResyncAfterRunnerRestartAsync()
    {
        for (int i = 0; i < 40 && !_disposed; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(1500);
                using var res = await _http.GetAsync($"{_wdaBaseUrl}/status", cts.Token);
                if (res.IsSuccessStatusCode) break;
            }
            catch { }
            await Task.Delay(500);
        }
        if (_disposed) return;
        bool ok = await EnsureSessionHealthyAsync();
        LogBridgeLine($"[WAI Link] runner restarted, session resync {(ok ? "ok" : "failed")}");
    }

    void OnBridgeLine(string? line)
    {
        if (line is not null) LogBridgeLine(line);
        if (line == "HID_CLIPBOARD_READY") _clipboardReady = true;
        if (line == "HID_VIDEO_READY") _videoLive.Set();
        if (line == "HID_VIDEO_PAUSED" || line == "HID_VIDEO_UNSUPPORTED" || line?.StartsWith("HID_VIDEO_FAILED", StringComparison.Ordinal) == true)
            _videoLive.Reset();
        if (line == "HID_CLIPBOARD_EMPTY") _phoneClipboard = null;
        if (line?.StartsWith("HID_CLIPBOARD ", StringComparison.Ordinal) == true)
        {
            try
            {
                _phoneClipboard = Encoding.UTF8.GetString(Convert.FromBase64String(line[14..]));
                ClipboardTextChanged?.Invoke(_phoneClipboard);
            }
            catch (FormatException) { }
        }
        if (line == "HID_CLIPBOARD_READ_OK") _clipboardRead?.TrySetResult(true);
        if (line == "HID_CLIPBOARD_READ_FAILED") _clipboardRead?.TrySetResult(false);
        if (line == "HID_BRIDGE_LOST")
        {
            _clipboardReady = false;
            _clipboardRead?.TrySetResult(false);
            _hidBridgeReady = false;
            _hidTouchReady = false;
            _hidButtonAckReady = false;
            _hidContactReady = false;
            FailPendingHidButtons();
            return;
        }
        if (line == "HID_BUTTON_ACK_READY") _hidButtonAckReady = true;
        if (line == "HID_TOUCH_READY") _hidTouchReady = true;
        if (line == "HID_CONTACT_READY") _hidContactReady = true;
        if (line == "HID_TOUCH_UNAVAILABLE") { _hidTouchReady = false; _hidContactReady = false; }
        if (line?.StartsWith("HID_BUTTON_OK ", StringComparison.Ordinal) == true &&
            _hidButtons.TryDequeue(out var pending))
            pending.Completion?.TrySetResult(line[14..] == pending.Button);
        if (line?.Contains("HID_BRIDGE_READY", StringComparison.OrdinalIgnoreCase) == true)
        {
            _hidBridgeReady = true;
            SendScreenPixels(); // a restarted bridge has not heard it yet
        }
        if (line is not null &&
            (line.Contains("HID_BRIDGE_READY", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("testCaseDidStart", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("ServerURLHere", StringComparison.OrdinalIgnoreCase)))
        {
            _wdaRunnerReady.Set();
            if (_awaitingRunnerReady)
            {
                _awaitingRunnerReady = false;
                // The first runner is configured by ConnectWdaAsync; later ones are relaunches.
                if (_runnerEverReady) _ = Task.Run(ResyncAfterRunnerRestartAsync);
                _runnerEverReady = true;
            }
        }
    }

    /// <summary>
    /// Starts the local WDA runner (with --userspace for Wi-Fi RemotePairing + USB).
    /// </summary>
    public bool EnsureLocalWdaBridgeStarted()
    {
        if (_disposed) return false;
        _bridgeWanted = true;
        bool startedNew = false;
        lock (_bridgeProcessLock)
        {
            try
            {
                string pyExe = AppPaths.Python;
                string legacyBridgeScript = Path.Combine(AppPaths.IphoneBridge, "wda_hid_bridge.py");
                string buttonBridgeScript = Path.Combine(AppPaths.IphoneBridge, "wda_hid_buttons.py");
                bool acknowledgedButtons = File.Exists(buttonBridgeScript);
                string bridgeScript = acknowledgedButtons ? buttonBridgeScript : legacyBridgeScript;
                if (!File.Exists(pyExe)) return false;
                bool useFastBridge = File.Exists(bridgeScript);

                if (_wdaBridgeProcess is null || _wdaBridgeProcess.HasExited)
                {
                    _awaitingRunnerReady = true;
                    LogBridgeLine("[WAI Link] starting WDA bridge");
                    _wdaRunnerReady.Reset();
                    _hidBridgeReady = false;
                    _hidTouchReady = false;
                    _hidButtonAckReady = false;
                    _hidContactReady = false;
                    FailPendingHidButtons();
                    var bridgeProc = StartBackgroundPmd3(
                        pyExe,
                        useFastBridge
                            ? acknowledgedButtons
                                ? $"-I -B -u \"{bridgeScript}\" \"{legacyBridgeScript}\" auto auto"
                                : $"-I -B -u \"{bridgeScript}\" auto auto"
                            : $"{Pmd3} developer dvt xcuitest --userspace com.facebook.WebDriverAgentRunner.xctrunner",
                        redirectInput: true,
                        onLine: OnBridgeLine);
                    if (bridgeProc is not null)
                    {
                        lock (_hidWriteLock)
                        {
                            try
                            {
                                _wdaBridgeStdin = bridgeProc.StandardInput;
                                _wdaBridgeStdin.AutoFlush = true;
                            }
                            catch
                            {
                                _wdaBridgeStdin = null;
                            }
                        }
                        try
                        {
                            bridgeProc.EnableRaisingEvents = true;
                            bridgeProc.Exited += (_, _) =>
                            {
                                try { LogBridgeLine($"[WAI Link] WDA bridge exited (code {bridgeProc.ExitCode})"); } catch { LogBridgeLine("[WAI Link] WDA bridge exited"); }
                                _hidBridgeReady = false;
                                _hidTouchReady = false;
                                _hidButtonAckReady = false;
                                _hidContactReady = false;
                                _clipboardReady = false;
                                _videoLive.Reset();
                                _clipboardRead?.TrySetResult(false);
                                FailPendingHidButtons();
                                lock (_hidWriteLock) { _wdaBridgeStdin = null; }
                                _wdaRunnerReady.Set();
                                if (!_disposed && _bridgeWanted)
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        await Task.Delay(800);
                                        if (!_disposed && _bridgeWanted) EnsureLocalWdaBridgeStarted();
                                    });
                                }
                            };
                        }
                        catch { }
                    }
                    _wdaBridgeProcess = bridgeProc;
                    startedNew = true;
                }
            }
            catch { }
        }
        return startedNew;
    }

    void EnsureUsbForwardersStarted()
    {
        if (_disposed) return;
        lock (_bridgeProcessLock)
        {
            try
            {
                string pyExe = AppPaths.Python;
                if (!File.Exists(pyExe)) return;

                if (!IsLocalPortOpen(8100) && (_wdaForwardProcess is null || _wdaForwardProcess.HasExited))
                {
                    _wdaForwardProcess = StartBackgroundPmd3(pyExe, $"{Pmd3} --reconnect usbmux forward 8100 8100");
                }

                if (!IsLocalPortOpen(9100) && (_wdaMjpegForwardProcess is null || _wdaMjpegForwardProcess.HasExited))
                {
                    _wdaMjpegForwardProcess = StartBackgroundPmd3(pyExe, $"{Pmd3} --reconnect usbmux forward 9100 9100");
                }
            }
            catch { }
        }
    }

    /// <summary>Arguments that run the pymobiledevice3 command line with the bundled Python.</summary>
    public const string Pmd3 = "-I -m pymobiledevice3";

    Process? StartBackgroundPmd3(string exePath, string arguments, bool redirectInput = false, Action<string?>? onLine = null)
    {
        var psi = new ProcessStartInfo(exePath, arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        AppPaths.ApplyIphoneEnvironment(psi);
        if (!string.IsNullOrWhiteSpace(_lastKnownLanIp))
            psi.EnvironmentVariables["WDA_WIFI_HOST"] = _lastKnownLanIp;
        if (_videoPaused) psi.EnvironmentVariables["WAILINK_VIDEO_PAUSED"] = "1";
        var proc = Process.Start(psi);
        if (proc is not null)
        {
            AssignToChildJob(proc);
            proc.OutputDataReceived += (_, e) => onLine?.Invoke(e.Data);
            proc.ErrorDataReceived += (_, e) => onLine?.Invoke(e.Data);
            try { proc.BeginOutputReadLine(); } catch { }
            try { proc.BeginErrorReadLine(); } catch { }
        }
        return proc;
    }

    bool TryWriteHidCommand(string command, TaskCompletionSource<bool>? completion = null)
    {
        lock (_hidWriteLock)
        {
            try
            {
                // A running XCUITest runner is not necessarily a HID command consumer.
                // Only the dedicated HID-ready handshake permits this transport.
                if (_hidBridgeReady &&
                    _wdaBridgeProcess is { HasExited: false } && _wdaBridgeStdin is not null)
                {
                    if (_hidButtonAckReady && command.StartsWith("BTN ", StringComparison.Ordinal))
                        _hidButtons.Enqueue((command[4..], completion));
                    _wdaBridgeStdin.WriteLine(command);
                    _wdaBridgeStdin.Flush();
                    return true;
                }
            }
            catch { _hidBridgeReady = false; FailPendingHidButtons(); }
            return false;
        }
    }

    async Task<bool> SendAcknowledgedHidButtonAsync(string button, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryWriteHidCommand($"BTN {button}", completion)) return false;
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), ct); }
        catch (TimeoutException) { return false; }
    }

    static (int Hx, int Hy) MapPanelToHidNormalized(int px, int py, Size panelSize)
    {
        int w = Math.Max(1, panelSize.Width);
        int h = Math.Max(1, panelSize.Height);
        int hx = Math.Clamp((int)Math.Round((double)px / w * 65535.0), 0, 65535);
        int hy = Math.Clamp((int)Math.Round((double)py / h * 65535.0), 0, 65535);
        return (hx, hy);
    }

    (int Hx, int Hy) MapLogicalToHidNormalized(int lx, int ly)
    {
        int w = Math.Max(1, _logicalWidth);
        int h = Math.Max(1, _logicalHeight);
        int hx = Math.Clamp((int)Math.Round((double)lx / w * 65535.0), 0, 65535);
        int hy = Math.Clamp((int)Math.Round((double)ly / h * 65535.0), 0, 65535);
        return (hx, hy);
    }

    public bool SendHidTouchDown(int panelX, int panelY, Size panelSize)
    {
        _selectAllPending = false;
        var (hx, hy) = MapPanelToHidNormalized(panelX, panelY, panelSize);
        return TryWriteHidCommand($"DOWN {hx} {hy}");
    }

    public bool SendHidTouchMove(int panelX, int panelY, Size panelSize)
    {
        var (hx, hy) = MapPanelToHidNormalized(panelX, panelY, panelSize);
        return TryWriteHidCommand($"MOVE {hx} {hy}");
    }

    public bool SendHidTouchUp(int panelX, int panelY, Size panelSize, bool brake)
    {
        var (hx, hy) = MapPanelToHidNormalized(panelX, panelY, panelSize);
        return TryWriteHidCommand($"UP {hx} {hy} {(brake ? 1 : 0)}");
    }

    /// <summary>
    /// Connects to WebDriverAgent over Wi-Fi (&lt;iphone-ip&gt;:8100) or USB (127.0.0.1:8100),
    /// ensuring the local --userspace XCUITest bridge is running so touch synthesis never stalls.
    /// </summary>
    public async Task<bool> ConnectWdaAsync(CancellationToken ct = default)
    {
        if (!await _connectLock.WaitAsync(20000, ct))
            return _wdaConnected;

        try
        {
            await Task.Run(EnsureLocalWdaBridgeStarted, ct);
            if (!_wdaRunnerReady.IsSet && _wdaBridgeProcess is { HasExited: false })
            {
                await Task.Run(() => _wdaRunnerReady.Wait(12000, ct), ct);
            }

            int lanAttempts = 10;
            for (int i = 0; i < lanAttempts; i++)
            {
                if (!string.IsNullOrWhiteSpace(_lastKnownLanIp) &&
                    await TryConnectEndpointAsync($"http://{_lastKnownLanIp}:8100", ct))
                    return true;

                if (!string.Equals(_wdaBaseUrl, "http://127.0.0.1:8100", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_wdaBaseUrl, $"http://{_lastKnownLanIp}:8100", StringComparison.OrdinalIgnoreCase) &&
                    await TryConnectEndpointAsync(_wdaBaseUrl, ct))
                    return true;

                if (i + 1 < lanAttempts)
                    await Task.Delay(350, ct);
            }

            await Task.Run(EnsureUsbForwardersStarted, ct);
            for (int i = 0; i < 3; i++)
            {
                if (await TryConnectEndpointAsync("http://127.0.0.1:8100", ct))
                    return true;
                if (!string.IsNullOrWhiteSpace(_lastKnownLanIp) &&
                    await TryConnectEndpointAsync($"http://{_lastKnownLanIp}:8100", ct))
                    return true;
                await Task.Delay(350, ct);
            }

            string? discoveredUrl = await DiscoverLanWdaUrlAsync(ct);
            if (discoveredUrl is not null && await TryConnectEndpointAsync(discoveredUrl, ct))
                return true;

            _wdaConnected = false;
            return false;
        }
        catch
        {
            _wdaConnected = false;
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    async Task<bool> TryConnectEndpointAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            bool isLoopback = baseUrl.Contains("127.0.0.1", StringComparison.Ordinal);
            using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            statusCts.CancelAfter(isLoopback ? 550 : 1400);

            using var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/status");
            using var res = await _http.SendAsync(statusReq, statusCts.Token);
            if (!res.IsSuccessStatusCode) return false;

            var json = await res.Content.ReadAsStringAsync(statusCts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                _sessionId = sid.GetString();

            string? reportedLanIp = null;
            if (doc.RootElement.TryGetProperty("value", out var valEl) &&
                valEl.TryGetProperty("ios", out var iosEl) &&
                iosEl.TryGetProperty("ip", out var ipEl) &&
                ipEl.ValueKind == JsonValueKind.String)
            {
                reportedLanIp = ipEl.GetString();
                if (!string.IsNullOrWhiteSpace(reportedLanIp))
                    RememberLanIp(reportedLanIp);
            }

            if (string.IsNullOrEmpty(_sessionId))
            {
                const string createSessionPayload = """
                {
                  "capabilities": {
                    "alwaysMatch": {
                      "shouldWaitForQuiescence": false,
                      "waitForQuiescence": false,
                      "shouldUseTestManagerForVisibilityDetection": false
                    }
                  }
                }
                """;
                using var sessionContent = new StringContent(createSessionPayload, Encoding.UTF8, "application/json");
                using var sessionRes = await _http.PostAsync($"{baseUrl}/session", sessionContent, ct);
                if (sessionRes.IsSuccessStatusCode)
                {
                    var sJson = await sessionRes.Content.ReadAsStringAsync(ct);
                    using var sDoc = JsonDocument.Parse(sJson);
                    if (sDoc.RootElement.TryGetProperty("sessionId", out var sIdProp))
                        _sessionId = sIdProp.GetString();
                }
            }

            _wdaBaseUrl = baseUrl;
            if (isLoopback)
            {
                _wdaBaseUrl = "http://127.0.0.1:8100";
                _mjpegHost = "127.0.0.1";
            }
            else if (!string.IsNullOrWhiteSpace(reportedLanIp))
            {
                _mjpegHost = reportedLanIp;
            }
            else if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                _mjpegHost = uri.Host;
            }

            if (!string.IsNullOrEmpty(_sessionId) &&
                !string.Equals(_sessionId, _configuredSessionId, StringComparison.Ordinal))
            {
                _configuredSessionId = _sessionId;
                await ApplyLowLatencySettingsAsync(ct);
                await RefreshWindowSizeAsync(ct);
            }

            _wdaConnected = true;
            OnStatusLog?.Invoke("iPhone Control Connected");
            return true;
        }
        catch
        {
            return false;
        }
    }

    async Task<string?> DiscoverLanWdaUrlAsync(CancellationToken ct)
    {
        if (_discovering) return null;
        _discovering = true;
        try
        {
            var candidateIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Check ARP cache first for active neighbor devices (fastest way to find the iPhone on Wi-Fi)
            try
            {
                using var arp = Process.Start(new ProcessStartInfo("arp", "-a")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                });
                if (arp is not null)
                {
                    string arpOut = await arp.StandardOutput.ReadToEndAsync(ct);
                    foreach (var line in arpOut.Split('\n'))
                    {
                        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out var ip))
                        {
                            byte[] b = ip.GetAddressBytes();
                            if (b.Length == 4 && (b[0] == 192 || b[0] == 10 || b[0] == 172) && b[3] != 255 && b[3] != 1)
                                candidateIps.Add(ip.ToString());
                        }
                    }
                }
            }
            catch { }

            // 2. Also add the local /24 subnet of active Wi-Fi/Ethernet adapters
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var b = ua.Address.GetAddressBytes();
                            if (b[0] == 169 && b[1] == 254) continue;
                            string prefix = $"{b[0]}.{b[1]}.{b[2]}.";
                            for (int i = 2; i < 255; i++)
                            {
                                if (i != b[3]) candidateIps.Add(prefix + i);
                            }
                        }
                    }
                }
            }
            catch { }

            using var sem = new SemaphoreSlim(48);
            var tasks = candidateIps.Select(async ip =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    using var tcp = new TcpClient();
                    var connectTask = tcp.ConnectAsync(ip, 8100);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(350, ct));
                    if (completed == connectTask && tcp.Connected)
                        return $"http://{ip}:8100";
                }
                catch { }
                finally { sem.Release(); }
                return null;
            });

            var results = await Task.WhenAll(tasks);
            return results.FirstOrDefault(r => r is not null);
        }
        catch
        {
            return null;
        }
        finally
        {
            _discovering = false;
        }
    }

    public async Task<bool> EnsureSessionHealthyAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected) return await ConnectWdaAsync(ct);
        try
        {
            using var res = await _http.GetAsync($"{_wdaBaseUrl}/status", ct);
            if (!res.IsSuccessStatusCode)
            {
                _wdaConnected = false;
                return await ConnectWdaAsync(ct);
            }
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            string? currentSid = null;
            if (doc.RootElement.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                currentSid = sid.GetString();

            if (string.IsNullOrEmpty(currentSid))
            {
                _sessionId = null;
                return await TryConnectEndpointAsync(_wdaBaseUrl, ct);
            }

            if (!string.Equals(currentSid, _sessionId, StringComparison.Ordinal))
            {
                _sessionId = currentSid;
                await ApplyLowLatencySettingsAsync(ct);
            }
            return true;
        }
        catch
        {
            _wdaConnected = false;
            return await ConnectWdaAsync(ct);
        }
    }

    async Task ApplyLowLatencySettingsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionId)) return;
        try
        {
            const string settingsPayload = """
            {
              "settings": {
                "waitForIdleTimeout": 0,
                "animationCoolOffTimeout": 0,
                "defaultActiveApplication": "com.apple.springboard",
                "snapshotMaxDepth": 1,
                "snapshotMaxChildren": 1,
                "shouldUseCompactResponses": true,
                "reduceMotion": true,
                "respectSystemAlerts": false,
                "screenshotOrientation": "portrait",
                "maxTypingFrequency": 180,
                "mjpegServerFramerate": 60,
                "mjpegScalingFactor": 100,
                "mjpegFixOrientation": false,
                "mjpegServerScreenshotQuality": 20,
                "screenshotQuality": 2
              }
            }
            """;
            using var content = new StringContent(settingsPayload, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/appium/settings", content, ct);
        }
        catch { }
    }

    /// <summary>The foreground app's interface orientation as WDA reports it ("PORTRAIT", "LANDSCAPE", ...), or null.</summary>
    public async Task<string?> GetOrientationAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected || string.IsNullOrEmpty(_sessionId)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var res = await _http.GetAsync($"{_wdaBaseUrl}/session/{_sessionId}/orientation", cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Rotates the phone (the foreground app follows if it supports that orientation).</summary>
    public async Task<bool> SetOrientationAsync(string orientation, CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (string.IsNullOrEmpty(_sessionId)) return false;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { orientation }), Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/orientation", content, ct);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    async Task RefreshWindowSizeAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionId)) return;
        try
        {
            using var res = await _http.GetAsync($"{_wdaBaseUrl}/session/{_sessionId}/window/size", ct);
            if (!res.IsSuccessStatusCode) return;
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var val))
            {
                if (val.TryGetProperty("width", out var w) && w.TryGetInt32(out int wi) && wi > 0)
                    _logicalWidth = wi;
                if (val.TryGetProperty("height", out var h) && h.TryGetInt32(out int hi) && hi > 0)
                    _logicalHeight = hi;
            }
        }
        catch { }
        try
        {
            // The screen's pixel size lets the bridge cut the black padding the phone adds
            // around its video frames (1242x2688 arrives as 1248x2704).
            using var res = await _http.GetAsync($"{_wdaBaseUrl}/wda/screen", ct);
            if (!res.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var val = doc.RootElement.GetProperty("value");
            double scale = val.GetProperty("scale").GetDouble();
            var size = val.GetProperty("screenSize");
            int a = (int)Math.Round(size.GetProperty("width").GetDouble() * scale);
            int b = (int)Math.Round(size.GetProperty("height").GetDouble() * scale);
            if (a > 0 && b > 0) { _screenPixels = $"{Math.Min(a, b)} {Math.Max(a, b)}"; SendScreenPixels(); }
        }
        catch { }
    }

    string? _screenPixels;
    void SendScreenPixels() { if (_screenPixels is not null) TryWriteHidCommand("SCREEN " + _screenPixels); }

    /// <summary>
    /// Translates client panel coordinates (px, py) inside panelSize to iPhone logical point coordinates and taps.
    /// </summary>
    public async Task<bool> SendTapAsync(int panelX, int panelY, Size panelSize, CancellationToken ct = default)
    {
        _selectAllPending = false;
        var (hx, hy) = MapPanelToHidNormalized(panelX, panelY, panelSize);
        if (TryWriteHidCommand($"TAP {hx} {hy}"))
            return true;

        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        var (ix, iy) = MapPanelToIphoneLogical(panelX, panelY, panelSize);
        return await SendRawTapAsync(ix, iy, ct);
    }

    /// <summary>
    /// Translates client panel coordinates (px, py) inside panelSize to iPhone logical point coordinates and double-taps
    /// (e.g. for on-screen keyboard CapsLock on Shift or word selection).
    /// </summary>
    public async Task<bool> SendDoubleTapAsync(int panelX, int panelY, Size panelSize, CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        var (ix, iy) = MapPanelToIphoneLogical(panelX, panelY, panelSize);
        return await SendRawDoubleTapAsync(ix, iy, ct);
    }

    public async Task<bool> SendRawDoubleTapAsync(int x, int y, CancellationToken ct = default)
    {
        _selectAllPending = false;
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;

        bool needReconnect = false;
        try
        {
            string url = !string.IsNullOrEmpty(_sessionId)
                ? $"{_wdaBaseUrl}/session/{_sessionId}/wda/doubleTap"
                : $"{_wdaBaseUrl}/wda/doubleTap";

            string payload = $$"""{"x": {{x}}, "y": {{y}}}""";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, content, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            _wdaConnected = false;
            needReconnect = true;
            return false;
        }
        finally
        {
            _inputLock.Release();
            if (needReconnect) _ = ConnectWdaAsync(ct);
        }
    }

    public async Task<bool> SendRawTapAsync(int x, int y, CancellationToken ct = default)
    {
        _selectAllPending = false;
        var (hx, hy) = MapLogicalToHidNormalized(x, y);
        if (TryWriteHidCommand($"TAP {hx} {hy}"))
            return true;

        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;

        bool needReconnect = false;
        try
        {
            string actionsJson = $$"""
            {
              "actions": [
                {
                  "type": "pointer",
                  "id": "finger1",
                  "parameters": { "pointerType": "touch" },
                  "actions": [
                    { "type": "pointerMove", "duration": 0, "origin": "viewport", "x": {{x}}, "y": {{y}} },
                    { "type": "pointerDown", "button": 0 },
                    { "type": "pointerUp", "button": 0 }
                  ]
                }
              ]
            }
            """;

            string url = !string.IsNullOrEmpty(_sessionId)
                ? $"{_wdaBaseUrl}/session/{_sessionId}/actions"
                : $"{_wdaBaseUrl}/wda/tap";

            using var content = new StringContent(actionsJson, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
            {
                _sessionId = null;
                if (!await TryConnectEndpointAsync(_wdaBaseUrl, ct)) return false;
                string retryUrl = $"{_wdaBaseUrl}/session/{_sessionId}/actions";
                using var retryContent = new StringContent(actionsJson, Encoding.UTF8, "application/json");
                using var retryRes = await _http.PostAsync(retryUrl, retryContent, ct);
                return retryRes.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            _wdaConnected = false;
            needReconnect = true;
            return false;
        }
        finally
        {
            _inputLock.Release();
            if (needReconnect) _ = ConnectWdaAsync(ct);
        }
    }

    /// <summary>
    /// Sends a swipe/drag gesture from (startX, startY) to (endX, endY) in panel coordinates.
    /// Horizontal swipes keep release velocity so paged views (Home screen) flip cleanly without springing back;
    /// vertical drags use micro-braking so scrolling stops without over-flicking.
    /// </summary>
    public async Task<bool> SendSwipeAsync(
        int startX,
        int startY,
        int endX,
        int endY,
        Size panelSize,
        double durationSec = 0.24,
        CancellationToken ct = default)
    {
        _selectAllPending = false;
        var (hx1, hy1) = MapPanelToHidNormalized(startX, startY, panelSize);
        var (hx2, hy2) = MapPanelToHidNormalized(endX, endY, panelSize);
        bool isHorizontal = Math.Abs(endX - startX) > Math.Abs(endY - startY) * 1.15;
        if (TryWriteHidCommand($"SWIPE {hx1} {hy1} {hx2} {hy2} {(isHorizontal ? 0 : 1)}"))
            return true;

        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        var (fx, fy) = MapPanelToIphoneLogical(startX, startY, panelSize);
        var (tx, ty) = MapPanelToIphoneLogical(endX, endY, panelSize);

        int dy = ty - fy;
        if (isHorizontal)
        {
            return await SendRawDragAsync(fx, fy, tx, ty, holdDurationSec: 0.04, moveDurationSec: 0.16, endPauseMs: 0, ct);
        }

        int scaledTy = fy + (int)Math.Round(dy * 0.65);
        scaledTy = Math.Clamp(scaledTy, 24, _logicalHeight - 24);
        double vMoveSec = Math.Clamp(durationSec, 0.24, 0.36);
        return await SendRawDragAsync(fx, fy, tx, scaledTy, holdDurationSec: 0.04, moveDurationSec: vMoveSec, endPauseMs: 40, ct);
    }

    /// <summary>
    /// Performs the native iOS left-edge swipe-back gesture.
    /// </summary>
    public async Task<bool> NavigateBackAsync(CancellationToken ct = default)
    {
        _selectAllPending = false;
        if (TryWriteHidCommand("SWIPE 120 32768 52000 32768 0"))
            return true;

        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        int midY = _logicalHeight / 2;
        int endX = (int)(_logicalWidth * 0.82);
        return await SendVelocityDragAsync(2, midY, endX, midY, pressSec: 0.02, holdSec: 0.02, velocity: 1200, ct);
    }

    /// <summary>
    /// Performs the native iOS bottom-edge swipe-up-and-hold gesture to open the App Switcher.
    /// </summary>
    public async Task<bool> OpenAppSwitcherAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        int midX = _logicalWidth / 2;
        int startY = _logicalHeight - 1;
        int endY = (int)(_logicalHeight * 0.56);
        return await SendVelocityDragAsync(midX, startY, midX, endY, pressSec: 0.05, holdSec: 0.50, velocity: 800, ct);
    }

    async Task<bool> SendVelocityDragAsync(int fx, int fy, int tx, int ty, double pressSec, double holdSec, int velocity, CancellationToken ct)
    {
        _selectAllPending = false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;

        bool needReconnect = false;
        try
        {
            if (string.IsNullOrEmpty(_sessionId))
                await TryConnectEndpointAsync(_wdaBaseUrl, ct);

            var payload = JsonSerializer.Serialize(new
            {
                fromX = fx,
                fromY = fy,
                toX = tx,
                toY = ty,
                pressDuration = pressSec,
                holdDuration = holdSec,
                velocity
            });

            string url = $"{_wdaBaseUrl}/session/{_sessionId}/wda/pressAndDragWithVelocity";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
            {
                _sessionId = null;
                if (!await TryConnectEndpointAsync(_wdaBaseUrl, ct)) return false;
                using var retryContent = new StringContent(payload, Encoding.UTF8, "application/json");
                using var retryRes = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/pressAndDragWithVelocity", retryContent, ct);
                return retryRes.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            _wdaConnected = false;
            needReconnect = true;
            return false;
        }
        finally
        {
            _inputLock.Release();
            if (needReconnect) _ = ConnectWdaAsync(ct);
        }
    }

    public Task<bool> SendRawSwipeAsync(int fx, int fy, int tx, int ty, double durationSec = 0.24, CancellationToken ct = default)
    {
        _selectAllPending = false;
        var (hx1, hy1) = MapLogicalToHidNormalized(fx, fy);
        var (hx2, hy2) = MapLogicalToHidNormalized(tx, ty);
        bool isHorizontal = Math.Abs(tx - fx) > Math.Abs(ty - fy) * 1.15;
        if (TryWriteHidCommand($"SWIPE {hx1} {hy1} {hx2} {hy2} {(isHorizontal ? 0 : 1)}"))
            return Task.FromResult(true);

        if (isHorizontal)
            return SendRawDragAsync(fx, fy, tx, ty, 0.04, 0.16, endPauseMs: 0, ct);

        int scaledTy = Math.Clamp(fy + (int)Math.Round((ty - fy) * 0.65), 24, _logicalHeight - 24);
        return SendRawDragAsync(fx, fy, tx, scaledTy, 0.04, Math.Clamp(durationSec, 0.24, 0.36), endPauseMs: 40, ct);
    }

    public async Task<bool> LaunchDeepLinkAsync(string urlScheme, CancellationToken ct = default)
    {
        PendingDeepLinkUrl = urlScheme;
        return await LaunchAppByUrlAsync(urlScheme, ct);
    }

    async Task<bool> SendRawDragAsync(
        int fx,
        int fy,
        int tx,
        int ty,
        double holdDurationSec,
        double moveDurationSec,
        int endPauseMs,
        CancellationToken ct)
    {
        _selectAllPending = false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;

        bool needReconnect = false;
        try
        {
            int holdMs = Math.Max(35, (int)Math.Round(holdDurationSec * 1000));
            int moveMs = Math.Clamp((int)Math.Round((moveDurationSec > 0 ? moveDurationSec : 0.26) * 1000), 140, 500);
            string brakeAction = endPauseMs > 0
                ? $$"""{ "type": "pause", "duration": {{endPauseMs}} },"""
                : "";

            string actionsJson = $$"""
            {
              "actions": [
                {
                  "type": "pointer",
                  "id": "finger1",
                  "parameters": { "pointerType": "touch" },
                  "actions": [
                    { "type": "pointerMove", "duration": 0, "origin": "viewport", "x": {{fx}}, "y": {{fy}} },
                    { "type": "pointerDown", "button": 0 },
                    { "type": "pause", "duration": {{holdMs}} },
                    { "type": "pointerMove", "duration": {{moveMs}}, "origin": "viewport", "x": {{tx}}, "y": {{ty}} },
                    {{brakeAction}}
                    { "type": "pointerUp", "button": 0 }
                  ]
                }
              ]
            }
            """;

            string url = $"{_wdaBaseUrl}/session/{_sessionId}/actions";
            using var content = new StringContent(actionsJson, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
            {
                _sessionId = null;
                if (!await TryConnectEndpointAsync(_wdaBaseUrl, ct)) return false;
                string retryUrl = $"{_wdaBaseUrl}/session/{_sessionId}/actions";
                using var retryContent = new StringContent(actionsJson, Encoding.UTF8, "application/json");
                using var retryRes = await _http.PostAsync(retryUrl, retryContent, ct);
                return retryRes.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            _wdaConnected = false;
            needReconnect = true;
            return false;
        }
        finally
        {
            _inputLock.Release();
            if (needReconnect) _ = ConnectWdaAsync(ct);
        }
    }

    /// <summary>
    /// Returns to the iPhone Home screen via hardware HID (or WDA fallback).
    /// </summary>
    public async Task<bool> SendHomeAsync(CancellationToken ct = default)
    {
        _selectAllPending = false;
        if (TryWriteHidCommand("BTN home"))
            return true;

        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;

        bool needReconnect = false;
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync($"{_wdaBaseUrl}/wda/homescreen", content, ct);
            if (res.IsSuccessStatusCode)
                return true;

            return await PressButtonInternalAsync("home", ct);
        }
        catch
        {
            _wdaConnected = false;
            needReconnect = true;
            return false;
        }
        finally
        {
            _inputLock.Release();
            if (needReconnect) _ = ConnectWdaAsync(ct);
        }
    }

    public async Task<(bool Ok, string Status)> BoostIphoneRamAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct))
            return (false, "iPhone Control Offline");

        bool homeOk = await SendHomeAsync(ct);
        return homeOk
            ? (true, "Returned to Home Screen")
            : (false, "Control Action Failed");
    }

    /// <summary>
    /// Presses physical power through the acknowledged HID bridge. Waking the
    /// display does not bypass the iPhone's passcode or authentication.
    /// </summary>
    public async Task<bool> TogglePowerLockAsync(CancellationToken ct = default)
    {
        _selectAllPending = false;
        if (!_hidBridgeReady || !_hidButtonAckReady)
            await ConnectWdaAsync(ct);
        if (!_hidBridgeReady || !_hidButtonAckReady) return false;
        return await SendAcknowledgedHidButtonAsync("lock", ct);
    }

    /// <summary>
    /// Presses physical iPhone button: "home", "volumeUp", "volumeDown".
    /// </summary>
    public async Task<bool> PressButtonAsync(string buttonName, CancellationToken ct = default)
    {
        if (buttonName.Equals("power", StringComparison.OrdinalIgnoreCase) ||
            buttonName.Equals("lock", StringComparison.OrdinalIgnoreCase))
            return await TogglePowerLockAsync(ct);
        string hidBtn = buttonName.ToLowerInvariant() switch
        {
            "volumeup" => "volup",
            "volumedown" => "voldown",
            "home" => "home",
            _ => ""
        };
        if (hidBtn.Length == 0) return false;
        if (_hidBridgeReady && _hidButtonAckReady)
            return await SendAcknowledgedHidButtonAsync(hidBtn, ct);

        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;

        try
        {
            return await PressButtonInternalAsync(buttonName, ct);
        }
        finally
        {
            _inputLock.Release();
        }
    }

    async Task<bool> PressButtonInternalAsync(string buttonName, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(_sessionId))
                await TryConnectEndpointAsync(_wdaBaseUrl, ct);

            var body = JsonSerializer.Serialize(new { name = buttonName });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/pressButton", content, ct);
            if (!res.IsSuccessStatusCode)
            {
                _sessionId = null;
                if (!await TryConnectEndpointAsync(_wdaBaseUrl, ct)) return false;
                using var retryContent = new StringContent(body, Encoding.UTF8, "application/json");
                using var retryRes = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/pressButton", retryContent, ct);
                return retryRes.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queues and batches text into the currently focused iOS keyboard input via WDA (/wda/keys)
    /// so rapid typing, Shift/CapsLock uppercase characters, Backspace, and Enter never collide or drop.
    /// </summary>
    public async Task<bool> SendKeysAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        bool shouldStartLoop = false;
        lock (_keyQueueLock)
        {
            _pendingKeys.Append(text);
            if (!_keyFlushRunning)
            {
                _keyFlushRunning = true;
                shouldStartLoop = true;
            }
        }

        if (shouldStartLoop)
            return await FlushPendingKeysLoopAsync(ct);

        return true;
    }

    async Task<bool> PostKeysAsync(string body, CancellationToken ct)
    {
        string url = !string.IsNullOrEmpty(_sessionId)
            ? $"{_wdaBaseUrl}/session/{_sessionId}/wda/keys"
            : $"{_wdaBaseUrl}/wda/keys";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(url, content, ct);
        return res.IsSuccessStatusCode;
    }

    async Task<bool> FlushPendingKeysLoopAsync(CancellationToken ct)
    {
        bool lastOk = true;
        while (true)
        {
            string batch;
            lock (_keyQueueLock)
            {
                if (_pendingKeys.Length == 0)
                {
                    _keyFlushRunning = false;
                    break;
                }
                batch = _pendingKeys.ToString();
                _pendingKeys.Clear();
            }

            if (!await _inputLock.WaitAsync(2500, ct))
            {
                lastOk = false;
                continue;
            }

            try
            {
                if (_selectAllPending)
                {
                    _selectAllPending = false;
                    await ClearFocusedElementInternalAsync(ct);
                    if (batch == "\b")
                    {
                        lastOk = true;
                        continue;
                    }
                }

                var chars = batch.Select(c => c.ToString()).ToArray();
                var body = JsonSerializer.Serialize(new { value = chars });
                lastOk = await PostKeysAsync(body, ct);
                // WDA can restart on the phone and drop its session while the
                // video stream keeps running, so the stream-based health check
                // never notices. Re-establish the session and retry once.
                if (!lastOk && await EnsureSessionHealthyAsync(ct))
                    lastOk = await PostKeysAsync(body, ct);
            }
            catch
            {
                lastOk = false;
            }
            finally
            {
                _inputLock.Release();
            }
        }
        return lastOk;
    }

    public async Task<bool> SendBackspaceAsync(CancellationToken ct = default)
    {
        if (_selectAllPending)
        {
            _selectAllPending = false;
            return await ClearFocusedElementAsync(ct);
        }
        return await SendKeysAsync("\b", ct);
    }

    async Task<string?> GetActiveElementUuidAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionId)) return null;
        try
        {
            using var res = await _http.GetAsync($"{_wdaBaseUrl}/session/{_sessionId}/element/active", ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Object)
            {
                if (val.TryGetProperty("ELEMENT", out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
                if (val.TryGetProperty("element-6066-11e4-a52e-4f735466cecf", out var w3cEl) && w3cEl.ValueKind == JsonValueKind.String)
                    return w3cEl.GetString();
            }
        }
        catch { }
        return null;
    }

    async Task<bool> ClearFocusedElementInternalAsync(CancellationToken ct)
    {
        var uuid = await GetActiveElementUuidAsync(ct);
        if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(_sessionId)) return false;
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/element/{uuid}/clear", content, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ClearFocusedElementAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;
        try
        {
            return await ClearFocusedElementInternalAsync(ct);
        }
        finally
        {
            _inputLock.Release();
        }
    }

    /// <summary>
    /// Selects all text in the currently focused iOS input field (Ctrl+A).
    /// Marks _selectAllPending so a subsequent Backspace, Delete, Paste, or key press replaces the field,
    /// and triple-taps the active element to visually highlight the selection on iOS.
    /// </summary>
    public async Task<bool> SelectAllAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (await SendKeyboardShortcutAsync("a", ct))
        {
            _selectAllPending = false;
            return true;
        }
        _selectAllPending = true;

        if (!await _inputLock.WaitAsync(2500, ct)) return true;
        try
        {
            var uuid = await GetActiveElementUuidAsync(ct);
            if (!string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(_sessionId))
            {
                const string payload = """{"numberOfTaps": 3, "numberOfTouches": 1}""";
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var res = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/element/{uuid}/tapWithNumberOfTaps", content, ct);
                return res.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            return true;
        }
        finally
        {
            _inputLock.Release();
        }
    }

    /// <summary>
    /// Copies text from the currently focused iOS input field (or iOS pasteboard) and syncs it to both iOS and Windows clipboards (Ctrl+C).
    /// </summary>
    public async Task<string?> CopyToClipboardAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return null;
        if (_clipboardReady && await SendKeyboardShortcutAsync("c", ct))
        {
            await Task.Delay(100, ct);
            return await ReadPhoneClipboardAsync(ct);
        }
        if (!await _inputLock.WaitAsync(2500, ct)) return null;

        try
        {
            string? text = null;
            var uuid = await GetActiveElementUuidAsync(ct);
            if (!string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(_sessionId))
            {
                using var res = await _http.GetAsync($"{_wdaBaseUrl}/session/{_sessionId}/element/{uuid}/text", ct);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
                        text = val.GetString();
                }
            }

            if (string.IsNullOrEmpty(text))
                text = await GetPasteboardInternalAsync(ct);

            if (!string.IsNullOrEmpty(text))
                await SetPasteboardInternalAsync(text, ct);

            return text;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inputLock.Release();
        }
    }

    /// <summary>
    /// Cuts text from the currently focused iOS input field and clears it (Ctrl+X).
    /// </summary>
    public async Task<string?> CutToClipboardAsync(CancellationToken ct = default)
    {
        var text = await CopyToClipboardAsync(ct);
        _selectAllPending = false;
        await ClearFocusedElementAsync(ct);
        return text;
    }

    /// <summary>
    /// Pastes text into the iOS pasteboard and types it into the currently focused iOS input field (Ctrl+V).
    /// </summary>
    public async Task<bool> PasteFromClipboardAsync(string? text, CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        if (string.IsNullOrEmpty(text))
            text = await GetPasteboardInternalAsync(ct);

        if (string.IsNullOrEmpty(text)) return false;

        await SetPasteboardInternalAsync(text, ct);
        return await SendKeysAsync(text, ct);
    }

    async Task<bool> SetPasteboardInternalAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionId)) return false;
        try
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            var body = JsonSerializer.Serialize(new { content = b64, contentType = "plaintext" });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/setPasteboard", content, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    async Task<string?> GetPasteboardInternalAsync(CancellationToken ct)
    {
        if (_clipboardReady)
            return await ReadPhoneClipboardAsync(ct);
        if (string.IsNullOrEmpty(_sessionId)) return null;
        try
        {
            var body = JsonSerializer.Serialize(new { contentType = "plaintext" });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/getPasteboard", content, ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
            {
                var b64 = val.GetString();
                if (!string.IsNullOrEmpty(b64))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
        }
        catch { }
        return null;
    }

    async Task<string?> ReadPhoneClipboardAsync(CancellationToken ct)
    {
        if (!_clipboardReady) return null;
        await _clipboardReadLock.WaitAsync(ct);
        try
        {
            _clipboardRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryWriteHidCommand("CLIP_GET")) return null;
            return await _clipboardRead.Task.WaitAsync(TimeSpan.FromSeconds(4), ct) ? _phoneClipboard : null;
        }
        catch (TimeoutException) { return null; }
        finally { _clipboardRead = null; _clipboardReadLock.Release(); }
    }

    public async Task<bool> SendKeyboardShortcutAsync(string key, CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;
        if (!await _inputLock.WaitAsync(2500, ct)) return false;
        try
        {
            var body = JsonSerializer.Serialize(new { keys = new[] { new { key, modifierFlags = 16 } } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{_wdaBaseUrl}/session/{_sessionId}/wda/element/0/keyboardInput", content, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
        finally { _inputLock.Release(); }
    }

    public Task<bool> SendPressAndHoldAsync(int x, int y, Size panelSize, double durationSec = .8, CancellationToken ct = default)
    {
        var (ix, iy) = MapPanelToIphoneLogical(x, y, panelSize);
        return SendRawDragAsync(ix, iy, ix, iy, Math.Clamp(durationSec, .5, 10), .14, 0, ct);
    }

    /// <summary>
    /// Captures a live PNG screenshot directly from the iPhone via WebDriverAgent (/screenshot).
    /// </summary>
    public async Task<byte[]?> CaptureScreenshotPngAsync(CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return null;

        try
        {
            using var res = await _http.GetAsync($"{_wdaBaseUrl}/screenshot", ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
            {
                var b64 = val.GetString();
                if (!string.IsNullOrEmpty(b64))
                    return Convert.FromBase64String(b64);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Launches an iOS app or URL scheme on the iPhone via WDA (/url).
    /// </summary>
    public async Task<bool> LaunchAppByUrlAsync(string urlScheme, CancellationToken ct = default)
    {
        if (!_wdaConnected && !await ConnectWdaAsync(ct)) return false;

        try
        {
            string url = !string.IsNullOrEmpty(_sessionId)
                ? $"{_wdaBaseUrl}/session/{_sessionId}/url"
                : $"{_wdaBaseUrl}/url";

            var body = JsonSerializer.Serialize(new { url = urlScheme });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, content, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    (int X, int Y) MapPanelToIphoneLogical(int px, int py, Size panelSize)
    {
        int w = Math.Max(1, panelSize.Width);
        int h = Math.Max(1, panelSize.Height);
        int ix = Math.Clamp((int)Math.Round((double)px / w * _logicalWidth), 0, _logicalWidth);
        int iy = Math.Clamp((int)Math.Round((double)py / h * _logicalHeight), 0, _logicalHeight);
        return (ix, iy);
    }

    static nint s_childJobHandle = CreateKillOnCloseJob();

    static nint CreateKillOnCloseJob()
    {
        try
        {
            nint hJob = CreateJobObjectW(0, null);
            if (hJob == 0) return 0;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x00002000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            SetInformationJobObject(hJob, 9, ref info, (uint)len);
            return hJob;
        }
        catch
        {
            return 0;
        }
    }

    public static void AssignToChildJob(Process? proc)
    {
        if (proc is null || s_childJobHandle == 0) return;
        try
        {
            AssignProcessToJobObject(s_childJobHandle, proc.Handle);
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(
        nint hJob,
        int JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    public void StopBridge()
    {
        _bridgeWanted = false;
        if (TryWriteHidCommand("QUIT")) WaitForVideoStop();
        _hidBridgeReady = false;
        _hidButtonAckReady = false;
        _hidTouchReady = _hidContactReady = _clipboardReady = false;
        _clipboardRead?.TrySetResult(false);
        FailPendingHidButtons();
        lock (_bridgeProcessLock)
        {
            _wdaConnected = false;
            _sessionId = null;
            _configuredSessionId = null;
            _wdaRunnerReady.Reset();
            lock (_hidWriteLock) { _wdaBridgeStdin = null; }
            try { if (_wdaBridgeProcess is { HasExited: false } && !_wdaBridgeProcess.WaitForExit(500)) _wdaBridgeProcess.Kill(true); } catch { }
            try { if (_wdaForwardProcess is { HasExited: false }) _wdaForwardProcess.Kill(true); } catch { }
            try { if (_wdaMjpegForwardProcess is { HasExited: false }) _wdaMjpegForwardProcess.Kill(true); } catch { }
            _wdaBridgeProcess = null;
            _wdaForwardProcess = null;
            _wdaMjpegForwardProcess = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _bridgeWanted = false;
        _wdaConnected = false;

        if (TryWriteHidCommand("QUIT")) WaitForVideoStop();
        lock (_hidWriteLock) { _wdaBridgeStdin = null; }

        try
        {
            if (!string.IsNullOrEmpty(_sessionId) && !string.IsNullOrEmpty(_wdaBaseUrl))
            {
                using var cts = new CancellationTokenSource(350);
                _http.DeleteAsync($"{_wdaBaseUrl}/session/{_sessionId}", cts.Token).GetAwaiter().GetResult();
            }
        }
        catch { }

        try { if (_wdaBridgeProcess is { HasExited: false } && !_wdaBridgeProcess.WaitForExit(500)) _wdaBridgeProcess.Kill(true); } catch { }
        try { if (_wdaForwardProcess is { HasExited: false }) _wdaForwardProcess.Kill(true); } catch { }
        try { if (_wdaMjpegForwardProcess is { HasExited: false }) _wdaMjpegForwardProcess.Kill(true); } catch { }
        nint job = Interlocked.Exchange(ref s_childJobHandle, 0);
        if (job != 0)
        {
            try { CloseHandle(job); } catch { }
        }

        _http.Dispose();
    }
}
