using System.Text;

namespace PhoneBridge;

public sealed partial class MainForm
{
    readonly StringBuilder _iphoneText = new();
    Task _iphoneKeyboardTail = Task.CompletedTask;
    bool _iphoneTextFlushPosted;
    bool _iphonePointerDown, _iphoneHidContact;
    Point _iphoneLastPoint;

    const int IphoneHoldDelayMs = 300;
    bool _iphoneHolding;
    int _iphonePressId;

    void QueueIphoneText(char value)
    {
        _iphoneText.Append(value);
        if (_iphoneTextFlushPosted) return;
        _iphoneTextFlushPosted = true;
        BeginInvoke(() => { _iphoneTextFlushPosted = false; FlushIphoneText(); });
    }

    void FlushIphoneText()
    {
        if (_iphoneText.Length == 0) return;
        string text = _iphoneText.ToString();
        _iphoneText.Clear();
        AppendIphoneInput(() => _iphoneController.SendKeysAsync(text));
    }

    void QueueIphoneInput(Func<Task> action)
    {
        FlushIphoneText();
        AppendIphoneInput(action);
    }

    void AppendIphoneInput(Func<Task> action)
    {
        // Queue on the UI thread, execute in order off-thread. Text is batched for
        // one UI turn, and a shortcut cannot overtake the preceding text batch.
        _iphoneKeyboardTail = _iphoneKeyboardTail.ContinueWith(async _ =>
        {
            if (_closing || _viewMode != StudioViewMode.IphoneOnly) return;
            try { await action(); }
            catch (Exception) { /* Backend status remains separate from queue lifetime. */ }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    void OnIphoneClipboardTextChanged(string text)
    {
        if (_closing || IsDisposed || !IsHandleCreated || _viewMode != StudioViewMode.IphoneOnly) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_closing || IsDisposed || _viewMode != StudioViewMode.IphoneOnly) return;
                try { if (!Clipboard.ContainsText() || Clipboard.GetText() != text) Clipboard.SetText(text); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            });
        }
        catch (InvalidOperationException) { }
    }

    void ReleaseIphoneContact(IphoneChassisView surface)
    {
        if (!_iphonePointerDown) return;
        _iphonePointerDown = false;
        if (_iphoneHidContact && surface.TryMapScreenPoint(_iphoneLastPoint, out var point, out var size, clamp: true))
            _iphoneController.SendHidTouchUp(point.X, point.Y, size, brake: false);
        _iphoneHidContact = false;
        _iphoneDragging = _iphoneHolding = false;
    }

