using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PhoneBridge;

// A floating glass sheet with its own soft shadow. It is a separate layered
// window, so it is never clipped by the shell and can overlap the live video.
abstract class GlassPopup : LayeredSurface
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    protected const int Gutter = 30;
    const float Radius = 16;
    readonly Form _shell;
    readonly UiAnimationClock _clock;
    double _last;
    float _progress, _velocity;
    bool _open, _dirty;
    Point _anchor;
    Bitmap? _shadow;

    public bool IsOpen => _open;
    public float Progress => _progress;
    public event Action? AnimationChanged;
    public Func<Rectangle>? TriggerScreenBounds { get; set; }
    protected bool SuspendAutoDismiss { get; set; }
    protected Form Shell => _shell;
    protected abstract Size ContentSize { get; }
    protected abstract void PaintContent(Graphics g, Size size);
    static int GutterPx => (int)Math.Ceiling(Gutter * ShellRenderer.Scale);
    Size ContentPx => new((int)Math.Ceiling(ContentSize.Width * ShellRenderer.Scale), (int)Math.Ceiling(ContentSize.Height * ShellRenderer.Scale));
    protected Point ToContent(Point p) => new((int)((p.X - GutterPx) / ShellRenderer.Scale), (int)((p.Y - GutterPx) / ShellRenderer.Scale));

    protected GlassPopup(Form shell)
    {
        _shell = shell;
        _ = Handle;
        _clock = new UiAnimationClock(this);
        _clock.Tick += (_, _) => Tick();
        ShellStyle.Changed += Repaint;
    }

    public void Open(Point anchorScreen)
    {
        _anchor = anchorScreen;
        _open = true; _dirty = true;
        _last = GlassPresentation.Seconds;
        OnOpening();
        Render();
        if (!Visible) Show(_shell);
        _clock.Start();
    }
    public void Dismiss()
    {
        if (!_open) return;
        _open = false;
        _last = _dismissedAt = GlassPresentation.Seconds;
        _clock.Start();
    }
    double _dismissedAt = double.NegativeInfinity;

    // Clicking the trigger of an open sheet closes it. The press itself may already have dismissed it
    // (focus loss or the outside-click check), so a toggle right after a dismiss keeps it closed.
    public void Toggle(Point anchorScreen)
    {
        if (_open) Dismiss();
        else if (GlassPresentation.Seconds - _dismissedAt > .35) Open(anchorScreen);
    }
    protected virtual void OnOpening() { }
    protected void Repaint() { _dirty = true; if (!_clock.Enabled && Visible) Render(); }

    void Tick()
    {
        double now = GlassPresentation.Seconds;
        float dt = (float)Math.Clamp(now - _last, .001, .2); _last = now;
        float target = _open ? 1 : 0;
        bool moving = Math.Abs(target - _progress) >= .002f || Math.Abs(_velocity) >= .02f;
        if (moving)
        {
            if (GlassPresentation.MotionEnabled) GlassPresentation.Spring(ref _progress, ref _velocity, target, dt);
            else { _progress = target; _velocity = 0; }
            _dirty = true;
            AnimationChanged?.Invoke();
        }
        else if (_progress != target) { _progress = target; _velocity = 0; _dirty = true; AnimationChanged?.Invoke(); }

        if (!_open && !moving)
        {
            _clock.Stop(); Hide();
            return;
        }
        // The live Android video belongs to another process, so a click on it
        // never reaches this thread. Poll the button instead of relying on focus.
        if (_open && !SuspendAutoDismiss && GetAsyncKeyState(0x01) < 0 && Capture == false)
        {
            var cursor = Cursor.Position;
            var body = new Rectangle(_anchor, ContentPx);
            if (!body.Contains(cursor) && TriggerScreenBounds?.Invoke().Contains(cursor) != true) Dismiss();
        }
        if (_dirty) Render();
    }

    Bitmap Compose(Bitmap shadow)
    {
        var content = ContentSize; var px = ContentPx; int gutter = GutterPx;
        var surface = new Bitmap(px.Width + gutter * 2, px.Height + gutter * 2, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(surface);
        g.DrawImageUnscaled(shadow, 0, 0);
        g.TranslateTransform(gutter, gutter);
        g.ScaleTransform(ShellRenderer.Scale, ShellRenderer.Scale);
        ShellRenderer.Panel(g, new RectangleF(0, 0, content.Width, content.Height), Radius, Math.Max(ShellStyle.Opacity, .94f));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        PaintContent(g, content);
        return surface;
    }

    void Render()
    {
        _dirty = false;
        var px = ContentPx; int gutter = GutterPx;
        if (_shadow is null || _shadow.Width != px.Width + gutter * 2 || _shadow.Height != px.Height + gutter * 2)
        {
            _shadow?.Dispose();
            _shadow = ShellRenderer.Shadow(px, Radius, gutter, 1.15f);
        }
        using var surface = Compose(_shadow);
        float p = Math.Clamp(_progress, 0, 1);
        Present(surface, new Point(_anchor.X - gutter, _anchor.Y - gutter - (int)Math.Round((1 - p) * 8 * ShellRenderer.Scale)), (byte)Math.Round(255 * p));
    }

    // Renders the sheet at rest for offline previews.
    public Bitmap RenderStill()
    {
        using var shadow = ShellRenderer.Shadow(ContentPx, Radius, GutterPx, 1.15f);
        return Compose(shadow);
    }
    public static int StillGutter => GutterPx;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ShellStyle.Changed -= Repaint; _clock.Dispose(); _shadow?.Dispose(); }
        base.Dispose(disposing);
    }
}

