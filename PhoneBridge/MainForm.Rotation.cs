namespace PhoneBridge;

// Portrait / landscape for both phones. Only the top-bar rotate button changes orientation: it turns
// the phone's interface (WebDriverAgent on iPhone, the rotation setting over adb on Android) and
// swaps the shell's width and height. While Android is shown, its auto-rotate is off so the phone
// cannot turn by itself; the user's own setting comes back when the Android view closes.
public sealed partial class MainForm
{
    const int RotateButtonId = 11;
    Rectangle _btnRotateRect;
    bool _rotationBusy;
    bool _landscape;
    Size _portraitClientSize;
    string? _androidAutoRotate; // the phone's accelerometer_rotation before WAI Link locked it

    void InitRotation() { }

    async Task ToggleLandscapeAsync()
    {
        if (_viewMode == StudioViewMode.None || _rotationBusy) return;
        _rotationBusy = true;
        try
        {
            bool toLandscape = !_landscape;
            bool ok = _viewMode == StudioViewMode.IphoneOnly
                ? await _iphoneController.SetOrientationAsync(toLandscape ? "LANDSCAPE" : "PORTRAIT")
                : await SetAndroidRotationAsync(toLandscape);
            // The shell turns even if the phone did not: the picture is then turned in the view.
            ApplyLandscape(toLandscape);
            if (!ok) _glyphError[RotateButtonId] = GlassPresentation.Seconds;
            Invalidate();
        }
        finally { _rotationBusy = false; }
    }

    // Android: auto-rotate off, then the requested fixed rotation (0 portrait, 1 landscape).
    async Task<bool> SetAndroidRotationAsync(bool landscape)
    {
        if (_adb.Serial is null) return false;
        try
        {
            if (_androidAutoRotate is null)
            {
                string current = (await _adb.ShellAsync("settings get system accelerometer_rotation")).Trim();
                _androidAutoRotate = current is "0" or "1" ? current : "1";
            }
            await _adb.ShellAsync($"settings put system accelerometer_rotation 0; settings put system user_rotation {(landscape ? 1 : 0)}");
            return true;
        }
        catch { return false; }
    }

    // Gives the phone back its own auto-rotate setting (leaving the Android view or closing).
    void RestoreAndroidRotation(bool wait = false)
    {
        string? previous = _androidAutoRotate;
        _androidAutoRotate = null;
        if (previous is null || _adb.Serial is null) return;
        var task = _adb.ShellAsync($"settings put system user_rotation 0; settings put system accelerometer_rotation {previous}");
        if (wait) { try { task.Wait(1500); } catch { } }
    }

    // Swaps the shell's width and height around its centre; the iPhone view turns portrait frames.
    void ApplyLandscape(bool landscape)
    {
        _iphonePanel.Rotation = landscape ? 270 : 0;
        if (_landscape == landscape) { LayoutViewport(); Invalidate(); return; }
        _landscape = landscape;
        if (WindowState == FormWindowState.Normal)
        {
            var center = new Point(Left + Width / 2, Top + Height / 2);
            var stage = _stage.Size;
            if (landscape) _portraitClientSize = ClientSize;
            // Coming back to portrait restores the exact earlier size, so repeated turns never drift.
            var size = !landscape && _portraitClientSize.Width > 0
                ? _portraitClientSize
                : new Size(stage.Height + SidePad * 2, stage.Width + TopBarH + BottomBarH);
            var area = Screen.FromPoint(center).WorkingArea;
            if (size.Width > area.Width || size.Height > area.Height)
            {
                double scale = Math.Min(area.Width / (double)size.Width, area.Height / (double)size.Height);
                size = new Size((int)(size.Width * scale), (int)(size.Height * scale));
            }
            MinimumSize = new Size(S(120), S(120));
            ClientSize = size;
            Location = new Point(Math.Clamp(center.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(center.Y - Height / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            FitShellToScreen();
        }
        LayoutViewport();
        Invalidate();
    }
}
