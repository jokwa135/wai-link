using System.Drawing.Drawing2D;

namespace PhoneBridge;

/// <summary>
/// Operate-mode dark design system and custom vector/GDI+ controls for WAI Link.
/// Follows impeccable (operate.md + craft-floor.md), ui-design-systems, and ui-ux-pro-max:
/// - No pure #000000; stratified dark charcoal surfaces (#0D0E11, #15171C, #1C1F26)
/// - Concentric border radii (10px outer containers, 6px inner interactive pills)
/// - Drawn vector geometry (no emoji surrogates)
/// - Loading/busy states on async buttons, tabular numerals for live telemetry, color+text state pairing
/// </summary>
static class Theme
{
    // Neutral Dark Zinc Design System Tokens (@shadcn/ui + WinUI 3)
    public static readonly Color Canvas = Color.FromArgb(9, 9, 11);              // #09090B Zinc 950
    public static readonly Color Surface = Color.FromArgb(18, 18, 21);           // #121215 Neutral Dark
    public static readonly Color SurfaceElevated = Color.FromArgb(28, 28, 32);   // #1C1C20 Zinc 900
    public static readonly Color ControlHover = Color.FromArgb(39, 39, 42);      // #27272A Zinc 800
    public static readonly Color ControlActive = Color.FromArgb(63, 63, 70);     // #3F3F46 Zinc 700
    public static readonly Color Border = Color.FromArgb(39, 39, 42);            // #27272A Hairline
    public static readonly Color SpecularLine = Color.FromArgb(63, 63, 70);      // #3F3F46 Subtle Highlight

    public static readonly Color TextPrimary = Color.FromArgb(244, 244, 245);    // #F4F4F5 Zinc 100
    public static readonly Color TextSecondary = Color.FromArgb(161, 161, 170);  // #A1A1AA Zinc 400
    public static readonly Color TextMuted = Color.FromArgb(113, 113, 122);      // #71717A Zinc 500

    // Semantic iOS State Tokens
    public static readonly Color Emerald = Color.FromArgb(48, 209, 88);         // #30D158 iOS Green
    public static readonly Color EmeraldSoft = Color.FromArgb(40, 48, 209, 88); // Frosted Green Glass
    public static readonly Color Cobalt = Color.FromArgb(10, 132, 255);         // #0A84FF iOS Electric Blue
    public static readonly Color CobaltSoft = Color.FromArgb(45, 10, 132, 255); // Frosted Blue Glass
    public static readonly Color Amber = Color.FromArgb(255, 159, 10);          // #FF9F0A iOS Amber/Gold
    public static readonly Color AmberSoft = Color.FromArgb(45, 255, 159, 10);  // Frosted Gold Glass
    public static readonly Color Sky = Color.FromArgb(56, 189, 248);            // #38BDF8 Cyan Glass Glow
    public static readonly Color SkySoft = Color.FromArgb(45, 56, 189, 248);    // Frosted Cyan Glass
    public static readonly Color Rose = Color.FromArgb(255, 55, 95);            // #FF375F iOS Neon Pink
    public static readonly Color Purple = Color.FromArgb(191, 90, 242);         // #BF5AF2 iOS Neon Violet

    public static readonly string FontFamilyName = ResolveCleanFont();

