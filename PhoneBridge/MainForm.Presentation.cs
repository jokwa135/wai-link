using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PhoneBridge;

// Shell presentation. The main form is only the opaque phone viewport (its
// window region is the screen). The glass chassis, hardware keys and controls
// live in a per-pixel-alpha frame window above it, and the drop shadow in a
// click-through window, so every edge is antialiased and truly translucent.
public sealed partial class MainForm
{
    const int StyleButtonId = 9;
    ShellFrameWindow? _frame;
    LayeredSurface? _shadow;
    Bitmap? _bodyLayer;
    Size _shadowSize;
    bool _bodyDirty = true, _frameDirty = true;
    UiAnimationClock _motionTimer = null!;
    readonly float[] _glyphScale = Enumerable.Repeat(1f, 12).ToArray();
    readonly float[] _glyphVelocity = new float[12];
    readonly double[] _glyphError = Enumerable.Repeat(-10.0, 12).ToArray();
    readonly bool[] _keyOnLeft = new bool[4];
    Rectangle _btnStyleRect;
    Bitmap? _homeLogo;
    double _motionLast;
    ViewportDissolve? _dissolve;

    bool IsGlyphHover(int i) => i is < 6 or StyleButtonId or UnlockButtonId or RotateButtonId ? _hoverTopId == i : _hoverBotId == i - 6;
    bool IsGlyphPressed(int i) => i is < 6 or StyleButtonId or UnlockButtonId or RotateButtonId ? _pressedTopId == i : _pressedBotId == i - 6;
    Point FrameOrigin => new(Left - ShellRenderer.PadPx, Top - ShellRenderer.PadPx);
    Point ShadowOrigin => new(Left - ShellRenderer.ShadowPadPx, Top - ShellRenderer.ShadowPadPx);

    void LoadLogo()
    {
        if (_homeLogo is not null) return;
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("PhoneBridge.Assets.Brand.logo.png");
        if (stream is not null) { using var image = Image.FromStream(stream); _homeLogo = new Bitmap(image); }
    }

    void StartPresentation()
    {
        LoadLogo();
        _motionLast = GlassPresentation.Seconds;
        ShellStyle.Changed += OnShellStyleChanged;
        _frame = new ShellFrameWindow(this);
        _shadow = new LayeredSurface(clickThrough: true);
        ApplyWindowRegion();
        _motionTimer = new UiAnimationClock(this);
        _motionTimer.Tick += (_, _) =>
        {
            double now = GlassPresentation.Seconds;
            float dt = (float)Math.Clamp(now - _motionLast, .001, .2);
            _motionLast = now;
            for (int i = 0; i < _glyphScale.Length; i++)
            {
                float target = !GlassPresentation.MotionEnabled ? 1 : IsGlyphPressed(i) ? .88f : IsGlyphHover(i) ? 1.06f : 1;
                GlassPresentation.Spring(ref _glyphScale[i], ref _glyphVelocity[i], target, dt);
                _frameDirty |= Math.Abs(target - _glyphScale[i]) > .001f || Math.Abs(_glyphVelocity[i]) > .01f;
                _frameDirty |= now - _glyphError[i] < 1.3;
            }
            if (_frameDirty) RenderFrame();
        };
        _motionTimer.Start();
        RenderFrame();
    }

