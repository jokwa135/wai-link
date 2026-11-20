using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneBridge;

/// <summary>
/// High-speed iPhone screen viewport inside the continuous glass shell.
/// - Decouples the TCP socket reader thread from the off-thread GDI+ decoder so socket reads never stall
///   and intermediate frames are dropped with zero queue backlog.
/// - Reuses double-buffered Format32bppPArgb bitmaps to avoid GC pauses and keep UI thread painting <0.3ms.
/// </summary>
public sealed class IphoneChassisView : Panel
{
    bool _wdaConnected;
    string _mjpegHost = "127.0.0.1";
    string _fallbackLanHost = "127.0.0.1"; // last Wi-Fi host the controller reported
    string _deviceName = "iPhone";
    string _statusText = "Connect iPhone via Wi-Fi or USB to view and control";
    Rectangle _connectBtnBounds;
    bool _hoverConnect;
    readonly UiAnimationClock _spinnerTimer;
    float _spinnerAngle;

    readonly object _frameLock = new();
    Bitmap? _currentFrame;
    Bitmap? _spareFrame;
    byte[]? _latestRawJpeg;
    readonly SemaphoreSlim _decodeSignal = new(0, 1);
    CancellationTokenSource? _mjpegCts;
    bool _streamingMjpeg;
    int _aspectChangePending;
    long _frameSequence, _paintedSequence;
    int _paintQueued;
    long _lastFrameTick;
    long _firstFrameTick;
    double? _frameAspectRatio;
    public double? FrameAspectRatio => _frameAspectRatio;
    public event Action? FrameAspectChanged;
    public long RenderedFrames { get; private set; }
    public long ReceivedFrames { get; private set; }
    // Clockwise rotation (0, 90 or 270) for a landscape shell. It turns portrait-shaped frames only:
    // the WDA stream always arrives portrait, while the hardware stream (iOS 27) already arrives
    // landscape once the phone's interface has turned. A frame that does not match the shell is
    // shown fitted (letterboxed) rather than turned, so only the rotate button changes orientation.
    volatile int _rotation;
    public int Rotation
    {
        get => _rotation;
        set { if (_rotation == value) return; _rotation = value; Invalidate(); }
    }
    int TurnFor(double frameAspect) => _rotation is 90 or 270 && frameAspect < 1 ? _rotation : 0;
    int EffectiveRotation => TurnFor(_frameAspectRatio ?? 414.0 / 896);

    public Rectangle ScreenBounds
    {
        get
        {
            double aspect = _frameAspectRatio ?? 414.0 / 896;
            if (EffectiveRotation != 0) aspect = 1 / aspect;
            int width = Math.Min(Width, (int)Math.Round(Height * aspect));
            int height = Math.Min(Height, (int)Math.Round(width / aspect));
            return new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
        }
    }

    public bool TryMapScreenPoint(Point point, out Point mapped, out Size size, bool clamp = false)
    {
        var bounds = ScreenBounds;
        bool inside = bounds.Width > 0 && bounds.Height > 0 && (clamp || bounds.Contains(point));
        int rotation = EffectiveRotation;
        if (rotation is not (90 or 270))
        {
            size = bounds.Size;
            mapped = new Point(Math.Clamp(point.X - bounds.X, 0, Math.Max(0, bounds.Width - 1)),
                Math.Clamp(point.Y - bounds.Y, 0, Math.Max(0, bounds.Height - 1)));
            return inside;
        }
        // Map the displayed (landscape) point back to the phone's portrait coordinates.
        double u = Math.Clamp((point.X - bounds.X) / (double)Math.Max(1, bounds.Width), 0, 1);
        double v = Math.Clamp((point.Y - bounds.Y) / (double)Math.Max(1, bounds.Height), 0, 1);
        (double ru, double rv) = rotation == 90 ? (v, 1 - u) : (1 - v, u);
        size = new Size(bounds.Height, bounds.Width);
        mapped = new Point(Math.Clamp((int)(ru * size.Width), 0, Math.Max(0, size.Width - 1)),
            Math.Clamp((int)(rv * size.Height), 0, Math.Max(0, size.Height - 1)));
        return inside;
    }

    public bool IsStreamingActively =>
        _streamingMjpeg && (Environment.TickCount64 - Interlocked.Read(ref _lastFrameTick) < 2500);

