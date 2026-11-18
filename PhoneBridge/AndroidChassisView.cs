using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PhoneBridge;

/// <summary>
/// Clean Android screen viewport inside the phone frame.
/// When wailink-render.exe is streaming, the live phone video fills this panel.
/// </summary>
public sealed class AndroidChassisView : Panel
{
    bool _connected;
    string _deviceName = "Android";
    string _statusText = "Plug in your phone with a USB cable";
    readonly UiAnimationClock _animation;

    public bool IsConnected
    {
        get => _connected;
        set { _connected = value; Invalidate(); }
    }

    public string DeviceName
    {
        get => _deviceName;
        set { _deviceName = value; Invalidate(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; Invalidate(); }
    }

    public AndroidChassisView()
    {
        _animation = new UiAnimationClock(this);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(9, 10, 14);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _animation.Tick += (_, _) => { if (Visible && GlassPresentation.MotionEnabled) Invalidate(); };
        VisibleChanged += (_, _) => { if (Visible) _animation.Start(); else _animation.Stop(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width < 40 || rect.Height < 40) return;

        GlassPresentation.PaintBackdrop(g, ClientRectangle, Point.Empty);
        GlassPresentation.Loader(g, ClientSize, false, GlassPresentation.Seconds, _statusText);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
        base.Dispose(disposing);
    }
}