sealed class DevicePickerPopup(Form shell) : GlassPopup(shell)
{
    const int RowH = 44, Inset = 6;
    int _hover = -1, _focus;
    public StudioViewMode SelectedMode { get; set; }
    public event Action<StudioViewMode>? OnModeSelected;
    protected override Size ContentSize => new(204, Inset * 2 + RowH * 2);

    protected override void OnOpening() { _focus = SelectedMode == StudioViewMode.AndroidOnly ? 1 : 0; _hover = -1; }

    int RowAt(Point p)
    {
        var c = ToContent(p);
        if (c.X < Inset || c.X >= ContentSize.Width - Inset || c.Y < Inset || c.Y >= Inset + RowH * 2) return -1;
        return (c.Y - Inset) / RowH;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Location);
        Cursor = row < 0 ? Cursors.Default : Cursors.Hand;
        if (row != _hover) { _hover = row; Repaint(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Repaint(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int row = RowAt(e.Location);
        if (IsOpen && e.Button == MouseButtons.Left && row >= 0) Select(row);
    }
    void Select(int row) { Dismiss(); OnModeSelected?.Invoke(row == 0 ? StudioViewMode.IphoneOnly : StudioViewMode.AndroidOnly); }

    public bool HandleKey(Keys key)
    {
        if (!IsOpen) return false;
        switch (key)
        {
            case Keys.Up or Keys.Down: _focus = 1 - _focus; _hover = _focus; Repaint(); return true;
            case Keys.Enter or Keys.Space: Select(_focus); return true;
            case Keys.Escape: Dismiss(); return true;
        }
        return false;
    }

    protected override void PaintContent(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ink = ShellStyle.Ink;
        for (int i = 0; i < 2; i++)
        {
            var row = new RectangleF(Inset, Inset + i * RowH, size.Width - Inset * 2, RowH);
            bool selected = SelectedMode == (i == 0 ? StudioViewMode.IphoneOnly : StudioViewMode.AndroidOnly);
            if (_hover == i || selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(_hover == i ? 30 : 16, ink));
                using var shape = ShellRenderer.Round(RectangleF.Inflate(row, 0, -2), 11);
                g.FillPath(fill, shape);
            }
            GlassPresentation.Phone(g, new PointF(row.X + 22, row.Y + RowH / 2f), 22, i == 0, ink);
            using var font = Theme.Font(9.75f, selected ? FontStyle.Bold : FontStyle.Regular);
            using var brush = new SolidBrush(ink);
            using var format = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(i == 0 ? "iPhone" : "Android", font, brush, new RectangleF(row.X + 42, row.Y, row.Width - 80, RowH), format);
            if (selected)
            {
                using var pen = new Pen(ShellStyle.Accent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float cx = row.Right - 20, cy = row.Y + RowH / 2f;
                g.DrawLines(pen, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 3.5f)]);
            }
        }
    }
}