    void OnShellStyleChanged() { _bodyDirty = true; _frameDirty = true; }
    void ResetGlassSurface() { _bodyDirty = true; _frameDirty = true; }
    protected override void OnInvalidated(InvalidateEventArgs e) { base.OnInvalidated(e); _frameDirty = true; }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); _frameDirty = true; if (!Visible) { _frame?.Hide(); _shadow?.Hide(); } }

    void MoveShellSurfaces()
    {
        _frame?.MoveTo(FrameOrigin);
        _shadow?.MoveTo(ShadowOrigin);
        _devicePopover.Dismiss();
        _stylePopup.Dismiss();
        _unlockPopup.Dismiss();
        if (_typeBox.IsOpen) ShowTypeBox();
    }

    void StopPresentation()
    {
        ShellStyle.Changed -= OnShellStyleChanged;
        _motionTimer?.Dispose();
        _devicePopover.Dispose();
        _stylePopup.Dispose();
        _typeBox.Dispose();
        _unlockPopup.Dispose();
        _frame?.Dispose(); _frame = null;
        _shadow?.Dispose(); _shadow = null;
        _bodyLayer?.Dispose(); _bodyLayer = null;
        _homeLogo?.Dispose();
        _dissolve?.Dispose(); _dissolve = null;
    }

    Bitmap ComposeFrame()
    {
        int w = Width, h = Height, pad = ShellRenderer.PadPx;
        if (_bodyLayer is null || _bodyDirty || _bodyLayer.Width != w + pad * 2 || _bodyLayer.Height != h + pad * 2)
        {
            _bodyLayer?.Dispose();
            _bodyLayer = ShellRenderer.Body(new Size(w, h), _stage.Bounds);
            _bodyDirty = false;
        }
        var surface = _bodyLayer.Clone(new Rectangle(Point.Empty, _bodyLayer.Size), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(surface);
        g.TranslateTransform(pad, pad);
        g.ScaleTransform(ShellRenderer.Scale, ShellRenderer.Scale);
        PaintGlassChrome(g, (int)Math.Round(w / ShellRenderer.Scale), (int)Math.Round(h / ShellRenderer.Scale));
        return surface;
    }

    void RenderFrame()
    {
        if (_closing || IsDisposed || _frame is null || _shadow is null) return;
        if (!Visible || WindowState == FormWindowState.Minimized || Width < 60 || Height < 60) return;
        _frameDirty = false;
        using (var surface = ComposeFrame()) _frame.Present(surface, FrameOrigin);
        if (_shadowSize != Size)
        {
            using var shadow = ShellRenderer.Shadow(Size, ShellRenderer.OuterRadius, ShellRenderer.ShadowPadPx);
            _shadow.Present(shadow, ShadowOrigin);
            _shadowSize = Size;
        }
        else _shadow.MoveTo(ShadowOrigin);
        if (!_shadow.Visible) _shadow.Show(this);
        if (!_frame.Visible) _frame.Show(this);
    }

    void BeginViewportTransition()
    {
        if (!GlassPresentation.MotionEnabled || _stage.Width < 10 || _stage.Height < 10 || !IsHandleCreated) return;
        _dissolve?.Dispose();
        var image = new Bitmap(_stage.Width, _stage.Height);
        _stage.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        _dissolve = new ViewportDissolve(image, _stage) { Bounds = _stage.Bounds };
        Controls.Add(_dissolve); _dissolve.BringToFront();
        _dissolve.Start();
    }
    void LayoutTransition() { if (_dissolve is { IsDisposed: false }) _dissolve.Bounds = _stage.Bounds; }

    void TogglePicker()
    {
        if (_viewMode == StudioViewMode.None) return;
        _stylePopup.Dismiss();
        _unlockPopup.Dismiss();
        _devicePopover.SelectedMode = _viewMode;
        _devicePopover.Toggle(new Point(Left + S(6), Top + TopBarH + S(4)));
    }
    void ToggleStylePopup()
    {
        _devicePopover.Dismiss();
        _unlockPopup.Dismiss();
        _stylePopup.Toggle(new Point(Left + Width - S(232 + 6), Top + TopBarH + S(4)));
    }
    Rectangle ShellToScreen(Rectangle r) => new(Left + S(r.X), Top + S(r.Y), S(r.Width), S(r.Height));

    void PaintGlassChrome(Graphics g, int w, int h)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var ink = ShellStyle.Ink;
        bool selected = _viewMode != StudioViewMode.None;
        bool iphone = _viewMode == StudioViewMode.IphoneOnly;
        float cy = BarTop / 2f;

        // Brand mark + active device, which doubles as the device switcher.
        string title = !selected ? "WAI Link" : iphone ? "iPhone" : "Android";
        using (var font = Theme.Font(8.5f, FontStyle.Bold))
        {
            float textWidth = g.MeasureString(title, font, 400, StringFormat.GenericTypographic).Width;
            var trigger = new Rectangle(8, (BarTop - 18) / 2, (int)Math.Ceiling(27 + textWidth + (selected ? 18 : 8)), 18);
            _pickerTriggerRect = selected ? trigger : Rectangle.Empty;
            if (selected && (_hoverTopId == 0 || _devicePopover.IsOpen))
            {
                using var fill = new SolidBrush(Color.FromArgb(_pressedTopId == 0 ? 44 : 28, ink));
                using var shape = ShellRenderer.Round(trigger, 9);
                g.FillPath(fill, shape);
            }
            if (_homeLogo is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_homeLogo, new RectangleF(trigger.X + 6, cy - 5.6f, 15.8f, 11.2f));
            }
            using var brush = new SolidBrush(Color.FromArgb(235, ink));
            using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            g.DrawString(title, font, brush, new RectangleF(trigger.X + 26, 0, textWidth + 6, BarTop + 1), format);
            if (selected)
            {
                var state = g.Save();
                g.TranslateTransform(trigger.Right - 10, cy + .5f);
                g.RotateTransform(180 * Math.Clamp(_devicePopover.Progress, 0, 1));
                using var pen = InkPen(Color.FromArgb(190, ink), 1.3f);
                g.DrawLines(pen, [new PointF(-2.8f, -1.4f), new PointF(0, 1.4f), new PointF(2.8f, -1.4f)]);
                g.Restore(state);
            }
        }

        int by = (BarTop - 18) / 2;
        _btnCloseRect = new Rectangle(w - 32, by, 22, 18);
        _btnMinRect = new Rectangle(w - 56, by, 22, 18);
        _btnStyleRect = new Rectangle(w - 80, by, 22, 18);
        DrawBarGlyph(g, _btnStyleRect, StyleButtonId);
        _btnUnlockRect = iphone ? new Rectangle(w - 104, by, 22, 18) : Rectangle.Empty;
        if (iphone) DrawBarGlyph(g, _btnUnlockRect, UnlockButtonId);
        _btnRotateRect = selected ? new Rectangle(w - (iphone ? 128 : 104), by, 22, 18) : Rectangle.Empty;
        if (selected) DrawBarGlyph(g, _btnRotateRect, RotateButtonId);
        DrawBarGlyph(g, _btnMinRect, 4);
        DrawBarGlyph(g, _btnCloseRect, 5);

        if (selected && !_landscape)
        {
            // Hardware keys sit where the real device has them.
            if (iphone)
            {
                int top = (int)(h * .21);
                SetKey(1, true, top, 36, w); SetKey(2, true, top + 44, 36, w); SetKey(3, false, (int)(h * .26), 58, w);
            }
            else
            {
                int top = (int)(h * .19);
                SetKey(1, false, top, 34, w); SetKey(2, false, top + 38, 34, w); SetKey(3, false, top + 92, 44, w);
            }
            DrawHardwareKey(g, _btnVolUpRect, 1, w);
            DrawHardwareKey(g, _btnVolDownRect, 2, w);
            DrawHardwareKey(g, _btnPowerRect, 3, w);
        }
        else _btnVolUpRect = _btnVolDownRect = _btnPowerRect = Rectangle.Empty;

        if (selected)
        {
            int third = (w - 48) / 3;
            _botBtn0Rect = new Rectangle(24, h - BarBottom + 2, third, BarBottom - 5);
            _botBtn1Rect = new Rectangle(24 + third, h - BarBottom + 2, third, BarBottom - 5);
            _botBtn2Rect = new Rectangle(24 + third * 2, h - BarBottom + 2, third, BarBottom - 5);
            DrawBarGlyph(g, _botBtn0Rect, 6);
            DrawBarGlyph(g, _botBtn1Rect, 7);
            DrawBarGlyph(g, _botBtn2Rect, 8);
        }
        else
        {
            _btnVolUpRect = _btnVolDownRect = _btnPowerRect = Rectangle.Empty;
            _botBtn0Rect = _botBtn1Rect = _botBtn2Rect = Rectangle.Empty;
        }
    }

    void SetKey(int kind, bool left, int y, int length, int w)
    {
        _keyOnLeft[kind] = left;
        var rect = left ? new Rectangle(-ShellRenderer.Pad, y - 3, ShellRenderer.Pad + Side, length + 6)
                        : new Rectangle(w - Side, y - 3, ShellRenderer.Pad + Side, length + 6);
        if (kind == 1) _btnVolUpRect = rect; else if (kind == 2) _btnVolDownRect = rect; else _btnPowerRect = rect;
    }

    static Pen InkPen(Color color, float width = 1.4f) => new(color, width)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    void PulseGlyph(int index) { }

    async Task RunIphoneButtonAsync(Func<Task<bool>> action, int index)
    {
        bool ok;
        try { ok = await action(); }
        catch { ok = false; }
        if (!ok && !IsDisposed)
        {
            _glyphError[index] = GlassPresentation.Seconds;
            Invalidate();
        }
    }

    // A machined key that protrudes from the frame edge and travels inward when pressed.
    void DrawHardwareKey(Graphics g, Rectangle hit, int kind, int w)
    {
        bool left = _keyOnLeft[kind];
        bool pressed = _pressedTopId == kind, hover = _hoverTopId == kind;
        bool error = GlassPresentation.Seconds - _glyphError[kind] < 1.3;
        float travel = pressed ? 1.4f : 0;
        float x = left ? -2.6f + travel : w - 1.4f - travel;
        var key = new RectangleF(x, hit.Y + 3, 4, hit.Height - 6);
        var metal = ShellStyle.IsLight ? Mix(ShellStyle.Color, Color.Black, .16f) : Mix(ShellStyle.Color, Color.White, .40f);
        var bright = Mix(metal, Color.White, .55f);
        var dark = Mix(metal, Color.Black, .35f);
        if (hover) { metal = Mix(metal, ShellStyle.Accent, .55f); bright = Mix(bright, ShellStyle.Accent, .25f); }
        if (error) { metal = Color.FromArgb(214, 62, 82); bright = Color.FromArgb(255, 150, 160); }
        using var shape = ShellRenderer.Round(key, 1.9f);
        using (var fill = new LinearGradientBrush(new PointF(key.Left - .5f, 0), new PointF(key.Right + .5f, 0), left ? bright : dark, left ? dark : bright))
        {
            fill.InterpolationColors = new ColorBlend
            {
                Colors = left ? [bright, metal, dark] : [dark, metal, bright],
                Positions = [0, .45f, 1]
            };
            g.FillPath(fill, shape);
        }
        using var outline = new Pen(Color.FromArgb(110, 0, 0, 0), .6f);
        g.DrawPath(outline, shape);
        using var glint = new Pen(Color.FromArgb(150, Color.White), .6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float gx = left ? key.Left + .9f : key.Right - .9f;
        g.DrawLine(glint, gx, key.Top + 3, gx, key.Bottom - 3);
    }

    // Top-bar buttons (style, minimize, close) and bottom navigation (back, home, recents).
    void DrawBarGlyph(Graphics g, Rectangle rect, int id)
    {
        bool hover = IsGlyphHover(id), pressed = IsGlyphPressed(id);
        bool top = id is 4 or 5 or StyleButtonId or UnlockButtonId or RotateButtonId;
        var ink = ShellStyle.Ink;
        var color = Color.FromArgb(top ? 215 : 225, ink);
        float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f;
        if (hover || pressed)
        {
            RectangleF plate = top ? rect : new RectangleF(cx - 24, cy - 12, 48, 24);
            using var shape = ShellRenderer.Round(plate, top ? 7 : 12);
            var tint = id == 5 ? Color.FromArgb(pressed ? 255 : 232, 226, 58, 76) : Color.FromArgb(pressed ? 46 : 28, ink);
            using var fill = new SolidBrush(tint);
            g.FillPath(fill, shape);
            if (id == 5) color = Color.White;
        }
        var state = g.Save();
        g.TranslateTransform(cx, cy);
        float scale = Math.Clamp(_glyphScale[id], .85f, 1.08f);
        g.ScaleTransform(scale, scale);
        using var pen = InkPen(color, top ? 1.25f : 1.5f);
        switch (id)
        {
            case StyleButtonId:
                g.DrawLine(pen, -4.5f, -2.5f, 4.5f, -2.5f); g.DrawLine(pen, -4.5f, 2.5f, 4.5f, 2.5f);
                using (var knob = new SolidBrush(color))
                {
                    g.FillEllipse(knob, .4f, -4.5f, 4, 4); g.FillEllipse(knob, -4.2f, .5f, 4, 4);
                }
                break;
            case RotateButtonId:
                // A phone outline that turns with the current orientation, plus a turn arrow.
                using (var phone = ShellRenderer.Round(_landscape ? new RectangleF(-5.5f, -1.5f, 9, 5.5f) : new RectangleF(-3.8f, -4.5f, 5.5f, 9), 1.4f)) g.DrawPath(pen, phone);
                g.DrawArc(pen, -5.5f, -5.5f, 11, 11, 300, 70);
                g.DrawLines(pen, [new PointF(4.2f, -5.8f), new PointF(4.9f, -2.9f), new PointF(2.2f, -2.4f)]);
                break;
            case UnlockButtonId:
                using (var body = ShellRenderer.Round(new RectangleF(-4.5f, -1, 9, 6.5f), 1.8f)) g.DrawPath(pen, body);
                g.DrawArc(pen, -2.8f, -5.5f, 5.6f, 7, 180, _unlockTaps.Count > 0 ? 180 : 150);
                if (_unlockRecording)
                    using (var dot = new SolidBrush(Color.FromArgb(235, 255, 80, 96))) g.FillEllipse(dot, 3.2f, -6.8f, 3.6f, 3.6f);
                break;
            case 4: g.DrawLine(pen, -4, 0, 4, 0); break;
            case 5: g.DrawLine(pen, -3.4f, -3.4f, 3.4f, 3.4f); g.DrawLine(pen, 3.4f, -3.4f, -3.4f, 3.4f); break;
            case 6: g.DrawLines(pen, [new PointF(2.6f, -5.5f), new PointF(-3, 0), new PointF(2.6f, 5.5f)]); break;
            case 7:
                if (_homeLogo is not null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_homeLogo, new RectangleF(-12.6f, -9, 25.2f, 18));
                }
                break;
            case 8:
                using (var front = ShellRenderer.Round(new RectangleF(-6.5f, -3, 9.5f, 9.5f), 2.6f)) g.DrawPath(pen, front);
                using (var back = new GraphicsPath())
                {
                    back.AddLine(-2.5f, -6.5f, 4, -6.5f);
                    back.AddArc(1.5f, -6.5f, 5, 5, 270, 90);
                    back.AddLine(6.5f, -4, 6.5f, 2.5f);
                    g.DrawPath(pen, back);
                }
                break;
        }
        g.Restore(state);
    }

    // Writes still images of the shell over dark and light wallpapers without
    // starting any device service, so the look can be reviewed offline.
    internal void RenderPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        LoadLogo();
        var original = (ShellStyle.Color, ShellStyle.Opacity);
        var shots = new (string Name, StudioViewMode Mode, Color Color, float Opacity, bool LightWall, int Popup)[]
        {
            ("iphone-graphite", StudioViewMode.IphoneOnly, ShellStyle.Presets[0].Color, .88f, false, 0),
            ("android-graphite-picker", StudioViewMode.AndroidOnly, ShellStyle.Presets[0].Color, .88f, false, 1),
            ("iphone-graphite-lightwall-style", StudioViewMode.IphoneOnly, ShellStyle.Presets[0].Color, .88f, true, 2),
            ("iphone-midnight-55", StudioViewMode.IphoneOnly, ShellStyle.Presets[1].Color, .55f, false, 0),
            ("iphone-frost-70", StudioViewMode.IphoneOnly, ShellStyle.Presets[6].Color, .70f, true, 0),
            ("start-graphite", StudioViewMode.None, ShellStyle.Presets[0].Color, .88f, false, 0),
        };
        foreach (var shot in shots)
        {
            ShellStyle.Set(shot.Color, shot.Opacity);
            _viewMode = shot.Mode;
            _devicePopover.SelectedMode = shot.Mode;
            _bodyDirty = true;
            LayoutViewport();
            const int margin = 90;
            using var canvas = new Bitmap(Width + margin * 2, Height + margin * 2, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var all = new Rectangle(Point.Empty, canvas.Size);
                using (var wall = new LinearGradientBrush(all, Color.Black, Color.Black, 35f))
                {
                    wall.InterpolationColors = new ColorBlend
                    {
                        Colors = shot.LightWall
                            ? [Color.FromArgb(236, 240, 246), Color.FromArgb(180, 205, 240), Color.FromArgb(248, 226, 214), Color.FromArgb(238, 242, 248)]
                            : [Color.FromArgb(14, 12, 16), Color.FromArgb(120, 22, 34), Color.FromArgb(34, 22, 30), Color.FromArgb(8, 8, 12)],
                        Positions = [0, .38f, .62f, 1]
                    };
                    g.FillRectangle(wall, all);
                }
                using (var streak = new Pen(Color.FromArgb(shot.LightWall ? 150 : 110, Color.White), 3))
                    g.DrawBezier(streak, -20, canvas.Height * .55f, canvas.Width * .3f, canvas.Height * .3f, canvas.Width * .6f, canvas.Height * .8f, canvas.Width + 20, canvas.Height * .35f);
                using (var shadow = ShellRenderer.Shadow(Size, ShellRenderer.OuterRadius, ShellRenderer.ShadowPadPx))
                    g.DrawImageUnscaled(shadow, margin - ShellRenderer.ShadowPadPx, margin - ShellRenderer.ShadowPadPx);
                var screen = _stage.Bounds; screen.Offset(margin, margin);
                using (var screenPath = ShellRenderer.Round(screen, ShellRenderer.StageRadius * ShellRenderer.Scale))
                using (var content = new LinearGradientBrush(screen, Color.FromArgb(250, 250, 252), Color.FromArgb(226, 232, 244), 90f))
                    g.FillPath(content, screenPath);
                using (var frame = ComposeFrame())
                    g.DrawImageUnscaled(frame, margin - ShellRenderer.PadPx, margin - ShellRenderer.PadPx);
                if (shot.Popup != 0)
                {
                    GlassPopup popup = shot.Popup == 1 ? _devicePopover : _stylePopup;
                    using var sheet = popup.RenderStill();
                    int x = shot.Popup == 1 ? margin + S(6) : margin + Width - S(232 + 6);
                    g.DrawImageUnscaled(sheet, x - GlassPopup.StillGutter, margin + TopBarH + S(4) - GlassPopup.StillGutter);
                }
            }
            canvas.Save(Path.Combine(directory, shot.Name + ".png"), ImageFormat.Png);
        }
        ShellStyle.Set(original.Color, original.Opacity);
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
    }
}

