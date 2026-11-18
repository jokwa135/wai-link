using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PhoneBridge;

// WM_TIMER is low-priority and coalesces badly during native painting. Post at most
// one elapsed-time animation tick to the UI queue, never a backlog of stale frames.
sealed class UiAnimationClock : IDisposable
{
    readonly Control _owner;
    readonly System.Threading.Timer _timer;
    int _pending;
    long _lastFrame;
    volatile bool _enabled;
    public bool Enabled => _enabled;
    public event EventHandler? Tick;
    public UiAnimationClock(Control owner)
    {
        _owner = owner;
        _timer = new System.Threading.Timer(_ =>
        {
            long now = Stopwatch.GetTimestamp();
            if ((now - Interlocked.Read(ref _lastFrame)) / (double)Stopwatch.Frequency < .012) return;
            if (!_enabled || _owner.IsDisposed || !_owner.IsHandleCreated || Interlocked.Exchange(ref _pending, 1) != 0) return;
            Interlocked.Exchange(ref _lastFrame, now);
            try
            {
                _owner.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _pending, 0);
                    if (_enabled && !_owner.IsDisposed) Tick?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _pending, 0); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }
    // Request below the Windows 15.6ms timer quantum, then limit presentation
    // above to ~60Hz. Requesting 16ms otherwise rounds up to ~31ms on this host.
    public void Start() { _enabled = true; _timer.Change(0, 8); }
    public void Stop() { _enabled = false; _timer.Change(Timeout.Infinite, Timeout.Infinite); }
    public void Dispose() { _enabled = false; _timer.Dispose(); }
}

// Shared motion helpers and the in-screen (stage) visuals. All animation uses
// elapsed time, not timer ticks.
static class GlassPresentation
{
    public static readonly Color Ink = Color.FromArgb(30, 41, 59);
    // Stage content sits on a dark backdrop regardless of the shell color.
    public static readonly Color StageInk = Color.FromArgb(238, 242, 250);
    public static readonly Color StageMuted = Color.FromArgb(150, 160, 178);

    public static bool MotionEnabled => SystemInformation.UIEffectsEnabled && SystemInformation.IsMenuAnimationEnabled;
    public static double Seconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static float Ease(double elapsed, double duration = .34)
    {
        float t = (float)Math.Clamp(elapsed / duration, 0, 1);
        return 1 - MathF.Pow(1 - t, 3);
    }

    public static void Spring(ref float value, ref float velocity, float target, float dt)
    {
        const float frequency = 24, damping = .82f;
        float damped = frequency * MathF.Sqrt(1 - damping * damping);
        float displacement = value - target;
        float b = (velocity + damping * frequency * displacement) / damped;
        float sin = MathF.Sin(damped * dt), cos = MathF.Cos(damped * dt);
        float decay = MathF.Exp(-damping * frequency * dt);
        float wave = displacement * cos + b * sin;
        value = target + decay * wave;
        velocity = decay * (-damping * frequency * wave + damped * (-displacement * sin + b * cos));
    }

    // Deep neutral backdrop with soft glows in the logo's blue, teal and violet.
    public static void PaintBackdrop(Graphics g, Rectangle bounds, Point origin)
    {
        var state = g.Save();
        g.SetClip(bounds);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new LinearGradientBrush(new Point(bounds.X, bounds.Y - 1), new Point(bounds.X, bounds.Bottom + 1),
            Color.FromArgb(13, 15, 21), Color.FromArgb(7, 8, 12))) g.FillRectangle(fill, bounds);
        float w = bounds.Width, h = bounds.Height;
        Glow(g, new RectangleF(bounds.X - w * .55f, bounds.Y + h * .42f, w * 1.3f, w * 1.3f), Color.FromArgb(74, 52, 132, 255));
        Glow(g, new RectangleF(bounds.X + w * .30f, bounds.Y - w * .50f, w * 1.2f, w * 1.2f), Color.FromArgb(62, 168, 104, 240));
        Glow(g, new RectangleF(bounds.X + w * .10f, bounds.Y + h * .12f, w * .9f, w * .9f), Color.FromArgb(34, 60, 214, 190));
        g.Restore(state);
    }