// Shell appearance: opacity slider and color swatches. Changes apply live.
sealed class ShellStylePopup(Form shell) : GlassPopup(shell)
{
    const int Edge = 16, TrackY = 46, SwatchY = 86, Swatch = 20, ToggleY = 124;
    bool _dragging;
    int _hover = -1;
    protected override Size ContentSize => new(232, 234);
    int TrackWidth => ContentSize.Width - Edge * 2;
    int SwatchCount => ShellStyle.Presets.Length + 1;
    RectangleF SwatchRect(int i)
    {
        float gap = (TrackWidth - SwatchCount * Swatch) / (float)(SwatchCount - 1);
        return new RectangleF(Edge + i * (Swatch + gap), SwatchY, Swatch, Swatch);
    }
    const int ToggleStep = 38;
    int ToggleAt(Point c)
    {
        if (c.X < Edge - 4 || c.X > ContentSize.Width - Edge + 4) return -1;
        for (int i = 0; i < 3; i++) if (c.Y >= ToggleY + i * ToggleStep - 2 && c.Y <= ToggleY + i * ToggleStep + 26) return i;
        return -1;
    }
    bool OnToggle(Point c) => ToggleAt(c) >= 0;
    int SwatchAt(Point c)
    {
        for (int i = 0; i < SwatchCount; i++) if (RectangleF.Inflate(SwatchRect(i), 3, 4).Contains(c)) return i;
        return -1;
    }