    public string MjpegHost
    {
        get => _mjpegHost;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                string trimmed = value.Trim();
                if (trimmed != "127.0.0.1") _fallbackLanHost = trimmed;
                if (!string.Equals(_mjpegHost, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    _mjpegHost = trimmed;
                    if (_streamingMjpeg)
                    {
                        _streamingMjpeg = false;
                        try { _mjpegCts?.Cancel(); } catch { }
                        StartMjpegStream();
                    }
                }
            }
        }
    }

    public bool WdaConnected
    {
        get => _wdaConnected;
        set
        {
            if (_wdaConnected == value) return;
            _wdaConnected = value;
            if (_wdaConnected)
                StartMjpegStream();
            else
                StopMjpegStream();
            Invalidate();
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        set { _deviceName = value; Invalidate(); }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            UpdateSpinnerState();
            Invalidate();
        }
    }

    void UpdateSpinnerState()
    {
        bool connecting = _wdaConnected || _statusText.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase);
        if (connecting && _currentFrame is null && Visible)
        {
            if (!_spinnerTimer.Enabled) _spinnerTimer.Start();
        }
        else if (_spinnerTimer.Enabled)
        {
            _spinnerTimer.Stop();
        }
    }

    public event Action? OnConnectWdaRequested;

    public byte[]? GetCurrentFramePng()
    {
        lock (_frameLock)
        {
            if (_currentFrame is null) return null;
            using var ms = new MemoryStream();
            _currentFrame.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    public IphoneChassisView()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(9, 10, 14);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);
        TabStop = true;
        _spinnerTimer = new UiAnimationClock(this);
        _spinnerTimer.Tick += (_, _) =>
        {
            if (_currentFrame is not null || !Visible)
            {
                _spinnerTimer.Stop();
                return;
            }
            _spinnerAngle = (float)(GlassPresentation.Seconds * 150 % 360);
            Invalidate();
        };
    }

    public void StartMjpegStream()
    {
        if (_streamingMjpeg) return;
        _streamingMjpeg = true;
        _mjpegCts?.Cancel();
        _mjpegCts = new CancellationTokenSource();
        var ct = _mjpegCts.Token;
        _ = Task.Run(() => MjpegReaderLoopAsync(ct), ct);
        _ = Task.Run(() => MjpegDecodeLoopAsync(ct), ct);
    }

    public void StopMjpegStream()
    {
        _streamingMjpeg = false;

        try { _mjpegCts?.Cancel(); } catch { }
        Interlocked.Exchange(ref _latestRawJpeg, null);
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
            _firstFrameTick = 0;
            _spareFrame?.Dispose();
            _spareFrame = null;
        }
    }

    sealed class StreamReadState
    {
        public readonly byte[] Buf = new byte[262144];
        public int Pos;
        public int Len;
    }

    // Hardware screen video (iOS 27+), decoded by the WDA bridge and served locally in the
    // same MJPEG format as WDA's port 9100. Preferred whenever it delivers frames.
    const int FastStreamPort = 9101;
    long _fastRetryAfterTick;
    int _fastFailures;
    long _fastPortCheckTick;
    bool _fastPortListening;

    // Look for the hardware stream again right away (after live video is resumed).
    public void ResetFastStreamRetry()
    {
        _fastRetryAfterTick = 0;
        _fastFailures = 0;
        _fastPortCheckTick = 0;
    }

    bool FastStreamAvailable()
    {
        long now = Environment.TickCount64;
        if (now < _fastRetryAfterTick) return false;
        if (now - _fastPortCheckTick > 2000)
        {
            _fastPortCheckTick = now;
            try
            {
                _fastPortListening = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners().Any(e => e.Port == FastStreamPort);
            }
            catch { _fastPortListening = false; }
        }
        return _fastPortListening;
    }

    void PublishJpeg(byte[] jpeg)
    {
        ReceivedFrames++;
        Interlocked.Exchange(ref _latestRawJpeg, jpeg);
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
        if (_decodeSignal.CurrentCount == 0)
        {
            try { _decodeSignal.Release(); } catch { }
        }
    }

    async Task ReadFastStreamAsync(StreamReadState state, CancellationToken ct)
    {
        int frames = 0;
        try
        {
            using var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 262144 };
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(500);
                await tcp.ConnectAsync("127.0.0.1", FastStreamPort, connectCts.Token);
            }
            using var stream = tcp.GetStream();
            byte[] req = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1:{FastStreamPort}\r\n\r\n");
            await stream.WriteAsync(req, ct);
            state.Pos = 0;
            state.Len = 0;
            while (!ct.IsCancellationRequested && !IsDisposed)
            {
                byte[]? jpeg = await ReadNextJpegFrameAsync(stream, state, ct);
                if (jpeg is null) break;
                frames++;
                PublishJpeg(jpeg);
            }
        }
        catch { }
        if (frames >= 10)
        {
            _fastFailures = 0;
        }
        else
        {
            // No (or barely any) video: use WDA for a while, backing off 5, 10, 20, 30 s.
            _fastFailures++;
            _fastRetryAfterTick = Environment.TickCount64 + Math.Min(30000, 5000L << Math.Min(3, _fastFailures - 1));
        }
    }

    async Task MjpegReaderLoopAsync(CancellationToken ct)
    {
        var state = new StreamReadState();
        bool useWifiHost = false;
        long lastReconnectRequestTick = 0;

        while (!ct.IsCancellationRequested && !IsDisposed)
        {
            if (FastStreamAvailable())
            {
                await ReadFastStreamAsync(state, ct);
                continue;
            }
            try
            {
                string wifiCandidate = !string.Equals(_mjpegHost, "127.0.0.1", StringComparison.Ordinal)
                    ? _mjpegHost
                    : _fallbackLanHost;

                string host = (useWifiHost || !string.Equals(_mjpegHost, "127.0.0.1", StringComparison.Ordinal))
                    ? wifiCandidate
                    : "127.0.0.1";

                using var tcp = new TcpClient
                {
                    NoDelay = true,
                    ReceiveBufferSize = 262144
                };

                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(1000);
                    try
                    {
                        await tcp.ConnectAsync(host, 9100, connectCts.Token);
                    }
                    catch
                    {
                        useWifiHost = !useWifiHost;
                        await Task.Delay(150, ct);
                        continue;
                    }
                }

                if (!tcp.Connected)
                {
                    useWifiHost = !useWifiHost;
                    await Task.Delay(150, ct);
                    continue;
                }

                using var stream = tcp.GetStream();

                byte[] req = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}:9100\r\nConnection: keep-alive\r\n\r\n");
                using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    writeCts.CancelAfter(1500);
                    await stream.WriteAsync(req, writeCts.Token);
                }

                state.Pos = 0;
                state.Len = 0;
                int framesRead = 0;

                while (!ct.IsCancellationRequested && !IsDisposed)
                {
                    byte[]? jpeg = await ReadNextJpegFrameAsync(stream, state, ct);
                    if (jpeg is null) break;


                    framesRead++;
                    PublishJpeg(jpeg);
                    if (FastStreamAvailable()) break;
                }

                if (framesRead == 0)
                {
                    useWifiHost = !useWifiHost;
                    long nowTick = Environment.TickCount64;
                    if (nowTick - lastReconnectRequestTick > 6000)
                    {
                        lastReconnectRequestTick = nowTick;
                        OnConnectWdaRequested?.Invoke();
                    }
                    await Task.Delay(150, ct);
                }
            }
            catch
            {
                useWifiHost = !useWifiHost;
                try { await Task.Delay(150, ct); } catch { break; }
            }
        }
        _streamingMjpeg = false;
    }

    async Task MjpegDecodeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !IsDisposed)
        {
            try
            {
                await _decodeSignal.WaitAsync(ct);

                byte[]? jpeg = Interlocked.Exchange(ref _latestRawJpeg, null);
                if (jpeg is null || jpeg.Length < 128) continue;

                using var ms = new MemoryStream(jpeg, writable: false);
                using var rawBmp = new Bitmap(ms);
                double aspect = (double)rawBmp.Width / rawBmp.Height;
                bool aspectChanged = _frameAspectRatio is null || Math.Abs(_frameAspectRatio.Value - aspect) > .001;
                _frameAspectRatio = aspect;
                if (aspectChanged) Interlocked.Exchange(ref _aspectChangePending, 1);
                var screen = ScreenBounds;
                int targetW = Math.Max(1, screen.Width), targetH = Math.Max(1, screen.Height);

                Bitmap? targetBmp;
                lock (_frameLock)
                {
                    targetBmp = _spareFrame;
                    _spareFrame = null;
                }

                // Landscape: scale to the portrait size first, then turn the small bitmap (fast pixel copy).
                int rotation = TurnFor(aspect);
                bool turn = rotation is 90 or 270;
                if (turn) (targetW, targetH) = (targetH, targetW);
                if (targetBmp is null || targetBmp.Width != targetW || targetBmp.Height != targetH)
                {
                    targetBmp?.Dispose();
                    targetBmp = new Bitmap(targetW, targetH, PixelFormat.Format32bppPArgb);
                }

                using (var g = Graphics.FromImage(targetBmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.SmoothingMode = SmoothingMode.None;
                    g.DrawImage(rawBmp, 0, 0, targetW, targetH);
                }
                if (turn)
                {
                    targetBmp.RotateFlip(rotation == 90 ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
                }

                lock (_frameLock)
                {

                    _spareFrame = _currentFrame;
                    if (_currentFrame is null) _firstFrameTick = Environment.TickCount64;
                    _currentFrame = targetBmp;
                    _frameSequence++;
                }

                if (!IsDisposed && IsHandleCreated && Interlocked.Exchange(ref _paintQueued, 1) == 0)
                {
                    BeginInvoke(() =>
                    {
                        Interlocked.Exchange(ref _paintQueued, 0);
                        if (!IsDisposed)
                        {
                            if (Interlocked.Exchange(ref _aspectChangePending, 0) != 0) FrameAspectChanged?.Invoke();
                            Invalidate();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore corrupt frame and continue
            }
        }
    }

    static async Task<int> ReadWithTimeoutAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(4000);
        return await stream.ReadAsync(buffer, readCts.Token);
    }

    static async Task<byte[]?> ReadNextJpegFrameAsync(NetworkStream stream, StreamReadState s, CancellationToken ct)
    {
        int contentLength = -1;
        byte[] buf = s.Buf;

        while (!ct.IsCancellationRequested)
        {
            int lineEnd = -1;
            for (int i = s.Pos; i < s.Len; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    lineEnd = i;
                    break;
                }
            }

            if (lineEnd < 0)
            {
                if (s.Pos > 0)
                {
                    int rem = s.Len - s.Pos;
                    if (rem > 0) Buffer.BlockCopy(buf, s.Pos, buf, 0, rem);
                    s.Pos = 0;
                    s.Len = rem;
                }
                if (s.Len >= buf.Length)
                {
                    s.Pos = 0;
                    s.Len = 0;
                }
                int n = await ReadWithTimeoutAsync(stream, buf.AsMemory(s.Len, buf.Length - s.Len), ct);
                if (n <= 0) return null;
                s.Len += n;
                continue;
            }

            int lineLen = lineEnd - s.Pos;
            if (lineLen > 0 && buf[lineEnd - 1] == (byte)'\r')
                lineLen--;

            if (lineLen == 0)
            {
                s.Pos = lineEnd + 1;
                if (contentLength > 0 && contentLength <= 4_000_000)
                    break;
                continue;
            }

            if (lineLen > 15 && (buf[s.Pos] == (byte)'C' || buf[s.Pos] == (byte)'c'))
            {
                string line = Encoding.ASCII.GetString(buf, s.Pos, lineLen);
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(line.AsSpan(15).Trim(), out int cl))
                        contentLength = cl;
                }
            }

            s.Pos = lineEnd + 1;
        }

        if (contentLength <= 0) return null;

        byte[] jpeg = new byte[contentLength];
        int copied = 0;
        int avail = s.Len - s.Pos;
        if (avail > 0)
        {
            int toTake = Math.Min(avail, contentLength);
            Buffer.BlockCopy(buf, s.Pos, jpeg, 0, toTake);
            s.Pos += toTake;
            copied += toTake;
        }

        while (copied < contentLength)
        {
            int n = await ReadWithTimeoutAsync(stream, jpeg.AsMemory(copied, contentLength - copied), ct);
            if (n <= 0) return null;
            copied += n;
        }

        return jpeg;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_wdaConnected)
        {
            if (Cursor != Cursors.Default) Cursor = Cursors.Default;
            return;
        }
        bool h = _connectBtnBounds.Contains(e.Location);
        if (h != _hoverConnect)
        {
            _hoverConnect = h;
            Cursor = h ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverConnect)
        {
            _hoverConnect = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (!_wdaConnected && e.Button == MouseButtons.Left && _connectBtnBounds.Contains(e.Location))
        {
            OnConnectWdaRequested?.Invoke();
            return;
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        lock (_frameLock)
        {
            if (_currentFrame is not null)
            {
                float reveal = GlassPresentation.MotionEnabled
                    ? GlassPresentation.Ease((Environment.TickCount64 - _firstFrameTick) / 1000.0, .22) : 1;
                g.CompositingMode = CompositingMode.SourceOver;
                g.Clear(Color.Black);
                var screen = ScreenBounds;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.SmoothingMode = SmoothingMode.None;
                if (reveal < 1)
                {
                    GlassPresentation.PaintBackdrop(g, screen, Point.Empty);
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = reveal });
                    g.DrawImage(_currentFrame, screen, 0, 0, _currentFrame.Width, _currentFrame.Height, GraphicsUnit.Pixel, attributes);
                }
                else g.DrawImage(_currentFrame, screen);
                if (_paintedSequence != _frameSequence)
                {
                    _paintedSequence = _frameSequence;
                    RenderedFrames++;
                }
                return;
            }
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width < 40 || rect.Height < 40) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        GlassPresentation.PaintBackdrop(g, ClientRectangle, Point.Empty);
        if (!_spinnerTimer.Enabled && Visible) _spinnerTimer.Start();
        GlassPresentation.Loader(g, ClientSize, true, GlassPresentation.Seconds, _statusText);
        // The phone glyph is also a retry target, without adding a second labelled control.
        _connectBtnBounds = new Rectangle(Width / 2 - 32, (int)(Height * .46f) - 32, 64, 64);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinnerTimer.Stop();
            _spinnerTimer.Dispose();
            StopMjpegStream();
            _decodeSignal.Dispose();
        }
        base.Dispose(disposing);
    }
}
