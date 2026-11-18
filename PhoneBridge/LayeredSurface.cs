using System.Runtime.InteropServices;

namespace PhoneBridge;

// A per-pixel-alpha, non-activating top-level surface. Windows 10 cannot
// antialias a window region or round a window in the compositor, so every
// curved or translucent piece of the shell is presented through one of these.
class LayeredSurface : Form
{
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(
        nint window, nint screen, ref NativePoint position, ref NativeSize size,
        nint source, ref NativePoint sourcePosition, uint colorKey, ref Blend blend, uint flags);

    readonly bool _clickThrough;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x80000 | 0x80 | 0x08000000 | (_clickThrough ? 0x20 : 0); // LAYERED | TOOLWINDOW | NOACTIVATE | TRANSPARENT
            return p;
        }
    }

    public LayeredSurface(bool clickThrough = false)
    {
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    public void Present(Bitmap surface, Point location, byte alpha = 255)
    {
        if (IsDisposed) return;
        nint screen = GetDC(0), source = CreateCompatibleDC(screen);
        nint bitmap = surface.GetHbitmap(Color.FromArgb(0));
        nint previous = SelectObject(source, bitmap);
        try
        {
            var point = new NativePoint { X = location.X, Y = location.Y };
            var size = new NativeSize { Width = surface.Width, Height = surface.Height };
            var zero = new NativePoint();
            var blend = new Blend { Alpha = alpha, Format = 1 };
            UpdateLayeredWindow(Handle, screen, ref point, ref size, source, ref zero, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(source, previous);
            DeleteObject(bitmap); DeleteDC(source); ReleaseDC(0, screen);
        }
    }

    public void MoveTo(Point location)
    {
        if (!IsDisposed && IsHandleCreated) SetWindowPos(Handle, 0, location.X, location.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }
}

// The interactive chassis surface. It owns no state: pointer input is handed
// to the main form in that form's client coordinates.
sealed class ShellFrameWindow(MainForm shell) : LayeredSurface
{
    static Point ToShell(Point p) => new((int)Math.Floor((p.X - ShellRenderer.PadPx) / ShellRenderer.Scale), (int)Math.Floor((p.Y - ShellRenderer.PadPx) / ShellRenderer.Scale));
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); shell.ChromeMouseMove(ToShell(e.Location)); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); shell.ChromeMouseLeave(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); shell.ChromeMouseDown(ToShell(e.Location), e.Button); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); shell.ChromeMouseUp(ToShell(e.Location), e.Button); }
}