    void SetOpacityFrom(int x)
    {
        float t = Math.Clamp((x - Edge) / (float)TrackWidth, 0, 1);
        ShellStyle.Set(ShellStyle.Color, ShellStyle.MinOpacity + t * (1 - ShellStyle.MinOpacity));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var c = ToContent(e.Location);
        if (e.Button == MouseButtons.Left && c.Y >= TrackY - 14 && c.Y <= TrackY + 16) { _dragging = true; SetOpacityFrom(c.X); }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var c = ToContent(e.Location);
        if (_dragging) { SetOpacityFrom(c.X); return; }
        int hover = SwatchAt(c);
        Cursor = hover >= 0 || OnToggle(c) || (c.Y >= TrackY - 14 && c.Y <= TrackY + 16) ? Cursors.Hand : Cursors.Default;
        if (hover != _hover) { _hover = hover; Repaint(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Repaint(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; ShellStyle.Save(); return; }
        if (e.Button == MouseButtons.Left && OnToggle(ToContent(e.Location)))
        {
            int toggle = ToggleAt(ToContent(e.Location));
            if (toggle == 0) ShellStyle.SetTypeBox(!ShellStyle.TypeBox);
            else if (toggle == 1) ShellStyle.SetPhoneSound(!ShellStyle.PhoneSound);
            else ShellStyle.SetAndroidTweaks(!ShellStyle.AndroidTweaks);
            ShellStyle.Save();
            return;
        }
        int index = SwatchAt(ToContent(e.Location));
        if (e.Button != MouseButtons.Left || index < 0) return;
        if (index < ShellStyle.Presets.Length)
        {
            ShellStyle.Set(ShellStyle.Presets[index].Color, ShellStyle.Opacity);
            ShellStyle.Save();
            return;
        }
        SuspendAutoDismiss = true;
        try
        {
            using var dialog = new ColorDialog { Color = ShellStyle.Color, FullOpen = true };
            if (dialog.ShowDialog(Shell) == DialogResult.OK)
            {
                ShellStyle.Set(dialog.Color, ShellStyle.Opacity);
                ShellStyle.Save();
            }
        }
        finally { SuspendAutoDismiss = false; }
    }

    void DrawToggle(Graphics g, float y, string title, string hint, bool on, Font label, Brush inkBrush, Brush mutedBrush, Color ink)
    {
        g.DrawString(title, label, inkBrush, Edge - 2, y + 1);
        using (var hintFont = Theme.Font(7.5f)) g.DrawString(hint, hintFont, mutedBrush, Edge - 2, y + 15);
        var toggle = new RectangleF(ContentSize.Width - Edge - 32, y + 4, 32, 18);
        using (var trackShape = ShellRenderer.Round(toggle, 9))
        {
            using var fill = new SolidBrush(on ? ShellStyle.Accent : Color.FromArgb(60, ink));
            g.FillPath(fill, trackShape);
        }
        g.FillEllipse(Brushes.White, on ? toggle.Right - 16 : toggle.X + 2, toggle.Y + 2, 14, 14);
    }

    protected override void PaintContent(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ink = ShellStyle.Ink;
        using var label = Theme.Font(8.75f, FontStyle.Bold);
        using var value = Theme.Font(8.75f);
        using var inkBrush = new SolidBrush(ink);
        using var mutedBrush = new SolidBrush(ShellStyle.Muted);
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString("Opacity", label, inkBrush, Edge - 2, 14);
        g.DrawString($"{Math.Round(ShellStyle.Opacity * 100)}%", value, mutedBrush, new RectangleF(Edge, 14, TrackWidth + 2, 18), right);

        float t = (ShellStyle.Opacity - ShellStyle.MinOpacity) / (1 - ShellStyle.MinOpacity);
        float knobX = Edge + t * TrackWidth;
        using (var track = ShellRenderer.Round(new RectangleF(Edge, TrackY - 2, TrackWidth, 4), 2))
        using (var fill = new SolidBrush(Color.FromArgb(46, ink))) g.FillPath(fill, track);
        if (knobX - Edge > 1)
        {
            using var active = ShellRenderer.Round(new RectangleF(Edge, TrackY - 2, knobX - Edge, 4), 2);
            using var fill = new SolidBrush(ShellStyle.Accent);
            g.FillPath(fill, active);
        }
        using (var knobShadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(knobShadow, knobX - 8, TrackY - 7, 16, 16);
        g.FillEllipse(Brushes.White, knobX - 7.5f, TrackY - 7.5f, 15, 15);

        g.DrawString("Color", label, inkBrush, Edge - 2, SwatchY - 22);
        using (var sep = new Pen(Color.FromArgb(34, ink), 1)) g.DrawLine(sep, Edge, ToggleY - 9, ContentSize.Width - Edge, ToggleY - 9);
        DrawToggle(g, ToggleY, "iPhone type box", "Type on the PC, send on Enter", ShellStyle.TypeBox, label, inkBrush, mutedBrush, ink);
        DrawToggle(g, ToggleY + ToggleStep, "iPhone sound on PC", "Plays the phone's sound in iPhone view", ShellStyle.PhoneSound, label, inkBrush, mutedBrush, ink);
        DrawToggle(g, ToggleY + ToggleStep * 2, "Android speed tweaks", "Changes phone settings on connect", ShellStyle.AndroidTweaks, label, inkBrush, mutedBrush, ink);
        for (int i = 0; i < SwatchCount; i++)
        {
            var rect = SwatchRect(i);
            bool custom = i == ShellStyle.Presets.Length;
            bool selected = !custom && ShellStyle.Presets[i].Color.ToArgb() == ShellStyle.Color.ToArgb();
            if (custom)
            {
                using var sweep = new LinearGradientBrush(rect, Color.White, Color.White, 45f)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = [Color.FromArgb(70, 130, 245), Color.FromArgb(60, 215, 200), Color.FromArgb(160, 225, 95), Color.FromArgb(232, 150, 225)],
                        Positions = [0, .36f, .66f, 1]
                    }
                };
                g.FillEllipse(sweep, rect);
                using var plus = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
                g.DrawLine(plus, cx - 4, cy, cx + 4, cy); g.DrawLine(plus, cx, cy - 4, cx, cy + 4);
            }
            else
            {
                using var fill = new SolidBrush(ShellStyle.Presets[i].Color);
                g.FillEllipse(fill, rect);
                using var edge = new Pen(Color.FromArgb(70, ink), 1);
                g.DrawEllipse(edge, rect);
            }
            if (selected || _hover == i)
            {
                using var ring = new Pen(selected ? ShellStyle.Accent : Color.FromArgb(120, ink), 1.6f);
                g.DrawEllipse(ring, RectangleF.Inflate(rect, 3, 3));
            }
        }
    }
}

// iPhone unlock sheet: record passcode taps once, then unlock with one click.
sealed class UnlockPopup(Form shell) : GlassPopup(shell)
{
    public enum Command { Unlock, Record, Save, Cancel, Forget }
    public const int ContentWidth = 248;
    const int Edge = 14, HeaderH = 64, RowH = 38;
    int _hover = -1;
    public bool Recording { get; set; }
    public bool HasCode { get; set; }
    public bool Busy { get; set; }
    public int TapCount { get; set; }
    public string Status { get; set; } = "";
    public event Action<Command>? Action;