    static string ResolveCleanFont()
    {
        // Apple SF Pro Display (installed authentic Apple typography) or clean modern sans-serif immune to GoldmanUI
        foreach (var name in new[] { "SF Pro Display", "SF Pro", "Aptos", "Arial", "Tahoma" })
        {
            try
            {
                using var test = new Font(name, 9f);
                if (string.Equals(test.Name, name, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            catch { }
        }
        return "Arial";
    }

    public static readonly Font FontTitle = new(FontFamilyName, 10.5f, FontStyle.Bold);
    public static readonly Font FontBody = new(FontFamilyName, 9f, FontStyle.Regular);
    public static readonly Font FontBodyBold = new(FontFamilyName, 9f, FontStyle.Bold);
    public static readonly Font FontSmall = new(FontFamilyName, 8f, FontStyle.Regular);
    public static readonly Font FontSmallBold = new(FontFamilyName, 8f, FontStyle.Bold);
    public static readonly Font FontMono = CreateMonoFont(8.5f, FontStyle.Regular);
    public static readonly Font FontMonoBold = CreateMonoFont(9f, FontStyle.Bold);
    public static readonly Font FontIcon = new("Segoe MDL2 Assets", 11.5f);

    public static Font Font(float size, FontStyle style = FontStyle.Regular) =>
        new(FontFamilyName, size, style);

    public static Font CreateFont(string family, float size, FontStyle style = FontStyle.Regular)
    {
        try
        {
            var f = new Font(family, size, style);
            if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        catch { }
        return new Font(FontFamilyName, size, style);
    }

    public static readonly Font FontIosClock = CreateFont("SF Pro Display", 52f, FontStyle.Bold);
    public static readonly Font FontIosDate = CreateFont("SF Pro Medium", 11.5f, FontStyle.Regular);
    public static readonly Font FontIosStatus = CreateFont("SF Pro Semibold", 8.5f, FontStyle.Regular);
    public static readonly Font FontIosCardTitle = CreateFont("SF Pro Display", 11.5f, FontStyle.Bold);
    public static readonly Font FontIosCardSub = CreateFont("SF Pro", 9f, FontStyle.Regular);
    public static readonly Font FontIosBadge = CreateFont("SF Pro Semibold", 7.5f, FontStyle.Regular);

    static Font CreateMonoFont(float size, FontStyle style)
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            var f = new Font(name, size, style);
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font(FontFamily.GenericMonospace, size, style);
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d >= bounds.Width && d >= bounds.Height)
        {
            path.AddEllipse(bounds);
            return path;
        }
        var arc = new Rectangle(bounds.X, bounds.Y, d, d);
        path.AddArc(arc, 180, 90);
        if (bounds.Width > d)
            path.AddLine(bounds.X + d / 2, bounds.Y, bounds.Right - d / 2, bounds.Y);
        arc.X = bounds.Right - d;
        path.AddArc(arc, 270, 90);
        if (bounds.Height > d)
            path.AddLine(bounds.Right, bounds.Y + d / 2, bounds.Right, bounds.Bottom - d / 2);
        arc.Y = bounds.Bottom - d;
        path.AddArc(arc, 0, 90);
        if (bounds.Width > d)
            path.AddLine(bounds.Right - d / 2, bounds.Bottom, bounds.X + d / 2, bounds.Bottom);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        if (bounds.Height > d)
            path.AddLine(bounds.Left, bounds.Bottom - d / 2, bounds.Left, bounds.Y + d / 2);
        path.CloseFigure();
        return path;
    }

    public static void DrawVectorIcon(Graphics g, VectorIcon icon, Rectangle rect, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;

        switch (icon)
        {
            case VectorIcon.Bolt:
            {
                PointF[] pts =
                [
                    new(cx + 1f, cy - 6.5f),
                    new(cx - 4.5f, cy + 0.5f),
                    new(cx - 0.5f, cy + 0.5f),
                    new(cx - 1.5f, cy + 6.5f),
                    new(cx + 4.5f, cy - 0.5f),
                    new(cx + 0.5f, cy - 0.5f)
                ];
                g.FillPolygon(brush, pts);
                break;
            }
            case VectorIcon.PopOut:
            {
                var box = new RectangleF(cx - 5.5f, cy - 4.5f, 9f, 9f);
                g.DrawRectangle(pen, box.X, box.Y + 1f, box.Width - 1f, box.Height - 1f);
                g.DrawLine(pen, cx - 0.5f, cy - 5.5f, cx + 5.5f, cy - 5.5f);
                g.DrawLine(pen, cx + 5.5f, cy - 5.5f, cx + 5.5f, cy + 0.5f);
                g.DrawLine(pen, cx - 1f, cy + 1f, cx + 5.2f, cy - 5.2f);
                break;
            }
            case VectorIcon.Back:
            {
                g.DrawLine(pen, cx + 3f, cy - 5.5f, cx - 3f, cy);
                g.DrawLine(pen, cx - 3f, cy, cx + 3f, cy + 5.5f);
                break;
            }
            case VectorIcon.Home:
            {
                var r = new Rectangle( (int)(cx - 5), (int)(cy - 5), 10, 10);
                using var p = RoundedRect(r, 3);
                g.DrawPath(pen, p);
                break;
            }
            case VectorIcon.Recents:
            {
                g.DrawRectangle(pen, cx - 5.5f, cy - 4.5f, 7.5f, 7.5f);
                g.DrawLine(pen, cx - 2f, cy + 5f, cx + 5.5f, cy + 5f);
                g.DrawLine(pen, cx + 5.5f, cy - 2f, cx + 5.5f, cy + 5f);
                break;
            }
            case VectorIcon.SoundOn:
            {
                PointF[] spk = [new(cx - 6f, cy - 2.5f), new(cx - 3f, cy - 2.5f), new(cx + 0.5f, cy - 5.5f), new(cx + 0.5f, cy + 5.5f), new(cx - 3f, cy + 2.5f), new(cx - 6f, cy + 2.5f)];
                g.FillPolygon(brush, spk);
                g.DrawArc(pen, cx + 1f, cy - 4f, 4f, 8f, -55, 110);
                g.DrawArc(pen, cx + 2f, cy - 6.5f, 6f, 13f, -55, 110);
                break;
            }
            case VectorIcon.SoundOff:
            {
                PointF[] spk = [new(cx - 6f, cy - 2.5f), new(cx - 3f, cy - 2.5f), new(cx + 0.5f, cy - 5.5f), new(cx + 0.5f, cy + 5.5f), new(cx - 3f, cy + 2.5f), new(cx - 6f, cy + 2.5f)];
                g.FillPolygon(brush, spk);
                g.DrawLine(pen, cx + 2.5f, cy - 3f, cx + 6.5f, cy + 3f);
                g.DrawLine(pen, cx + 6.5f, cy - 3f, cx + 2.5f, cy + 3f);
                break;
            }
            case VectorIcon.Upload:
            {
                g.DrawLine(pen, cx, cy + 4f, cx, cy - 4.5f);
                g.DrawLine(pen, cx - 3.5f, cy - 1.5f, cx, cy - 5f);
                g.DrawLine(pen, cx + 3.5f, cy - 1.5f, cx, cy - 5f);
                g.DrawLine(pen, cx - 5f, cy + 6f, cx + 5f, cy + 6f);
                break;
            }
            case VectorIcon.ChevronDown:
            {
                g.DrawLine(pen, cx - 4f, cy - 1.5f, cx, cy + 2.5f);
                g.DrawLine(pen, cx, cy + 2.5f, cx + 4f, cy - 1.5f);
                break;
            }
            case VectorIcon.Iphone:
            {
                var r = new Rectangle((int)(cx - 4), (int)(cy - 6), 8, 12);
                using var p = RoundedRect(r, 2);
                g.DrawPath(pen, p);
                g.DrawLine(pen, cx - 1.5f, cy - 4.2f, cx + 1.5f, cy - 4.2f);
                break;
            }
            case VectorIcon.Copy:
            {
                g.DrawRectangle(pen, cx - 2.5f, cy - 2.5f, 7f, 7.5f);
                g.DrawLine(pen, cx - 5f, cy + 2.5f, cx - 5f, cy - 5f);
                g.DrawLine(pen, cx - 5f, cy - 5f, cx + 2f, cy - 5f);
                break;
            }
            case VectorIcon.Close:
            {
                g.DrawLine(pen, cx - 4f, cy - 4f, cx + 4f, cy + 4f);
                g.DrawLine(pen, cx + 4f, cy - 4f, cx - 4f, cy + 4f);
                break;
            }
            case VectorIcon.Sidebar:
            {
                var r = new RectangleF(cx - 5.5f, cy - 5.5f, 11f, 11f);
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawLine(pen, cx + 1.5f, cy - 5.5f, cx + 1.5f, cy + 5.5f);
                break;
            }
        }
    }
}

enum VectorIcon
{
    None,
    Bolt,
    PopOut,
    Back,
    Home,
    Recents,
    SoundOn,
    SoundOff,
    Upload,
    ChevronDown,
    Iphone,
    Copy,
    Close,
    Sidebar
}

/// <summary>Double-buffered container panel with subtle 1px border and top specular highlight.</summary>
sealed class SurfacePanel : Panel
{
    public bool DrawTopBorder { get; set; }
    public bool DrawBottomBorder { get; set; }

    public SurfacePanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var borderPen = new Pen(Theme.Border, 1f);
        if (DrawTopBorder)
        {
            e.Graphics.DrawLine(borderPen, 0, 0, Width, 0);
            using var specPen = new Pen(Theme.SpecularLine, 1f);
            e.Graphics.DrawLine(specPen, 12, 1, Width - 12, 1);
        }
        if (DrawBottomBorder)
        {
            e.Graphics.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);
        }
    }
}