sealed class ViewportDissolve(Bitmap image, Control nextSurface) : Control
{
    UiAnimationClock _timer = null!;
    Bitmap? _frost;
    double _start;
    float _alpha = 1;
    public void Start()
    {
        _timer = new UiAnimationClock(this);
        DoubleBuffered = true;
        using (var small = new Bitmap(Math.Max(1, image.Width / 12), Math.Max(1, image.Height / 12)))
        {
            using (var g = Graphics.FromImage(small)) g.DrawImage(image, new Rectangle(Point.Empty, small.Size));
            _frost = new Bitmap(image.Width, image.Height);
            using var gFrost = Graphics.FromImage(_frost);
            gFrost.InterpolationMode = InterpolationMode.HighQualityBilinear;
            gFrost.DrawImage(small, new Rectangle(Point.Empty, image.Size));
        }
        _start = GlassPresentation.Seconds;
        _timer.Tick += (_, _) =>
        {
            _alpha = 1 - GlassPresentation.Ease(GlassPresentation.Seconds - _start, .24);
            if (_alpha <= .001f) { Dispose(); return; }
            Invalidate();
        };
        _timer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var next = new Bitmap(Math.Max(1, nextSurface.Width), Math.Max(1, nextSurface.Height));
        nextSurface.DrawToBitmap(next, new Rectangle(Point.Empty, next.Size));
        e.Graphics.DrawImage(next, ClientRectangle);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = _alpha });
        e.Graphics.DrawImage(image, ClientRectangle, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        if (_frost is not null)
        {
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = MathF.Sin(_alpha * MathF.PI) * .55f });
            e.Graphics.DrawImage(_frost, ClientRectangle, 0, 0, _frost.Width, _frost.Height, GraphicsUnit.Pixel, attributes);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); image.Dispose(); _frost?.Dispose(); } base.Dispose(disposing); }
}