    static void Glow(Graphics g, RectangleF rect, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(rect);
        using var fill = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(fill, path);
    }

    public static void Phone(Graphics g, PointF center, float height, bool iphone, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        float s = height / 38f;
        g.ScaleTransform(s, s);
        using var pen = new Pen(color, 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var body = Theme.RoundedRect(new Rectangle(-10, -19, 20, 38), 5);
        using var brush = new SolidBrush(color);
        g.DrawPath(pen, body);
        if (iphone)
        {
            using var island = Theme.RoundedRect(new Rectangle(-4, -16, 8, 3), 2);
            g.FillPath(brush, island);
            g.DrawLine(pen, -12, -10, -12, -7);
            g.DrawLine(pen, -12, -3, -12, 1);
            g.DrawLine(pen, 12, -6, 12, 1);
        }
        else
        {
            g.FillEllipse(brush, -1.3f, -15.5f, 2.6f, 2.6f);
            g.DrawLine(pen, 12, -10, 12, -3);
            g.DrawLine(pen, 12, 2, 12, 6);
        }
        g.DrawLine(pen, -3, 15, 3, 15);
        g.Restore(state);
    }

    // Loading state: the 3D logo turning, with an optional status line beneath it.
    public static void Loader(Graphics g, Size size, bool iphone, double time, string? status = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float width = Math.Clamp(size.Width * .56f, 96, 300), height = width * .75f;
        float cx = size.Width / 2f, cy = size.Height * .46f;
        LogoAnimation.DrawLoop(g, new RectangleF(cx - width / 2, cy - height / 2, width, height), time);
        if (string.IsNullOrWhiteSpace(status)) return;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = Theme.Font(9f);
        using var brush = new SolidBrush(StageMuted);
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(status, font, brush, new RectangleF(16, cy + height / 2 + 6, size.Width - 32, 40), format);
    }
}

// Pre-rendered Blender frames of the 3D logo (Assets/Logo3D): a one-shot
// intro and a seamless turning loop. Frames decode lazily and stay cached.
static class LogoAnimation
{
    const int IntroFrames = 40, LoopFrames = 48;
    const double Fps = 30;
    static readonly Bitmap?[] Intro = new Bitmap?[IntroFrames], Loop = new Bitmap?[LoopFrames];
    public static double IntroSeconds => IntroFrames / Fps;

    static Bitmap? Frame(Bitmap?[] cache, string kind, int index)
    {
        if (cache[index] is { } cached) return cached;
        using var stream = typeof(LogoAnimation).Assembly.GetManifestResourceStream($"PhoneBridge.Assets.Logo3D.{kind}_{index:000}.png");
        if (stream is null) return null;
        using var image = Image.FromStream(stream);
        var frame = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(frame)) g.DrawImage(image, 0, 0, image.Width, image.Height);
        return cache[index] = frame;
    }

    static void Paint(Graphics g, Bitmap? frame, RectangleF bounds, float opacity)
    {
        if (frame is null || opacity <= .004f) return;
        float scale = Math.Min(bounds.Width / frame.Width, bounds.Height / frame.Height);
        var target = new RectangleF(bounds.X + (bounds.Width - frame.Width * scale) / 2,
            bounds.Y + (bounds.Height - frame.Height * scale) / 2, frame.Width * scale, frame.Height * scale);
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (opacity >= .996f) g.DrawImage(frame, target);
        else
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            g.DrawImage(frame, [new PointF(target.Left, target.Top), new PointF(target.Right, target.Top), new PointF(target.Left, target.Bottom)],
                new RectangleF(0, 0, frame.Width, frame.Height), GraphicsUnit.Pixel, attributes);
        }
        g.InterpolationMode = mode;
    }

    // Continuous turning loop; with motion disabled it rests on the front view.
    public static void DrawLoop(Graphics g, RectangleF bounds, double time)
    {
        int index = GlassPresentation.MotionEnabled ? (int)(time * Fps % LoopFrames + LoopFrames) % LoopFrames : 0;
        Paint(g, Frame(Loop, "loop", index), bounds, 1);
    }

    // Plays the intro once from <paramref name="elapsed"/> = 0, then rests.
    // Returns true while the intro is still moving.
    public static bool DrawIntro(Graphics g, RectangleF bounds, double elapsed)
    {
        if (!GlassPresentation.MotionEnabled) elapsed = IntroSeconds;
        int index = Math.Clamp((int)(elapsed * Fps), 0, IntroFrames - 1);
        Paint(g, Frame(Intro, "intro", index), bounds, (float)Math.Clamp(elapsed / .22, 0, 1));
        return index < IntroFrames - 1;
    }
}

