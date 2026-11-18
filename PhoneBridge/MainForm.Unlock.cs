using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PhoneBridge;

// iPhone unlock button. The user records their passcode taps once by tapping
// the (hidden) keypad in the mirror; WAI Link stores the tap positions
// encrypted for the current Windows account (DPAPI) and replays them through
// the existing tap path. Uses only existing IphoneController calls.
public sealed partial class MainForm
{
    const int UnlockButtonId = 10;
    static readonly HttpClient s_wda = new() { Timeout = TimeSpan.FromSeconds(3) };
    readonly List<PointF> _unlockTaps = [];
    readonly List<PointF> _recordedTaps = [];
    bool _unlockRecording, _unlockBusy;
    Point _recordDown;
    Rectangle _btnUnlockRect;
    UnlockPopup _unlockPopup = null!;

    static string UnlockFile => AppPaths.DataFile("iphone-unlock.bin");

    void InitUnlock()
    {
        LoadUnlockTaps();
        _unlockPopup = new UnlockPopup(this) { TriggerScreenBounds = () => ShellToScreen(_btnUnlockRect) };
        _unlockPopup.Action += OnUnlockAction;
        _iphonePanel.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _recordDown = e.Location; };
        _iphonePanel.MouseUp += (_, e) =>
        {
            if (!_unlockRecording || e.Button != MouseButtons.Left) return;
            if (Math.Abs(e.X - _recordDown.X) > 4 || Math.Abs(e.Y - _recordDown.Y) > 4) return;
            if (!_iphonePanel.TryMapScreenPoint(e.Location, out var p, out var size, clamp: true) || size.Width <= 0) return;
            _recordedTaps.Add(new PointF(p.X / (float)size.Width, p.Y / (float)size.Height));
            UpdateUnlockPopup();
        };
    }

    void ToggleUnlockPopup()
    {
        _devicePopover.Dismiss();
        _stylePopup.Dismiss();
        UpdateUnlockPopup();
        _unlockPopup.Toggle(new Point(Left + Width - S(UnlockPopup.ContentWidth + 6), Top + TopBarH + S(4)));
    }

    void UpdateUnlockPopup(string? status = null)
    {
        if (_unlockPopup is null) return;
        _unlockPopup.Recording = _unlockRecording;
        _unlockPopup.HasCode = _unlockTaps.Count > 0;
        _unlockPopup.TapCount = _recordedTaps.Count;
        _unlockPopup.Busy = _unlockBusy;
        if (status is not null) _unlockPopup.Status = status;
        _unlockPopup.Redraw();
    }

    async void OnUnlockAction(UnlockPopup.Command command)
    {
        switch (command)
        {
            case UnlockPopup.Command.Unlock:
                await UnlockIphoneAsync();
                break;
            case UnlockPopup.Command.Record:
                await StartUnlockRecordingAsync();
                break;
            case UnlockPopup.Command.Save:
                if (_recordedTaps.Count == 0) return;
                _unlockTaps.Clear(); _unlockTaps.AddRange(_recordedTaps);
                _recordedTaps.Clear(); _unlockRecording = false;
                SaveUnlockTaps();
                UpdateUnlockPopup("Saved. Lock the phone and press Unlock to test.");
                break;
            case UnlockPopup.Command.Cancel:
                _recordedTaps.Clear(); _unlockRecording = false;
                UpdateUnlockPopup("Recording cancelled.");
                break;
            case UnlockPopup.Command.Forget:
                _unlockTaps.Clear();
                try { if (File.Exists(UnlockFile)) File.Delete(UnlockFile); } catch { }
                UpdateUnlockPopup("Removed.");
                break;
        }
        Invalidate();
    }

    async Task<bool?> IsIphoneLockedAsync()
    {
        string host = _iphoneController.MjpegHost;
        if (string.IsNullOrWhiteSpace(host)) return null;
        try
        {
            using var doc = JsonDocument.Parse(await s_wda.GetStringAsync($"http://{host}:8100/wda/locked"));
            return doc.RootElement.GetProperty("value").GetBoolean();
        }
        catch { return null; }
    }

    Size IphoneScreenSize()
    {
        _iphonePanel.TryMapScreenPoint(new Point(_iphonePanel.Width / 2, _iphonePanel.Height / 2), out _, out var size, clamp: true);
        return size;
    }

    // Wakes the phone and brings up the passcode keypad.
    async Task<bool> ShowKeypadAsync()
    {
        await _iphoneController.SendHomeAsync();
        await Task.Delay(700);
        if (await IsIphoneLockedAsync() == false) return false; // Face ID got there first
        var size = IphoneScreenSize();
        if (size.Width <= 0) return false;
        int cx = size.Width / 2;
        await _iphoneController.SendSwipeAsync(cx, (int)(size.Height * .97), cx, (int)(size.Height * .40), size, .30);
        await Task.Delay(750);
        return true;
    }

    async Task StartUnlockRecordingAsync()
    {
        if (_viewMode != StudioViewMode.IphoneOnly || !_iphoneController.IsWdaConnected) { UpdateUnlockPopup("Connect the iPhone first."); return; }
        var locked = await IsIphoneLockedAsync();
        if (locked != true) { UpdateUnlockPopup(locked == false ? "Lock the iPhone first, then press Record." : "Can't read the lock state."); return; }
        _unlockBusy = true; UpdateUnlockPopup("Opening the keypad...");
        bool keypad = await ShowKeypadAsync();
        _unlockBusy = false;
        if (!keypad) { UpdateUnlockPopup("The phone unlocked itself. Lock it and try again."); return; }
        _recordedTaps.Clear();
        _unlockRecording = true;
        UpdateUnlockPopup("Tap your passcode on the phone screen. The keypad is hidden in the mirror, but taps land.");
    }

    async Task UnlockIphoneAsync()
    {
        if (_unlockBusy) return;
        if (_unlockTaps.Count == 0) { UpdateUnlockPopup("Record your passcode taps first."); return; }
        if (_viewMode != StudioViewMode.IphoneOnly || !_iphoneController.IsWdaConnected) { UpdateUnlockPopup("Connect the iPhone first."); return; }
        _unlockBusy = true; UpdateUnlockPopup("Unlocking...");
        try
        {
            var locked = await IsIphoneLockedAsync();
            if (locked == false) { UpdateUnlockPopup("Already unlocked."); return; }
            if (locked is null) { UpdateUnlockPopup("Can't read the lock state."); _glyphError[UnlockButtonId] = GlassPresentation.Seconds; return; }
            if (!await ShowKeypadAsync()) { UpdateUnlockPopup("Unlocked."); return; }
            var size = IphoneScreenSize();
            foreach (var tap in _unlockTaps)
            {
                await _iphoneController.SendTapAsync((int)Math.Round(tap.X * size.Width), (int)Math.Round(tap.Y * size.Height), size);
                await Task.Delay(110);
            }
            await Task.Delay(900);
            bool done = await IsIphoneLockedAsync() == false;
            if (!done) _glyphError[UnlockButtonId] = GlassPresentation.Seconds;
            UpdateUnlockPopup(done ? "Unlocked." : "Still locked. Try recording again.");
            if (done) _unlockPopup.Dismiss();
        }
        finally { _unlockBusy = false; UpdateUnlockPopup(); Invalidate(); }
    }

    void LoadUnlockTaps()
    {
        try
        {
            if (!File.Exists(UnlockFile)) return;
            string text = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(UnlockFile)));
            foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(',');
                _unlockTaps.Add(new PointF(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)));
            }
        }
        catch { _unlockTaps.Clear(); }
    }

    void SaveUnlockTaps()
    {
        string text = string.Join(';', _unlockTaps.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.0000},{p.Y:0.0000}")));
        try { File.WriteAllBytes(UnlockFile, Dpapi.Protect(Encoding.UTF8.GetBytes(text))); }
        catch { UpdateUnlockPopup("Couldn't save."); }
    }
}

// Windows DPAPI, current-user scope: only this Windows account can decrypt.
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);

    public static byte[] Protect(byte[] data) => Run(data, true);
    public static byte[] Unprotect(byte[] data) => Run(data, false);

    static byte[] Run(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect
                ? CryptProtectData(ref input, "WAI Link", 0, 0, 0, 1, ref output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0) LocalFree(output.Data);
        }
    }
}
