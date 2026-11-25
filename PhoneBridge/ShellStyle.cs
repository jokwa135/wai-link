using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PhoneBridge;

// User-adjustable shell material: one base color and one opacity, persisted
// beside the executable. Everything the chrome paints derives from these two.
static class ShellStyle
{
    public const float MinOpacity = .30f;
    public static Color Color { get; private set; } = Color.FromArgb(5, 6, 8);
    public static float Opacity { get; private set; } = .88f;
    // Optional: collect iPhone typing in a PC-side box and send it on Enter.
    public static bool TypeBox { get; private set; }
    // Play the iPhone's sound on the PC over Bluetooth while the iPhone view is open.
    public static bool PhoneSound { get; private set; } = true;
    // Opt-in: when an Android phone connects, apply speed settings to it (animations off, fewer cached
    // apps, lower brightness, auto-sync off...). They stay on the phone; off by default.
    public static bool AndroidTweaks { get; private set; }
    public static event Action? Changed;

    public static readonly (string Name, Color Color)[] Presets =
    [
        ("Graphite", Color.FromArgb(5, 6, 8)),
        ("Midnight", Color.FromArgb(10, 18, 38)),
        ("Violet", Color.FromArgb(28, 16, 48)),
        ("Forest", Color.FromArgb(8, 30, 26)),
        ("Wine", Color.FromArgb(44, 12, 22)),
        ("Titanium", Color.FromArgb(124, 128, 136)),
        ("Frost", Color.FromArgb(233, 240, 250)),
    ];

    static string FilePath => AppPaths.DataFile("shell-style.txt");

    public static bool IsLight => (Color.R * .299 + Color.G * .587 + Color.B * .114) > 150;
    public static Color Ink => IsLight ? Color.FromArgb(24, 33, 48) : Color.FromArgb(244, 246, 250);
    public static Color Muted => Color.FromArgb(150, Ink);
    public static Color Accent => Color.FromArgb(78, 160, 255);
    public static Color Fill => Color.FromArgb((int)Math.Round(255 * Opacity), Color);

    public static void Set(Color color, float opacity)
    {
        color = Color.FromArgb(255, color);
        opacity = Math.Clamp(opacity, MinOpacity, 1f);
        if (color.ToArgb() == Color.ToArgb() && Math.Abs(opacity - Opacity) < .001f) return;
        Color = color; Opacity = opacity;
        Changed?.Invoke();
    }

    public static void SetTypeBox(bool enabled)
    {
        if (TypeBox == enabled) return;
        TypeBox = enabled;
        Changed?.Invoke();
    }

    public static void SetPhoneSound(bool enabled)
    {
        if (PhoneSound == enabled) return;
        PhoneSound = enabled;
        Changed?.Invoke();
    }

    public static void SetAndroidTweaks(bool enabled)
    {
        if (AndroidTweaks == enabled) return;
        AndroidTweaks = enabled;
        Changed?.Invoke();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var parts = File.ReadAllText(FilePath).Trim().Split(';');
            if (parts.Length < 2) return;
            int rgb = int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            float opacity = float.Parse(parts[1], CultureInfo.InvariantCulture);
            Color = Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            Opacity = Math.Clamp(opacity, MinOpacity, 1f);
            TypeBox = parts.Length > 2 && parts[2] == "typebox";
            PhoneSound = parts.Length <= 3 || parts[3] != "nosound";
            AndroidTweaks = parts.Length > 4 && parts[4] == "tweaks";
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, string.Create(CultureInfo.InvariantCulture,
                $"{Color.R:X2}{Color.G:X2}{Color.B:X2};{Opacity:0.00};{(TypeBox ? "typebox" : "live")};{(PhoneSound ? "sound" : "nosound")};{(AndroidTweaks ? "tweaks" : "notweaks")}"));
        }
        catch { }
    }
}