public sealed record DeviceCard(string Title, string Status, bool Ready);

// Start screen: living backdrop, the 3D logo as hero, feature chips, and one
// illustrated card per platform showing what is actually connected right now.
sealed class GlassDeviceChooser : Panel
{
    static readonly Color Blue = Color.FromArgb(66, 140, 255), Teal = Color.FromArgb(58, 214, 190),
        Lime = Color.FromArgb(150, 226, 104), Violet = Color.FromArgb(168, 112, 244), Pink = Color.FromArgb(232, 150, 226);
    readonly UiAnimationClock _timer;
    double _started = GlassPresentation.Seconds, _lastAmbient;
    readonly RectangleF[] _choices = new RectangleF[2];
    readonly float[] _hover = new float[2];
    readonly DeviceCard[] _cards = [new("iPhone", "Looking for devices...", false), new("Android", "Looking for devices...", false)];
    int _hovered = -1, _focused, _pressed = -1;
    bool _introMoving = true;
    Bitmap? _backdrop;
    public event Action<StudioViewMode>? OnDeviceChosen;
    float Dpi => DeviceDpi / 96f;

    public GlassDeviceChooser()
    {
        _timer = new UiAnimationClock(this);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(9, 10, 14);
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        AccessibleName = "Choose a device";
        _timer.Tick += (_, _) =>
        {
            double now = GlassPresentation.Seconds;
            bool moving = _introMoving || now - _started < 2.2;
            for (int i = 0; i < 2; i++)
            {
                float target = _hovered == i ? 1 : 0;
                if (Math.Abs(target - _hover[i]) > .004f) { _hover[i] += (target - _hover[i]) * .2f; moving = true; }
                else _hover[i] = target;
            }
            // Ambient motion (drifting light, breathing halo) runs at ~30 Hz.
            if (!moving && GlassPresentation.MotionEnabled && now - _lastAmbient >= 1 / 30.0) moving = true;
            if (Visible && moving) { _lastAmbient = now; Invalidate(); }
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible) { _started = GlassPresentation.Seconds; _introMoving = true; _timer.Start(); }
            else _timer.Stop();
        };
        _timer.Start();
    }

    public void SetDevices(DeviceCard iphone, DeviceCard android)
    {
        if (_cards[0] == iphone && _cards[1] == android) return;
        _cards[0] = iphone; _cards[1] = android;
        Invalidate();
    }

    PointF Logical(Point p) => new(p.X / Dpi, p.Y / Dpi);
    int CardAt(Point p) { var l = Logical(p); return Array.FindIndex(_choices, r => r.Contains(l)); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); _backdrop?.Dispose(); _backdrop = null; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = CardAt(e.Location);
        Cursor = hovered < 0 ? Cursors.Default : Cursors.Hand;
        if (hovered != _hovered) { _hovered = hovered; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = -1; _pressed = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = CardAt(e.Location); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = CardAt(e.Location), pressed = _pressed;
        _pressed = -1; Invalidate();
        if (e.Button == MouseButtons.Left && index >= 0 && index == pressed) Choose(index);
    }
    void Choose(int index) => OnDeviceChosen?.Invoke(index == 0 ? StudioViewMode.IphoneOnly : StudioViewMode.AndroidOnly);
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Up or Keys.Down) { _focused = 1 - _focused; Invalidate(); e.Handled = true; }
        if (e.KeyCode is Keys.Enter or Keys.Space) { Choose(_focused); e.Handled = true; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    static LinearGradientBrush BrandBrush(RectangleF rect, int alpha, float angle = 0)
    {
        var brush = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), Color.White, Color.White, angle);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = [Color.FromArgb(alpha, Blue), Color.FromArgb(alpha, Teal), Color.FromArgb(alpha, Lime), Color.FromArgb(alpha, Violet), Color.FromArgb(alpha, Pink)],
            Positions = [0, .3f, .5f, .76f, 1]
        };
        return brush;
    }

    static void Glow(Graphics g, float cx, float cy, float radius, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
        using var fill = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(fill, path);
    }

    // Static layer: deep base and a fine dot lattice that fades toward the edges.
    Bitmap BuildBackdrop()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new LinearGradientBrush(new Point(0, -1), new Point(0, Height + 1), Color.FromArgb(14, 16, 24), Color.FromArgb(6, 7, 11)))
            g.FillRectangle(fill, ClientRectangle);
        float step = 18 * Dpi, cx = Width / 2f, cy = Height * .30f, reach = Math.Max(Width, Height) * .62f;
        for (float y = step / 2; y < Height; y += step)
        for (float x = step / 2; x < Width; x += step)
        {
            float fade = 1 - Math.Clamp(MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / reach, 0, 1);
            int alpha = (int)(34 * fade * fade);
            if (alpha < 2) continue;
            using var dot = new SolidBrush(Color.FromArgb(alpha, 200, 215, 255));
            g.FillEllipse(dot, x - .9f * Dpi, y - .9f * Dpi, 1.8f * Dpi, 1.8f * Dpi);
        }
        return bitmap;
    }

    static void Ribbon(Graphics g, float w, float h, double t, float baseY, float amplitude, float phase, int alpha)
    {
        var points = new PointF[9];
        for (int i = 0; i < points.Length; i++)
        {
            float x = -20 + (w + 40) * i / (points.Length - 1);
            points[i] = new PointF(x, baseY + amplitude * MathF.Sin((float)(t * .35 + phase + i * .82f)) + amplitude * .4f * MathF.Sin((float)(t * .21 + i * 1.7f + phase)));
        }
        using var brush = BrandBrush(new RectangleF(0, baseY - amplitude * 2, w, amplitude * 4 + 2), alpha);
        using var pen = new Pen(brush, 1.3f) { LineJoin = LineJoin.Round };
        g.DrawCurve(pen, points, .5f);
    }

    // A small illustrated handset whose screen carries the brand gradient when ready.
    static void DeviceArt(Graphics g, RectangleF r, bool iphone, bool ready, float alpha)
    {
        int a(float v) => (int)Math.Clamp(v * alpha, 0, 255);
        using var body = ShellRenderer.Round(r, 8);
        using (var frame = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb(a(255), 58, 62, 74), Color.FromArgb(a(255), 20, 22, 30), 60f))
            g.FillPath(frame, body);
        var screen = RectangleF.Inflate(r, -2.2f, -2.2f);
        using (var glass = ShellRenderer.Round(screen, 6))
        {
            if (ready)
            {
                using var fill = new LinearGradientBrush(RectangleF.Inflate(screen, 1, 1), Color.White, Color.White, 62f);
                fill.InterpolationColors = new ColorBlend
                {
                    Colors = iphone
                        ? [Color.FromArgb(a(255), Blue), Color.FromArgb(a(255), Violet), Color.FromArgb(a(255), Pink)]
                        : [Color.FromArgb(a(255), Teal), Color.FromArgb(a(255), Lime), Color.FromArgb(a(255), Blue)],
                    Positions = [0, .55f, 1]
                };
                g.FillPath(fill, glass);
                using var shine = new LinearGradientBrush(new PointF(screen.Left, screen.Top), new PointF(screen.Right, screen.Top + screen.Height * .7f),
                    Color.FromArgb(a(90), Color.White), Color.FromArgb(0, Color.White));
                g.FillPath(shine, glass);
            }
            else
            {
                using var fill = new SolidBrush(Color.FromArgb(a(255), 30, 33, 42));
                g.FillPath(fill, glass);
            }
        }
        using var dark = new SolidBrush(Color.FromArgb(a(230), 8, 9, 12));
        if (iphone) { using var island = ShellRenderer.Round(new RectangleF(r.X + r.Width / 2 - 5, r.Y + 5, 10, 3.4f), 1.7f); g.FillPath(dark, island); }
        else g.FillEllipse(dark, r.X + r.Width / 2 - 1.7f, r.Y + 5, 3.4f, 3.4f);
        using var rim = new Pen(Color.FromArgb(a(70), Color.White), .8f);
        g.DrawPath(rim, body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 40 || Height < 40) return;
        _backdrop ??= BuildBackdrop();
        var g = e.Graphics;
        g.DrawImageUnscaled(_backdrop, 0, 0);
        g.ScaleTransform(Dpi, Dpi);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float w = Width / Dpi, h = Height / Dpi;
        double now = GlassPresentation.Seconds, elapsed = now - _started;
        bool motion = GlassPresentation.MotionEnabled;
        double t = motion ? now : 0;
        bool roomy = h >= 600;

        // Drifting light in the logo colors.
        Glow(g, w * (.18f + .10f * MathF.Sin((float)(t * .23))), h * (.70f + .05f * MathF.Cos((float)(t * .19))), w * .78f, Color.FromArgb(78, Blue));
        Glow(g, w * (.86f + .08f * MathF.Cos((float)(t * .17))), h * (.10f + .05f * MathF.Sin((float)(t * .21))), w * .72f, Color.FromArgb(70, Violet));
        Glow(g, w * (.62f + .12f * MathF.Sin((float)(t * .13 + 2))), h * (.50f + .06f * MathF.Sin((float)(t * .27))), w * .50f, Color.FromArgb(34, Teal));
        Ribbon(g, w, h, t, h * .86f, 16, 0, 64);
        Ribbon(g, w, h, t, h * .90f, 22, 1.7f, 38);
        Ribbon(g, w, h, t, h * .055f, 10, 3.1f, 30);

        // Hero: breathing halo, then the 3D mark. After the intro it turns once every few seconds.
        float logoW = Math.Min(roomy ? 196 : 150, w * .60f), logoH = logoW * .75f;
        float logoTop = roomy ? Math.Max(18, h * .075f) : 8;
        float logoCx = w / 2, logoCy = logoTop + logoH / 2;
        float reveal = motion ? GlassPresentation.Ease(elapsed - .1, .9) : 1;
        float breathe = 1 + .05f * MathF.Sin((float)(t * 1.1));
        Glow(g, logoCx - logoW * .16f, logoCy + 4, logoW * .62f * breathe, Color.FromArgb((int)(86 * reveal), Blue));
        Glow(g, logoCx + logoW * .18f, logoCy + 4, logoW * .58f * breathe, Color.FromArgb((int)(78 * reveal), Violet));
        var logoRect = new RectangleF((w - logoW) / 2, logoTop, logoW, logoH);
        double afterIntro = elapsed - LogoAnimation.IntroSeconds;
        const double period = 7.0, turn = 1.6;
        if (motion && afterIntro > 2.5 && afterIntro % period < turn)
            LogoAnimation.DrawLoop(g, logoRect, afterIntro % period);
        else
            _introMoving = LogoAnimation.DrawIntro(g, logoRect, elapsed);

        float text = motion ? GlassPresentation.Ease(elapsed - .55, .5) : 1;
        float y = logoTop + logoH + (roomy ? 4 : 0) + (1 - text) * 8;
        using var center = new StringFormat { Alignment = StringAlignment.Center };
        using (var title = Theme.Font(roomy ? 17f : 14f, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb((int)(255 * text), GlassPresentation.StageInk)))
            g.DrawString("WAI Link", title, brush, new RectangleF(0, y, w, 32), center);
        y += roomy ? 31 : 25;
        using (var sub = Theme.Font(9f))
        using (var brush = new SolidBrush(Color.FromArgb((int)(255 * text), GlassPresentation.StageMuted)))
            g.DrawString("Your phones, live on your desktop", sub, brush, new RectangleF(0, y, w, 20), center);
        y += 26;

        if (roomy)
        {
            string[] chips = ["60 FPS mirror", "Touch + keys", "Files + OTP"];
            using var chipFont = Theme.Font(7.5f, FontStyle.Bold);
            float[] widths = chips.Select(c => g.MeasureString(c, chipFont).Width + 12).ToArray();
            float x = (w - widths.Sum() - 6 * (chips.Length - 1)) / 2;
            for (int i = 0; i < chips.Length; i++)
            {
                float chip = motion ? GlassPresentation.Ease(elapsed - .7 - i * .06, .45) : 1;
                var rect = new RectangleF(x, y + (1 - chip) * 6, widths[i], 20);
                using (var shape = ShellRenderer.Round(rect, 10))
                {
                    using var fill = new SolidBrush(Color.FromArgb((int)(16 * chip), Color.White));
                    g.FillPath(fill, shape);
                    using var edge = BrandBrush(rect, (int)(120 * chip));
                    using var pen = new Pen(edge, 1);
                    g.DrawPath(pen, shape);
                }
                using var brush = new SolidBrush(Color.FromArgb((int)(220 * chip), GlassPresentation.StageInk));
                using var middle = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(chips[i], chipFont, brush, new RectangleF(rect.X, rect.Y + .5f, rect.Width, rect.Height), middle);
                x += widths[i] + 6;
            }
            y += 38;
        }

        float cardW = Math.Min(300, w - 28), cardH = roomy ? 84 : 70, left = (w - cardW) / 2;
        int readyCount = _cards.Count(c => c.Ready);
        using (var caption = Theme.Font(7.5f, FontStyle.Bold))
        {
            using var brush = new SolidBrush(Color.FromArgb((int)(190 * text), GlassPresentation.StageMuted));
            g.DrawString("DEVICES", caption, brush, left + 4, y);
            using var right = new StringFormat { Alignment = StringAlignment.Far };
            using var countBrush = new SolidBrush(readyCount > 0 ? Color.FromArgb((int)(235 * text), 52, 211, 153) : Color.FromArgb((int)(170 * text), GlassPresentation.StageMuted));
            g.DrawString(readyCount > 0 ? $"{readyCount} READY" : "NONE READY", caption, countBrush, new RectangleF(left, y, cardW - 4, 16), right);
        }
        y += 20;

        for (int i = 0; i < 2; i++)
        {
            float progress = motion ? GlassPresentation.Ease(elapsed - .85 - i * .09, .55) : 1;
            var rest = new RectangleF(left, y + i * (cardH + 10), cardW, cardH);
            _choices[i] = rest;
            if (progress <= .004f) continue;
            float hover = _hover[i];
            var rect = rest; rect.Offset(0, (1 - progress) * 18 - hover * 2 + (_pressed == i ? 1.5f : 0));
            int alpha(float v) => (int)Math.Clamp(v * progress, 0, 255);
            var card = _cards[i];
            if (hover > .01f)
            {
                Glow(g, rect.X + rect.Width * .3f, rect.Bottom - 6, rect.Width * .5f, Color.FromArgb(alpha(46 * hover), i == 0 ? Violet : Teal));
            }
            using (var shape = ShellRenderer.Round(rect, 20))
            {
                using (var fill = new LinearGradientBrush(new PointF(0, rect.Top - 1), new PointF(0, rect.Bottom + 1),
                    Color.FromArgb(alpha(30 + hover * 16), Color.White), Color.FromArgb(alpha(10 + hover * 10), Color.White)))
                    g.FillPath(fill, shape);
                if (card.Ready)
                {
                    using var edge = BrandBrush(rect, alpha(150 + hover * 100), 20f);
                    using var pen = new Pen(edge, 1.2f);
                    g.DrawPath(pen, shape);
                }
                else
                {
                    using var pen = new Pen(Color.FromArgb(alpha(36 + hover * 40), Color.White), 1);
                    g.DrawPath(pen, shape);
                }
            }
            float artH = cardH - 22, artW = artH * .52f;
            DeviceArt(g, new RectangleF(rect.X + 18, rect.Y + 11, artW, artH), i == 0, card.Ready, progress);
            float tx = rect.X + 18 + artW + 14, textW = rect.Right - 92 - tx;
            var ink = Color.FromArgb(alpha(card.Ready ? 255 : 205), GlassPresentation.StageInk);
            using var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using (var kind = Theme.Font(7f, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.FromArgb(alpha(150), GlassPresentation.StageMuted)))
                g.DrawString(i == 0 ? "iOS" : "ANDROID", kind, brush, tx, rect.Y + cardH / 2 - 27);
            using (var name = Theme.Font(11.5f, FontStyle.Bold))
            using (var brush = new SolidBrush(ink))
                g.DrawString(card.Title, name, brush, new RectangleF(tx - 1, rect.Y + cardH / 2 - 15, textW, 22), trim);
            var dot = card.Ready ? Color.FromArgb(alpha(255), 52, 211, 153) : Color.FromArgb(alpha(200), 120, 128, 144);
            float dotY = rect.Y + cardH / 2 + 11.5f;
            if (card.Ready && motion)
            {
                float pulse = (float)(now * .9 % 1);
                using var ring = new Pen(Color.FromArgb(alpha(150 * (1 - pulse)), 52, 211, 153), 1);
                g.DrawEllipse(ring, tx + 3.25f - 3.25f * (1 + pulse), dotY + 3.25f - 3.25f * (1 + pulse), 6.5f * (1 + pulse), 6.5f * (1 + pulse));
            }
            using (var dotBrush = new SolidBrush(dot)) g.FillEllipse(dotBrush, tx, dotY, 6.5f, 6.5f);
            using (var status = Theme.Font(8.5f))
            using (var brush = new SolidBrush(Color.FromArgb(alpha(255), GlassPresentation.StageMuted)))
                g.DrawString(card.Status, status, brush, new RectangleF(tx + 10, rect.Y + cardH / 2 + 6, textW - 8, 18), trim);

            // Action pill: filled brand gradient when the phone is ready.
            var pill = new RectangleF(rect.Right - 82, rect.Y + cardH / 2 - 14, 66, 28);
            using (var pillShape = ShellRenderer.Round(pill, 14))
            {
                if (card.Ready)
                {
                    using var fill = new LinearGradientBrush(RectangleF.Inflate(pill, 1, 1), Color.FromArgb(alpha(255), Blue), Color.FromArgb(alpha(255), Violet), 0f);
                    g.FillPath(fill, pillShape);
                    using var gloss = new LinearGradientBrush(new PointF(0, pill.Top - 1), new PointF(0, pill.Top + pill.Height * .6f),
                        Color.FromArgb(alpha(70 + hover * 40), Color.White), Color.FromArgb(0, Color.White));
                    g.FillPath(gloss, pillShape);
                }
                else
                {
                    using var fill = new SolidBrush(Color.FromArgb(alpha(14 + hover * 16), Color.White));
                    g.FillPath(fill, pillShape);
                    using var pen = new Pen(Color.FromArgb(alpha(70 + hover * 60), Color.White), 1);
                    g.DrawPath(pen, pillShape);
                }
            }
            using (var action = Theme.Font(8.5f, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.FromArgb(alpha(card.Ready ? 255 : 215), Color.White)))
            using (var middle = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(card.Ready ? "Mirror" : "Connect", action, brush, new RectangleF(pill.X, pill.Y + .5f, pill.Width, pill.Height), middle);

            if (Focused && ShowFocusCues && _focused == i)
            {
                using var focus = new Pen(Color.FromArgb(220, 120, 190, 255), 1.5f);
                using var path = ShellRenderer.Round(RectangleF.Inflate(rect, 3, 3), 23);
                g.DrawPath(focus, path);
            }
        }

        float cardsBottom = y + cardH * 2 + 10;
        if (h - cardsBottom > 40)
        {
            using var hint = Theme.Font(8f);
            using var brush = new SolidBrush(Color.FromArgb((int)(170 * text), GlassPresentation.StageMuted));
            g.DrawString("Plug in by USB, or join the same Wi-Fi", hint, brush, new RectangleF(0, h - 30, w, 18), center);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _backdrop?.Dispose(); }
        base.Dispose(disposing);
    }
}