/// <summary>
/// Operate-mode interactive pill button with vector icon support, concentric 6px radius,
/// hover/press states, and built-in busy state (per ui-ux-pro-max loading-buttons rule).
/// </summary>
sealed class ActionPill : Control
{
    bool _hover;
    bool _pressed;
    bool _isBusy;
    string _busyText = "Working...";

    public VectorIcon Icon { get; set; } = VectorIcon.None;
    public VectorIcon TrailingIcon { get; set; } = VectorIcon.None;
    public Color? AccentColor { get; set; }
    public Color? SoftFillColor { get; set; }
    public bool Compact { get; set; }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; Enabled = !value; Invalidate(); }
    }

    public string BusyText
    {
        get => _busyText;
        set { _busyText = value; if (_isBusy) Invalidate(); }
    }

    public bool Borderless { get; set; }
    public int CornerRadius { get; set; } = 6;

    public ActionPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = Theme.FontBodyBold;
        ForeColor = Theme.TextPrimary;
        BackColor = Theme.Surface;
        Size = new Size(92, 32);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color clearColor = Parent?.BackColor is Color pCol && pCol != Color.Transparent && pCol.A > 0 ? pCol : Theme.Surface;
        g.Clear(clearColor);

        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = Theme.RoundedRect(rect, CornerRadius);

        Color fill = Borderless ? Color.Transparent : (SoftFillColor ?? Theme.SurfaceElevated);
        if (_pressed) fill = Theme.ControlActive;
        else if (_hover && Enabled) fill = Theme.ControlHover;

        if (fill != Color.Transparent)
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }

        if (Borderless && (_hover || _pressed))
        {
            using var p = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
            g.DrawPath(p, path);
        }
        else if (!Borderless)
        {
            Color borderCol = Focused ? Theme.Cobalt : (AccentColor.HasValue ? Color.FromArgb(95, AccentColor.Value) : Theme.Border);
            using var p = new Pen(borderCol, 1f);
            g.DrawPath(p, path);
        }

        Color fg = !Enabled && !_isBusy ? Theme.TextMuted : (AccentColor ?? ForeColor);
        string label = _isBusy ? _busyText : Text;

        int padX = Compact ? 2 : 8;
        int iconSpace = Icon != VectorIcon.None ? (Compact ? 14 : 18) : 0;
        int trailSpace = TrailingIcon != VectorIcon.None ? 16 : 0;

        if (string.IsNullOrEmpty(label) && Icon != VectorIcon.None)
        {
            Theme.DrawVectorIcon(g, Icon, new Rectangle(Width / 2 - 8, Height / 2 - 8, 16, 16), fg);
            return;
        }

        if (Icon != VectorIcon.None)
        {
            Theme.DrawVectorIcon(g, Icon, new Rectangle(padX, Height / 2 - 8, 14, 16), fg);
        }
        if (TrailingIcon != VectorIcon.None)
        {
            Theme.DrawVectorIcon(g, TrailingIcon, new Rectangle(Width - padX - 12, Height / 2 - 8, 12, 16), Theme.TextSecondary);
        }

        var textRect = new Rectangle(padX + iconSpace, 0, Width - (padX * 2) - iconSpace - trailSpace, Height);
        TextRenderer.DrawText(g, label, Font, textRect, fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | (Icon == VectorIcon.None && TrailingIcon == VectorIcon.None ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// Clean 3-way segmented mode switcher (Android | iPhone | Split).
/// </summary>
public sealed class SegmentedModeControl : Control
{
    StudioViewMode _selected = StudioViewMode.AndroidOnly;
    int _hoverIdx = -1;

    public StudioViewMode SelectedMode
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public event Action<StudioViewMode>? OnModeSelected;

    public SegmentedModeControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Size = new Size(204, 30);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int w = Math.Max(1, Width / 3);
        int idx = Math.Clamp(e.X / w, 0, 2);
        if (idx != _hoverIdx) { _hoverIdx = idx; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIdx = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int w = Math.Max(1, Width / 3);
        int idx = Math.Clamp(e.X / w, 0, 2);
        var next = idx switch
        {
            0 => StudioViewMode.AndroidOnly,
            _ => StudioViewMode.IphoneOnly
        };
        if (next != _selected)
        {
            _selected = next;
            Invalidate();
            OnModeSelected?.Invoke(_selected);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.FromArgb(18, 18, 21));

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var outerPath = Theme.RoundedRect(outer, 6))
        {
            using var bg = new SolidBrush(Color.FromArgb(24, 24, 27));
            g.FillPath(bg, outerPath);
            using var border = new Pen(Color.FromArgb(39, 39, 42), 1f);
            g.DrawPath(border, outerPath);
        }

        string[] labels = ["Android", "iPhone", "Split"];
        int segW = Width / 3;
        int activeIdx = _selected switch
        {
            StudioViewMode.AndroidOnly => 0,
            StudioViewMode.IphoneOnly => 1,
            _ => 2
        };

        for (int i = 0; i < 3; i++)
        {
            var segRect = new Rectangle(i * segW + 2, 2, segW - 4, Height - 4);
            if (i == activeIdx)
            {
                using var activePath = Theme.RoundedRect(segRect, 5);
                using var activeFill = new SolidBrush(Color.FromArgb(39, 39, 42));
                g.FillPath(activeFill, activePath);
            }
            else if (i == _hoverIdx)
            {
                using var hoverPath = Theme.RoundedRect(segRect, 5);
                using var hoverFill = new SolidBrush(Color.FromArgb(30, 30, 34));
                g.FillPath(hoverFill, hoverPath);
            }

            Color fg = i == activeIdx ? Color.FromArgb(250, 250, 250) : Color.FromArgb(161, 161, 170);
            TextRenderer.DrawText(g, labels[i], Theme.FontSmallBold, segRect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