// Pure rendering of the shell material. Static layers are drawn at 2x and
// resampled so every curve is smooth; nothing here depends on window state.
static class ShellRenderer
{
    public const int Pad = 6;          // frame-window margin for protruding hardware keys
    public const int ShadowPad = 56;   // shadow-window margin
    public const float OuterRadius = 26, StageRadius = 18;
    // Device scale (monitor DPI / 96). All geometry above is in logical units.
    public static float Scale { get; set; } = 1;
    public static int Px(float logical) => (int)Math.Round(logical * Scale);
    public static int PadPx => (int)Math.Ceiling(Pad * Scale);
    public static int ShadowPadPx => (int)Math.Ceiling(ShadowPad * Scale);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Glass slab: tinted fill, diagonal sheen, top light and a two-line rim.
    public static void Panel(Graphics g, RectangleF rect, float radius, float opacity)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        bool light = ShellStyle.IsLight;
        using (var edge = Round(RectangleF.Inflate(rect, .5f, .5f), radius + .5f))
        using (var pen = new Pen(Color.FromArgb(light ? 46 : 96, 0, 0, 0), 1)) g.DrawPath(pen, edge);
        using var body = Round(rect, radius);
        using (var fill = new SolidBrush(Color.FromArgb((int)Math.Round(255 * Math.Clamp(opacity, 0, 1)), ShellStyle.Color)))
            g.FillPath(fill, body);
        var state = g.Save();
        g.SetClip(body, CombineMode.Intersect);
        using (var sheen = new LinearGradientBrush(new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Bottom), Color.White, Color.White))
        {
            sheen.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(light ? 70 : 24, Color.White), Color.FromArgb(0, Color.White), Color.FromArgb(light ? 20 : 7, Color.White)],
                Positions = [0, .48f, 1]
            };
            g.FillRectangle(sheen, rect);
        }
        float glow = Math.Min(72, rect.Height * .5f);
        using (var top = new LinearGradientBrush(new PointF(0, rect.Top - 1), new PointF(0, rect.Top + glow),
            Color.FromArgb(light ? 60 : 18, Color.White), Color.FromArgb(0, Color.White)))
            g.FillRectangle(top, rect.Left, rect.Top, rect.Width, glow);
        g.Restore(state);
        using (var rimPath = Round(RectangleF.Inflate(rect, -.5f, -.5f), radius - .5f))
        using (var rim = new LinearGradientBrush(new PointF(0, rect.Top - 1), new PointF(0, rect.Bottom + 1), Color.White, Color.White))
        {
            rim.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(light ? 235 : 96, Color.White), Color.FromArgb(light ? 120 : 34, Color.White), Color.FromArgb(light ? 150 : 50, Color.White)],
                Positions = [0, .5f, 1]
            };
            using var pen = new Pen(rim, 1);
            g.DrawPath(pen, rimPath);
        }
        using (var inner = Round(RectangleF.Inflate(rect, -1.5f, -1.5f), radius - 1.5f))
        using (var pen = new Pen(Color.FromArgb(light ? 40 : 12, Color.White), 1)) g.DrawPath(pen, inner);
    }

    static Bitmap Downsample(Bitmap big, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(big, new Rectangle(0, 0, width, height), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }

    // The chassis without controls: glass body with the screen cut out and a
    // thin opaque bezel that hides the viewport window's binary region edge.
    public static Bitmap Body(Size size, Rectangle stage, bool fast = false)
    {
        int scale = fast ? 1 : 2;
        int width = size.Width + PadPx * 2, height = size.Height + PadPx * 2;
        var canvas = new Bitmap(width * scale, height * scale, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            if (scale > 1) g.ScaleTransform(scale, scale);
            g.TranslateTransform(PadPx, PadPx);
            g.ScaleTransform(Scale, Scale);
            Panel(g, new RectangleF(0, 0, size.Width / Scale, size.Height / Scale), OuterRadius, ShellStyle.Opacity);
            var screen = new RectangleF(stage.X / Scale, stage.Y / Scale, stage.Width / Scale, stage.Height / Scale);
            using (var hole = Round(screen, StageRadius))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.None;
                using var clear = new SolidBrush(Color.Transparent);
                g.FillPath(clear, hole);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            using (var bezel = Round(RectangleF.Inflate(screen, .5f, .5f), StageRadius + .5f))
            using (var pen = new Pen(Color.FromArgb(255, 2, 3, 4), 2)) g.DrawPath(pen, bezel);
            using (var lip = Round(RectangleF.Inflate(screen, 2f, 2f), StageRadius + 2f))
            using (var pen = new Pen(Color.FromArgb(ShellStyle.IsLight ? 90 : 26, Color.White), .75f)) g.DrawPath(pen, lip);
        }
        if (fast) return canvas;
        using (canvas) return Downsample(canvas, width, height);
    }

    // Two-layer soft shadow (tight contact + wide ambient) with the body cut out.
    public static Bitmap Shadow(Size size, float radius, int pad, float strength = 1)
    {
        int width = size.Width + pad * 2, height = size.Height + pad * 2;
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        foreach (var (blur, dy, alpha) in new[] { (pad * .30f, pad * .22f, .34f), (pad * .09f, pad * .05f, .26f) })
        {
            using var layer = BlurredShape(size, radius, pad, blur, alpha * strength);
            g.DrawImage(layer, new RectangleF(0, dy, width, height));
        }
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.None;
        using var cut = Round(new RectangleF(pad + 1, pad + 1, size.Width - 2, size.Height - 2), radius * Scale - 1);
        using var clear = new SolidBrush(Color.Transparent);
        g.FillPath(clear, cut);
        return result;
    }

    static Bitmap BlurredShape(Size size, float radius, int pad, float blur, float alpha)
    {
        const float scale = .5f;
        int w = Math.Max(4, (int)Math.Ceiling((size.Width + pad * 2) * scale));
        int h = Math.Max(4, (int)Math.Ceiling((size.Height + pad * 2) * scale));
        var mask = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(mask))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(scale, scale);
            using var shape = Round(new RectangleF(pad, pad, size.Width, size.Height), radius * Scale);
            g.FillPath(Brushes.White, shape);
        }
        var data = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            byte[] pixels = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var a = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y * w + x] = pixels[y * data.Stride + x * 4 + 3];
            int box = Math.Max(1, (int)Math.Round(blur * scale * .6f));
            var scratch = new float[w * h];
            for (int pass = 0; pass < 3; pass++) { BoxBlur(a, scratch, w, h, box, true); BoxBlur(scratch, a, w, h, box, false); }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * data.Stride + x * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                pixels[i + 3] = (byte)Math.Clamp(a[y * w + x] * alpha, 0, 255);
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { mask.UnlockBits(data); }
        return mask;
    }

    static void BoxBlur(float[] source, float[] target, int w, int h, int r, bool horizontal)
    {
        int length = horizontal ? w : h, lines = horizontal ? h : w;
        float norm = 1f / (r * 2 + 1);
        for (int line = 0; line < lines; line++)
        {
            float sum = 0;
            for (int k = -r; k <= r; k++) sum += Sample(k);
            for (int i = 0; i < length; i++)
            {
                target[horizontal ? line * w + i : i * w + line] = sum * norm;
                sum += Sample(i + r + 1) - Sample(i - r);
            }
            float Sample(int i) => i < 0 || i >= length ? 0 : source[horizontal ? line * w + i : i * w + line];
        }
    }
}