    (string Label, Command Command, bool Primary)[] Rows => Recording
        ? [($"Save {TapCount} tap{(TapCount == 1 ? "" : "s")}", Command.Save, true), ("Cancel", Command.Cancel, false)]
        : HasCode
            ? [("Unlock iPhone", Command.Unlock, true), ("Record again", Command.Record, false), ("Remove saved taps", Command.Forget, false)]
            : [("Record passcode taps", Command.Record, true)];

    protected override Size ContentSize => new(ContentWidth, HeaderH + Rows.Length * RowH + 10);
    public void Redraw() { SuspendAutoDismiss = Recording || Busy; Repaint(); }

    int RowAt(Point p)
    {
        var c = ToContent(p);
        if (c.X < Edge - 4 || c.X > ContentWidth - Edge + 4 || c.Y < HeaderH) return -1;
        int row = (c.Y - HeaderH) / RowH;
        return row < Rows.Length ? row : -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Location);
        Cursor = row < 0 || Busy ? Cursors.Default : Cursors.Hand;
        if (row != _hover) { _hover = row; Repaint(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Repaint(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int row = RowAt(e.Location);
        if (e.Button != MouseButtons.Left || row < 0 || Busy) return;
        var command = Rows[row].Command;
        if (command == Command.Save && TapCount == 0) return;
        Action?.Invoke(command);
    }

    protected override void PaintContent(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ink = ShellStyle.Ink;
        using (var title = Theme.Font(9.5f, FontStyle.Bold))
        using (var brush = new SolidBrush(ink))
            g.DrawString(Recording ? "Recording passcode" : "iPhone unlock", title, brush, Edge - 2, 12);
        string hint = Status.Length > 0 ? Status
            : Recording ? "Tap your passcode on the phone screen."
            : HasCode ? "Saved taps are encrypted for this Windows account." : "Lock the iPhone, press Record, then tap your passcode once.";
        using (var small = Theme.Font(8f))
        using (var muted = new SolidBrush(ShellStyle.Muted))
            g.DrawString(hint, small, muted, new RectangleF(Edge - 2, 31, size.Width - Edge * 2 + 4, 32));
        var rows = Rows;
        for (int i = 0; i < rows.Length; i++)
        {
            var rect = new RectangleF(Edge - 6, HeaderH + i * RowH + 2, size.Width - (Edge - 6) * 2, RowH - 4);
            bool enabled = !Busy && !(rows[i].Command == Command.Save && TapCount == 0);
            using var shape = ShellRenderer.Round(rect, 11);
            if (rows[i].Primary && enabled)
            {
                using var fill = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), Color.FromArgb(66, 140, 255), Color.FromArgb(168, 112, 244), 0f);
                g.FillPath(fill, shape);
                if (_hover == i) { using var lift = new SolidBrush(Color.FromArgb(40, Color.White)); g.FillPath(lift, shape); }
            }
            else if (_hover == i && enabled)
            {
                using var fill = new SolidBrush(Color.FromArgb(28, ink));
                g.FillPath(fill, shape);
            }
            var color = rows[i].Primary && enabled ? Color.White : rows[i].Command == Command.Forget ? Color.FromArgb(enabled ? 235 : 110, 240, 96, 110) : Color.FromArgb(enabled ? 235 : 110, ink);
            using var font = Theme.Font(9f, rows[i].Primary ? FontStyle.Bold : FontStyle.Regular);
            using var brush = new SolidBrush(color);
            using var format = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
            g.DrawString(Busy && rows[i].Primary ? "Working..." : rows[i].Label, font, brush, rect, format);
        }
    }
}

// PC-side text field for iPhone typing (optional). Shows what will be sent,
// with a caret and selection; editing keys are handled by MainForm.TypeBox.
sealed class TypeBoxPopup(Form shell) : GlassPopup(shell)
{
    public int Width_ { get; set; } = 300;
    public string Text_ { get; set; } = "";
    public int Caret { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionEnd { get; set; }
    protected override Size ContentSize => new(Math.Max(160, Width_), 50);
    public void Redraw() { SuspendAutoDismiss = true; Repaint(); }

    // Newlines are shown as a return symbol on the single line.
    static string ShowNewlines(string text) => text.Replace('\n', '⏎');

    protected override void PaintContent(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ink = ShellStyle.Ink;
        var field = new RectangleF(8, 8, size.Width - 16, 22);
        using var font = Theme.Font(10f);
        using var brush = new SolidBrush(ink);
        using var measure = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };
        string text = ShowNewlines(Text_);
        float At(int index) => index <= 0 ? 0 : g.MeasureString(text[..Math.Min(index, text.Length)], font, 100000, measure).Width;
        float caretOffset = At(Caret);
        // Scroll so the caret stays inside the field.
        float x = field.X + 2;
        if (caretOffset > field.Width - 8) x = field.Right - 6 - caretOffset;
        var state = g.Save();
        g.SetClip(field);
        if (SelectionEnd > SelectionStart)
        {
            using var sel = new SolidBrush(Color.FromArgb(110, ShellStyle.Accent));
            float a = x + At(SelectionStart), b = x + At(SelectionEnd);
            g.FillRectangle(sel, a, field.Y + 3, b - a, field.Height - 6);
        }
        using (var format = new StringFormat(measure) { LineAlignment = StringAlignment.Center })
            g.DrawString(text, font, brush, new RectangleF(x, field.Y, 100000, field.Height), format);
        if ((int)(GlassPresentation.Seconds * 2) % 2 == 0 || SelectionEnd > SelectionStart)
        {
            using var caret = new Pen(ShellStyle.Accent, 1.5f);
            float cx = x + caretOffset + .5f;
            g.DrawLine(caret, cx, field.Y + 4, cx, field.Bottom - 4);
        }
        g.Restore(state);
        using var small = Theme.Font(7.5f);
        using var muted = new SolidBrush(ShellStyle.Muted);
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString("Enter to send  ·  Shift+Enter new line  ·  Esc to cancel", small, muted, new RectangleF(8, 32, size.Width - 16, 14), right);
    }
}