    void WireIphoneControlEvents(IphoneChassisView surface)
    {
        bool wheelBusy = false;
        long lastMoveTick = 0;
        surface.MouseDown += (_, e) =>
        {
            if (_devicePopover.Visible) { _devicePopover.Dismiss(); Invalidate(); }
            surface.Focus();
            if (e.Button != MouseButtons.Left || !surface.TryMapScreenPoint(e.Location, out var point, out var size)) return;
            ReleaseIphoneContact(surface);
            _iphoneMouseDownPt = _iphoneLastPoint = e.Location;
            _iphoneMouseDownTime = DateTime.UtcNow;
            _iphoneDragging = false;
            _iphoneHolding = false;
            _iphonePointerDown = true;
            lastMoveTick = 0;
            _iphoneHidContact = false;
            surface.Capture = true;
            // Press and hold: a still press becomes a held touch on the phone (long press,
            // then drag to move things). Quick clicks stay taps.
            int press = ++_iphonePressId;
            _ = Task.Delay(IphoneHoldDelayMs).ContinueWith(_ => { if (!_closing && IsHandleCreated && !IsDisposed) BeginInvoke(() =>
            {
                if (press != _iphonePressId || !_iphonePointerDown || _iphoneDragging) return;
                if (!surface.TryMapScreenPoint(_iphoneMouseDownPt, out var start, out var startSize, clamp: true)) return;
                _iphoneHidContact = _iphoneController.SendHidTouchDown(start.X, start.Y, startSize);
                if (!_iphoneHidContact) return;
                _iphoneDragging = _iphoneHolding = true;
                lastMoveTick = Environment.TickCount64;
            }); }, TaskScheduler.Default);
        };
        surface.MouseMove += (_, e) =>
        {
            if (!_iphonePointerDown || !surface.TryMapScreenPoint(e.Location, out var point, out var size, clamp: true)) return;
            _iphoneLastPoint = e.Location;
            if (!_iphoneDragging && (Math.Abs(e.X - _iphoneMouseDownPt.X) > 4 || Math.Abs(e.Y - _iphoneMouseDownPt.Y) > 4))
            {
                _iphoneDragging = true;
                lastMoveTick = Environment.TickCount64;
                surface.TryMapScreenPoint(_iphoneMouseDownPt, out var start, out var startSize, clamp: true);
                _iphoneHidContact = _iphoneController.SendHidTouchDown(start.X, start.Y, startSize);
                _iphoneController.SendHidTouchMove(point.X, point.Y, size);
            }
            else if (_iphoneDragging && Environment.TickCount64 - lastMoveTick >= 12)
            {
                lastMoveTick = Environment.TickCount64;
                _iphoneController.SendHidTouchMove(point.X, point.Y, size);
            }
        };
        surface.MouseUp += async (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { await _iphoneController.SendHomeAsync(); return; }
            if (e.Button == MouseButtons.Middle) { await _iphoneController.OpenAppSwitcherAsync(); return; }
            if (e.Button != MouseButtons.Left || !_iphonePointerDown) return;
            bool dragged = _iphoneDragging, held = _iphoneHolding;
            _iphoneLastPoint = e.Location;
            _iphonePointerDown = false;
            _iphoneHidContact = false;
            _iphoneDragging = _iphoneHolding = false;
            _iphonePressId++;
            surface.Capture = false;
            if (!surface.TryMapScreenPoint(e.Location, out var end, out var size, clamp: true)) return;
            surface.TryMapScreenPoint(_iphoneMouseDownPt, out var start, out var startSize, clamp: true);
            if (dragged)
            {
                bool horizontal = Math.Abs(e.X - _iphoneMouseDownPt.X) > Math.Abs(e.Y - _iphoneMouseDownPt.Y) * 1.15;
                // A held touch lifts in place; only a quick horizontal swipe keeps momentum.
                if (!_iphoneController.SendHidTouchUp(end.X, end.Y, size, brake: held || !horizontal))
                    await _iphoneController.SendSwipeAsync(start.X, start.Y, end.X, end.Y, size,
                        durationSec: Math.Clamp((DateTime.UtcNow - _iphoneMouseDownTime).TotalSeconds, .20, .36));
            }
            else
                await _iphoneController.SendTapAsync(end.X, end.Y, size);
        };
        surface.MouseCaptureChanged += (_, _) => { if (!surface.Capture) ReleaseIphoneContact(surface); };
        surface.MouseWheel += async (_, e) =>
        {
            if (wheelBusy || e.Delta == 0) return;
            wheelBusy = true;
            try
            {
                var r = surface.ScreenBounds;
                int x = r.X + r.Width / 2;
                var from = new Point(x, r.Y + (int)(r.Height * (e.Delta < 0 ? .58 : .42)));
                var to = new Point(x, r.Y + (int)(r.Height * (e.Delta < 0 ? .42 : .58)));
                surface.TryMapScreenPoint(from, out var start, out var size, clamp: true);
                surface.TryMapScreenPoint(to, out var end, out var endSize, clamp: true);
                await _iphoneController.SendSwipeAsync(start.X, start.Y, end.X, end.Y, size, durationSec: .26);
            }
            finally { wheelBusy = false; }
        };
    }
}
