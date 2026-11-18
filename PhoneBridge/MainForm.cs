using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhoneBridge;

public enum StudioViewMode
{
    None,
    AndroidOnly,
    IphoneOnly
}

public sealed partial class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint SendMessage(nint hWnd, int msg, nint wp, nint lp);
    [DllImport("user32.dll")] static extern nint SetParent(nint hWndChild, nint hWndNewParent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")] static extern bool MoveWindow(nint hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint SetFocus(nint hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(nint hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    static nint s_jobHandle;

    static void EnsureKillOnCloseJob()
    {
        if (s_jobHandle != 0) return;
        try
        {
            s_jobHandle = CreateJobObject(0, null);
            if (s_jobHandle != 0)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                SetInformationJobObject(s_jobHandle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                AssignProcessToJobObject(s_jobHandle, Process.GetCurrentProcess().Handle);
            }
        }
        catch { }
    }

    const int GWL_STYLE = -16;
    static readonly nint WS_CAPTION = 0x00C00000;
    static readonly nint WS_THICKFRAME = 0x00040000;
    static readonly nint WS_POPUP = unchecked((nint)0x80000000);
    static readonly nint WS_CHILD = 0x40000000;
    static readonly nint WS_VISIBLE = 0x10000000;
    const int SW_SHOW = 5;
    const uint WM_CLOSE = 0x0010;
    const int WM_NCHITTEST = 0x0084;
    const int HTCLIENT = 1;
    const int HTLEFT = 10;
    const int HTRIGHT = 11;
    const int HTTOP = 12;
    const int HTTOPLEFT = 13;
    const int HTTOPRIGHT = 14;
    const int HTBOTTOM = 15;
    const int HTBOTTOMLEFT = 16;
    const int HTBOTTOMRIGHT = 17;

    // Logical chrome metrics; the *H/Pad properties are the same in device pixels.
    const int BarTop = 24, BarBottom = 34, Side = 8;
    const int ResizeGrip = 5;
    static int S(float logical) => ShellRenderer.Px(logical);
    static int TopBarH => S(BarTop);
    static int BottomBarH => S(BarBottom);
    static int SidePad => S(Side);
    // The shell follows the rotate button only: the phone's own picture orientation never reshapes it.
    double ScreenAspect => _landscape ? 1 / PortraitAspect : PortraitAspect;
    double PortraitAspect
    {
        get
        {
            double aspect = _viewMode == StudioViewMode.IphoneOnly
                ? _iphonePanel.FrameAspectRatio ?? (double)_iphoneController.LogicalWidth / _iphoneController.LogicalHeight
                : 414.0 / 896;
            return aspect > 1 ? 1 / aspect : aspect;
        }
    }
    int ShellHeight(int width) => TopBarH + BottomBarH + (int)Math.Round((width - SidePad * 2) / ScreenAspect);

    [StructLayout(LayoutKind.Sequential)]
    struct SizingRect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    readonly Adb _adb;
    readonly AndroidGovernor _androidGovernor;
    readonly IphoneService _iphoneService = new();
    readonly IphoneController _iphoneController = new();
    readonly WaiLinkAgentServer _agentServer;

    readonly Panel _stage;
    readonly GlassDeviceChooser _chooserPanel;
    readonly AndroidChassisView _androidPanel;
    readonly IphoneChassisView _iphonePanel;
    readonly DevicePickerPopup _devicePopover;
    readonly ShellStylePopup _stylePopup;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _watchdog;
    readonly System.Windows.Forms.Timer _telemetryTimer;

    StudioViewMode _viewMode = StudioViewMode.None;
    bool _iphoneServiceStarted;

    // Top bar hit rectangles:
    // 0 = Device Picker Trigger, 1 = VolUp (+), 2 = VolDown (-), 3 = Power, 4 = Minimize, 5 = Close
    Rectangle _pickerTriggerRect;
    Rectangle _hwGroupRect;
    Rectangle _btnVolUpRect;
    Rectangle _btnVolDownRect;
    Rectangle _btnPowerRect;
    Rectangle _winGroupRect;
    Rectangle _btnMinRect;
    Rectangle _btnCloseRect;
    int _hoverTopId = -1;
    int _pressedTopId = -1;

    // Bottom bar hit rectangles (3 distinct keys: 0 = Back, 1 = Home, 2 = Recents)
    Rectangle _botBtn0Rect;
    Rectangle _botBtn1Rect;
    Rectangle _botBtn2Rect;
    int _hoverBotId = -1;
    int _pressedBotId = -1;

    Process? _scrcpy;
    nint _scrcpyHwnd;
    bool _connecting;
    bool _streamVisible;
    bool _closing;
    bool _boostBusy;
    string _androidDeviceName = "Android";
    string _latestOtpCode = "";
    string _latestOtpSource = "";

    Point _iphoneMouseDownPt;
    DateTime _iphoneMouseDownTime;
    bool _iphoneDragging;

    static string ScrcpyDir => AppPaths.Lib;
    static string ScrcpyExe => Path.Combine(ScrcpyDir, "wailink-render.exe");
    static string AdbExe => Path.Combine(ScrcpyDir, "adb.exe");

    public MainForm()
    {
        EnsureKillOnCloseJob();

        _adb = new Adb(AdbExe);
        _androidGovernor = new AndroidGovernor(_adb);

        Text = "WAI Link";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShellRenderer.Scale = DeviceDpi / 96f;
        Size = new Size(S(356), TopBarH + BottomBarH + (int)Math.Round((S(356) - SidePad * 2) * 896.0 / 414));
        MinimumSize = new Size(S(280), S(320));
        BackColor = Color.Black;
        ForeColor = GlassPresentation.Ink;
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        try
        {
            using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("PhoneBridge.Assets.Brand.app.ico");
            if (iconStream is not null) Icon = new Icon(iconStream);
        }
        catch { }

        _stage = new Panel
        {
            BackColor = Color.FromArgb(9, 10, 14)
        };

        _chooserPanel = new GlassDeviceChooser
        {
            Dock = DockStyle.Fill,
            Visible = true
        };
        _chooserPanel.OnDeviceChosen += mode => SetViewMode(mode);

        _androidPanel = new AndroidChassisView
        {
            Dock = DockStyle.Fill,
            Visible = false
        };
        EnableDragAndDrop(_androidPanel, isIphone: false);

        _iphonePanel = new IphoneChassisView
        {
            Dock = DockStyle.Fill,
            Visible = false
        };
        _iphonePanel.OnConnectWdaRequested += () =>
        {
            if (_viewMode == StudioViewMode.IphoneOnly)
                _ = ConnectIphoneControlAsync();
        };
        EnableDragAndDrop(_iphonePanel, isIphone: true);
        WireIphoneControlEvents(_iphonePanel);
        _iphonePanel.FrameAspectChanged += FitShellToScreen;
        _iphoneController.ClipboardTextChanged += OnIphoneClipboardTextChanged;

        ShellStyle.Load();
        _devicePopover = new DevicePickerPopup(this) { TriggerScreenBounds = () => ShellToScreen(_pickerTriggerRect) };
        _devicePopover.OnModeSelected += mode =>
        {
            SetViewMode(mode);
            Invalidate();
        };
        _devicePopover.AnimationChanged += Invalidate;
        _stylePopup = new ShellStylePopup(this) { TriggerScreenBounds = () => ShellToScreen(_btnStyleRect) };
        InitUnlock();
        InitRotation();
        _typeBox = new TypeBoxPopup(this);
        ShellStyle.Changed += () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(UpdatePhoneAudio); };

        _stage.Controls.Add(_chooserPanel);
        _stage.Controls.Add(_androidPanel);
        _stage.Controls.Add(_iphonePanel);
        Controls.Add(_stage);

        _iphoneService.OnMessageReceived += evt =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                if (!string.IsNullOrEmpty(evt.OtpCode))
                {
                    _latestOtpCode = evt.OtpCode;
                    _latestOtpSource = evt.Sender;
                    try { Clipboard.SetText(evt.OtpCode); } catch { }
                    Invalidate();
                }
            });
        };

        _androidGovernor.OnAndroidOtpDetected += (otp, sender, _) =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                _latestOtpCode = otp;
                _latestOtpSource = sender;
                try { Clipboard.SetText(otp); } catch { }
                Invalidate();
            });
        };

        _tray = BuildTray();

        _watchdog = new System.Windows.Forms.Timer { Interval = 2500 };
        _watchdog.Tick += async (_, _) => await WatchdogTickAsync();

        _telemetryTimer = new System.Windows.Forms.Timer { Interval = 3500 };
        _telemetryTimer.Tick += async (_, _) => await PollTelemetryAsync();

        _agentServer = new WaiLinkAgentServer(
            _adb,
            _androidGovernor,
            _iphoneController,
            _iphoneService,
            () => this,
            () => _viewMode switch
            {
                StudioViewMode.IphoneOnly => "IphoneOnly",
                StudioViewMode.AndroidOnly => "AndroidOnly",
                _ => "None"
            },
            mode => SetViewMode(mode.Contains("Iphone", StringComparison.OrdinalIgnoreCase)
                ? StudioViewMode.IphoneOnly
                : StudioViewMode.AndroidOnly),
            () => "Default",
            _ => Invalidate(true),
            () => (_latestOtpCode, _latestOtpSource)
        );

        Shown += (_, _) =>
        {
            ApplyWindowRegion();
            LayoutViewport();
            _watchdog.Start();
            _telemetryTimer.Start();
            _agentServer.Start();
            StartPresentation();
            StartDeviceScan();
            FocusActiveSurface();
        };

        Resize += (_, _) =>
        {
            ResetGlassSurface();
            ApplyWindowRegion();
            LayoutViewport();
            Invalidate();
        };
        LocationChanged += (_, _) => MoveShellSurfaces();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0214) // WM_SIZING: preserve the opposite edge/corner as anchor.
        {
            var rect = Marshal.PtrToStructure<SizingRect>(m.LParam);
            int edge = (int)m.WParam;
            bool fromHeight = edge is 3 or 6;
            int width = Math.Max(MinimumSize.Width, fromHeight
                ? SidePad * 2 + (int)Math.Round((rect.Bottom - rect.Top - TopBarH - BottomBarH) * ScreenAspect) : rect.Right - rect.Left);
            int height = ShellHeight(width);
            if (edge is 1 or 4 or 7) rect.Left = rect.Right - width;
            else if (fromHeight)
            {
                int center = (rect.Left + rect.Right) / 2;
                rect.Left = center - width / 2; rect.Right = rect.Left + width;
            }
            else rect.Right = rect.Left + width;
            if (edge is 3 or 4 or 5) rect.Top = rect.Bottom - height;
            else if (edge is 1 or 2)
            {
                int center = (rect.Top + rect.Bottom) / 2;
                rect.Top = center - height / 2; rect.Bottom = rect.Top + height;
            }
            else rect.Bottom = rect.Top + height;
            Marshal.StructureToPtr(rect, m.LParam, false);
            m.Result = 1;
            return;
        }
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                long coords = m.LParam.ToInt64();
                Point pt = PointToClient(new Point((short)(coords & 0xffff), (short)((coords >> 16) & 0xffff)));
                int w = ClientSize.Width;
                int h = ClientSize.Height;
                int c = 10;

                if (pt.X < c && pt.Y < c) { m.Result = (nint)HTTOPLEFT; return; }
                if (pt.X >= w - c && pt.Y < c) { m.Result = (nint)HTTOPRIGHT; return; }
                if (pt.X < c && pt.Y >= h - c) { m.Result = (nint)HTBOTTOMLEFT; return; }
                if (pt.X >= w - c && pt.Y >= h - c) { m.Result = (nint)HTBOTTOMRIGHT; return; }
                if (pt.X < ResizeGrip) { m.Result = (nint)HTLEFT; return; }
                if (pt.X >= w - ResizeGrip) { m.Result = (nint)HTRIGHT; return; }
                if (pt.Y < ResizeGrip) { m.Result = (nint)HTTOP; return; }
                if (pt.Y >= h - ResizeGrip) { m.Result = (nint)HTBOTTOM; return; }
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        FocusActiveSurface();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        ReleaseIphoneContact(_iphonePanel);
        _devicePopover.Dismiss();
        _stylePopup.Dismiss();
        Invalidate();
    }

    void FocusActiveSurface()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (_viewMode == StudioViewMode.None)
            {
                _chooserPanel.Focus();
            }
            else if (_viewMode == StudioViewMode.AndroidOnly)
            {
                if (_scrcpyHwnd != 0)
                {
                    uint curThread = GetCurrentThreadId();
                    uint childThread = GetWindowThreadProcessId(_scrcpyHwnd, out _);
                    if (childThread != 0 && childThread != curThread)
                        AttachThreadInput(curThread, childThread, true);
                    SetFocus(_scrcpyHwnd);
                }
                else
                {
                    _androidPanel.Focus();
                }
            }
            else
            {
                if (_scrcpyHwnd != 0)
                {
                    uint curThread = GetCurrentThreadId();
                    uint childThread = GetWindowThreadProcessId(_scrcpyHwnd, out _);
                    if (childThread != 0 && childThread != curThread)
                        AttachThreadInput(curThread, childThread, false);
                }
                _iphonePanel.Focus();
            }
        }
        catch { }
    }

    public void SetViewMode(StudioViewMode mode)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetViewMode(mode));
            return;
        }
        _devicePopover.SelectedMode = mode;
        if (_viewMode == mode)
        {
            FocusActiveSurface();
            return;
        }
        BeginViewportTransition();
        if (_viewMode == StudioViewMode.AndroidOnly) RestoreAndroidRotation();
        _viewMode = mode;
        if (_landscape) ApplyLandscape(false); // each phone starts upright

        if (_viewMode == StudioViewMode.AndroidOnly)
        {
            _chooserPanel.Visible = false;
            _iphonePanel.Visible = false;
            _iphonePanel.StopMjpegStream();
            _iphonePanel.WdaConnected = false;
            _iphoneController.StopBridge();

            _androidPanel.Visible = true;
            _androidPanel.BringToFront();
            if (_scrcpyHwnd != 0)
                MoveWindow(_scrcpyHwnd, 0, 0, _androidPanel.Width, _androidPanel.Height, true);
            else
                _ = EnsureConnectedAsync();
        }
        else if (_viewMode == StudioViewMode.IphoneOnly)
        {
            _chooserPanel.Visible = false;
            _androidPanel.Visible = false;
            KillScrcpy();

            if (!_iphoneServiceStarted)
            {
                _iphoneServiceStarted = true;
                _iphoneService.Start();
            }

            _iphonePanel.Visible = true;
            _iphonePanel.BringToFront();
            _ = ConnectIphoneControlAsync();
        }
        else
        {
            _iphonePanel.Visible = false;
            _androidPanel.Visible = false;
            _chooserPanel.Visible = true;
            _chooserPanel.BringToFront();
        }

        LayoutViewport();
        UpdatePhoneAudio();
        FocusActiveSurface();
        Invalidate();
    }

    async Task ConnectIphoneControlAsync()
    {
        _iphonePanel.StatusText = "Connecting to iPhone...";
        Invalidate();

        bool ok = await _iphoneController.ConnectWdaAsync();
        if (IsDisposed) return;

        BeginInvoke(() =>
        {
            _iphonePanel.MjpegHost = _iphoneController.MjpegHost;
            if (_closing || _viewMode != StudioViewMode.IphoneOnly) return;
            _iphonePanel.WdaConnected = ok;
            if (ok) FitShellToScreen();
            if (ok)
                _iphonePanel.StartMjpegStream();

            _iphonePanel.StatusText = ok
                ? "Connected"
                : "Ensure iPhone is on Wi-Fi or plugged in via USB";
            FocusActiveSurface();
            Invalidate();
        });
    }

    // The form itself is only the phone viewport; the chassis is painted by the frame window.
    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Black);

    void DrawTopGlassHeader(Graphics g, int w)
    {
        // 03 / DEVICE PICKER: "One device at a time. The active phone stays visible. Click it to switch."
        _pickerTriggerRect = new Rectangle(SidePad + 2, 5, 114, 28);
        bool pickerActiveOrHover = _hoverTopId == 0 || _devicePopover.Visible;
        if (pickerActiveOrHover)
        {
            using var trigPath = Theme.RoundedRect(_pickerTriggerRect, 8);
            using var trigBrush = new SolidBrush(Color.FromArgb(42, 214, 166, 120));
            g.FillPath(trigBrush, trigPath);
        }

        bool isIphone = _viewMode == StudioViewMode.IphoneOnly;
        bool isAndroid = _viewMode == StudioViewMode.AndroidOnly;
        bool online = isIphone
            ? _iphoneController.WdaConnected
            : (isAndroid && (_streamVisible || _adb.Serial is not null));

        // Status dot
        Color dotCol = online ? Color.FromArgb(74, 222, 128) : Color.FromArgb(120, 134, 156);
        using (var dotBrush = new SolidBrush(dotCol))
        {
            g.FillEllipse(dotBrush, _pickerTriggerRect.X + 7, _pickerTriggerRect.Y + 11, 5.5f, 5.5f);
        }

        // Device glyph + name + chevron
        Color fgCol = pickerActiveOrHover ? Color.FromArgb(248, 216, 184) : Color.FromArgb(236, 242, 249);
        int iconX = _pickerTriggerRect.X + 18;
        int iconY = _pickerTriggerRect.Y + 7;
        if (isAndroid)
            DrawAndroidGlyph(g, iconX, iconY, fgCol);
        else
            DrawIphoneGlyph(g, iconX, iconY, fgCol);

        string devLabel = _viewMode switch
        {
            StudioViewMode.IphoneOnly => "iPhone",
            StudioViewMode.AndroidOnly => "Android",
            _ => "Select"
        };
        using (var font = Theme.Font(8.75f, FontStyle.Bold))
        using (var textBrush = new SolidBrush(fgCol))
        using (var leftFmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
        {
            var textRect = new RectangleF(iconX + 14, _pickerTriggerRect.Y, 64, _pickerTriggerRect.Height);
            g.DrawString(devLabel, font, textBrush, textRect, leftFmt);
        }

        // Chevron
        using (var chevPen = new Pen(Color.FromArgb(180, fgCol), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            int cx = _pickerTriggerRect.Right - 14;
            int cy = _pickerTriggerRect.Y + 14;
            if (_devicePopover.Visible)
            {
                g.DrawLines(chevPen, [new Point(cx - 3, cy + 2), new Point(cx, cy - 1), new Point(cx + 3, cy + 2)]);
            }
            else
            {
                g.DrawLines(chevPen, [new Point(cx - 3, cy - 1), new Point(cx, cy + 2), new Point(cx + 3, cy - 1)]);
            }
        }

        // 04 / WINDOW & SHELL:
        // Rightmost: Minimize and close share a compact group [ —  ✕ ]
        _winGroupRect = new Rectangle(w - SidePad - 58, 6, 56, 26);
        _btnMinRect = new Rectangle(_winGroupRect.X + 2, _winGroupRect.Y + 2, 25, 22);
        _btnCloseRect = new Rectangle(_winGroupRect.X + 29, _winGroupRect.Y + 2, 25, 22);

        using (var winGroupPath = Theme.RoundedRect(_winGroupRect, 8))
        using (var winGroupFill = new SolidBrush(Color.FromArgb(46, 22, 27, 36)))
        using (var winGroupBorder = new Pen(Color.FromArgb(48, 255, 255, 255), 1f))
        {
            g.FillPath(winGroupFill, winGroupPath);
            g.DrawPath(winGroupBorder, winGroupPath);
        }

        DrawTopIconHighlight(g, _btnMinRect, _hoverTopId == 4, _pressedTopId == 4, isClose: false);
        DrawTopIconHighlight(g, _btnCloseRect, _hoverTopId == 5, _pressedTopId == 5, isClose: true);

        using (var winPen = new Pen(Color.FromArgb(226, 232, 240), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            int my = _btnMinRect.Y + _btnMinRect.Height / 2;
            g.DrawLine(winPen, _btnMinRect.X + 8, my, _btnMinRect.Right - 8, my);

            int cx1 = _btnCloseRect.X + 8, cy1 = _btnCloseRect.Y + 7;
            int cx2 = _btnCloseRect.Right - 8, cy2 = _btnCloseRect.Bottom - 7;
            g.DrawLine(winPen, cx1, cy1, cx2, cy2);
            g.DrawLine(winPen, cx2, cy1, cx1, cy2);
        }

        // Hardware group: Volume Up (+), Volume Down (-), Power/Lock [ +  −  ⏻ ]
        _hwGroupRect = new Rectangle(_winGroupRect.X - 86, 6, 80, 26);
        _btnVolUpRect = new Rectangle(_hwGroupRect.X + 2, _hwGroupRect.Y + 2, 24, 22);
        _btnVolDownRect = new Rectangle(_hwGroupRect.X + 28, _hwGroupRect.Y + 2, 24, 22);
        _btnPowerRect = new Rectangle(_hwGroupRect.X + 54, _hwGroupRect.Y + 2, 24, 22);

        using (var hwGroupPath = Theme.RoundedRect(_hwGroupRect, 8))
        using (var hwGroupFill = new SolidBrush(Color.FromArgb(46, 22, 27, 36)))
        using (var hwGroupBorder = new Pen(Color.FromArgb(48, 255, 255, 255), 1f))
        {
            g.FillPath(hwGroupFill, hwGroupPath);
            g.DrawPath(hwGroupBorder, hwGroupPath);
        }

        DrawTopIconHighlight(g, _btnVolUpRect, _hoverTopId == 1, _pressedTopId == 1, isClose: false);
        DrawTopIconHighlight(g, _btnVolDownRect, _hoverTopId == 2, _pressedTopId == 2, isClose: false);
        DrawTopIconHighlight(g, _btnPowerRect, _hoverTopId == 3, _pressedTopId == 3, isClose: false);

        // Volume Up (+)
        Color vUpCol = (_hoverTopId == 1 || _pressedTopId == 1) ? Color.FromArgb(246, 203, 162) : Color.FromArgb(216, 224, 236);
        using (var p = new Pen(vUpCol, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            int cx = _btnVolUpRect.X + _btnVolUpRect.Width / 2;
            int cy = _btnVolUpRect.Y + _btnVolUpRect.Height / 2;
            g.DrawLine(p, cx - 4, cy, cx + 4, cy);
            g.DrawLine(p, cx, cy - 4, cx, cy + 4);
        }

        // Volume Down (-)
        Color vDnCol = (_hoverTopId == 2 || _pressedTopId == 2) ? Color.FromArgb(246, 203, 162) : Color.FromArgb(216, 224, 236);
        using (var p = new Pen(vDnCol, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            int cx = _btnVolDownRect.X + _btnVolDownRect.Width / 2;
            int cy = _btnVolDownRect.Y + _btnVolDownRect.Height / 2;
            g.DrawLine(p, cx - 4, cy, cx + 4, cy);
        }

        // Power / Lock icon (⏻)
        Color pwrCol = (_hoverTopId == 3 || _pressedTopId == 3) ? Color.FromArgb(246, 203, 162) : Color.FromArgb(236, 186, 138);
        using (var p = new Pen(pwrCol, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            int cx = _btnPowerRect.X + _btnPowerRect.Width / 2;
            int cy = _btnPowerRect.Y + _btnPowerRect.Height / 2 + 1;
            g.DrawArc(p, cx - 4.5f, cy - 4.5f, 9f, 9f, -60f, 300f);
            g.DrawLine(p, cx, cy - 6f, cx, cy - 1f);
        }
    }

    static void DrawTopIconHighlight(Graphics g, Rectangle rect, bool hover, bool pressed, bool isClose)
    {
        if (!hover && !pressed) return;
        using var path = Theme.RoundedRect(rect, 6);
        Color fill = isClose
            ? (pressed ? Color.FromArgb(210, 185, 28, 28) : Color.FromArgb(165, 185, 28, 28))
            : (pressed ? Color.FromArgb(105, 214, 166, 120) : Color.FromArgb(55, 214, 166, 120));
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
    }

    public static void DrawIphoneGlyph(Graphics g, int x, int y, Color color)
    {
        using var pen = new Pen(color, 1.35f);
        using var path = Theme.RoundedRect(new Rectangle(x, y, 9, 14), 2);
        g.DrawPath(pen, path);
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, x + 3, y + 2, 3, 1);
    }

    public static void DrawAndroidGlyph(Graphics g, int x, int y, Color color)
    {
        using var pen = new Pen(color, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, x, y + 3, 10, 9, 180, 180);
        g.DrawLine(pen, x, y + 8, x + 10, y + 8);
        g.DrawLine(pen, x + 2, y + 4, x + 1, y + 1);
        g.DrawLine(pen, x + 8, y + 4, x + 9, y + 1);
    }

    void DrawBottomGlassNav(Graphics g, int w, int h)
    {
        // 02 / NAVIGATION: "Three distinct keys: A left arrow, a home key, and stacked app cards."
        int navY = h - BottomBarH + 6;
        int navH = BottomBarH - 12;
        int totalW = w - SidePad * 2;
        int colW = totalW / 3;

        int pillW = Math.Min(104, colW - 8);
        _botBtn0Rect = new Rectangle(SidePad + (colW - pillW) / 2, navY, pillW, navH);
        _botBtn1Rect = new Rectangle(SidePad + colW + (colW - pillW) / 2, navY, pillW, navH);
        _botBtn2Rect = new Rectangle(SidePad + colW * 2 + (colW - pillW) / 2, navY, pillW, navH);

        DrawNavKey(g, _botBtn0Rect, 0, "Back", _hoverBotId == 0, _pressedBotId == 0);
        DrawNavKey(g, _botBtn1Rect, 1, "Home", _hoverBotId == 1, _pressedBotId == 1);
        DrawNavKey(g, _botBtn2Rect, 2, "Recents", _hoverBotId == 2, _pressedBotId == 2);
    }

    static void DrawNavKey(Graphics g, Rectangle rect, int kind, string label, bool hover, bool pressed)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Warm bronze/amber highlight pill when hovered or pressed (02 / NAVIGATION: PRESSED)
        if (hover || pressed)
        {
            using var pillPath = Theme.RoundedRect(rect, 11);
            Color fillCol = pressed
                ? Color.FromArgb(105, 206, 154, 106)
                : Color.FromArgb(48, 206, 154, 106);
            using var fillBrush = new SolidBrush(fillCol);
            g.FillPath(fillBrush, pillPath);

            Color borderCol = pressed
                ? Color.FromArgb(135, 236, 188, 140)
                : Color.FromArgb(65, 236, 188, 140);
            using var borderPen = new Pen(borderCol, 1f);
            g.DrawPath(borderPen, pillPath);
        }

        Color iconCol = (hover || pressed)
            ? Color.FromArgb(246, 203, 162)
            : Color.FromArgb(226, 232, 240);
        Color labelCol = (hover || pressed)
            ? Color.FromArgb(238, 192, 148)
            : Color.FromArgb(156, 168, 186);

        int cx = rect.X + rect.Width / 2;
        int iconCy = rect.Y + 13;

        using (var iconPen = new Pen(iconCol, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (kind == 0)
            {
                // Left arrow (Back)
                g.DrawLine(iconPen, cx + 6, iconCy, cx - 5, iconCy);
                g.DrawLines(iconPen, [new Point(cx - 1, iconCy - 4), new Point(cx - 6, iconCy), new Point(cx - 1, iconCy + 4)]);
            }
            else if (kind == 1)
            {
                // Rounded squircle with inner squircle (Home)
                using var outerSq = Theme.RoundedRect(new Rectangle(cx - 7, iconCy - 7, 14, 14), 4);
                g.DrawPath(iconPen, outerSq);
                using var innerPen = new Pen(Color.FromArgb(150, iconCol), 1.2f);
                using var innerSq = Theme.RoundedRect(new Rectangle(cx - 3, iconCy - 3, 6, 6), 2);
                g.DrawPath(innerPen, innerSq);
            }
            else
            {
                // Stacked app cards (Recents)
                using var frontCard = Theme.RoundedRect(new Rectangle(cx - 6, iconCy - 4, 9, 11), 2);
                g.DrawPath(iconPen, frontCard);
                // Back card peeking top-right
                g.DrawLines(iconPen,
                [
                    new Point(cx - 2, iconCy - 4),
                    new Point(cx - 2, iconCy - 7),
                    new Point(cx + 6, iconCy - 7),
                    new Point(cx + 6, iconCy + 3),
                    new Point(cx + 3, iconCy + 3)
                ]);
            }
        }

        using var labelFont = Theme.Font(7.75f, FontStyle.Regular);
        using var labelBrush = new SolidBrush(labelCol);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var labelRect = new RectangleF(rect.X, rect.Y + 23, rect.Width, 16);
        g.DrawString(label, labelFont, labelBrush, labelRect, fmt);
    }

    int HitTestTopBar(Point pt)
    {
        if (_pickerTriggerRect.Contains(pt)) return 0;
        if (_btnVolUpRect.Contains(pt)) return 1;
        if (_btnVolDownRect.Contains(pt)) return 2;
        if (_btnPowerRect.Contains(pt)) return 3;
        if (_btnMinRect.Contains(pt)) return 4;
        if (_btnCloseRect.Contains(pt)) return 5;
        if (_btnStyleRect.Contains(pt)) return StyleButtonId;
        if (_btnUnlockRect.Contains(pt)) return UnlockButtonId;
        if (_btnRotateRect.Contains(pt)) return RotateButtonId;
        return -1;
    }

    int HitTestBotBar(Point pt)
    {
        if (_botBtn0Rect.Contains(pt)) return 0;
        if (_botBtn1Rect.Contains(pt)) return 1;
        if (_botBtn2Rect.Contains(pt)) return 2;
        return -1;
    }

    int ResizeHit(Point pt)
    {
        int w = (int)(Width / ShellRenderer.Scale), h = (int)(Height / ShellRenderer.Scale), corner = 14;
        bool l = pt.X < ResizeGrip, r = pt.X >= w - ResizeGrip, t = pt.Y < ResizeGrip, b = pt.Y >= h - ResizeGrip;
        if (!(l || r || t || b)) return 0;
        bool nearL = pt.X < corner, nearR = pt.X >= w - corner, nearT = pt.Y < corner, nearB = pt.Y >= h - corner;
        if (nearL && nearT) return HTTOPLEFT;
        if (nearR && nearT) return HTTOPRIGHT;
        if (nearL && nearB) return HTBOTTOMLEFT;
        if (nearR && nearB) return HTBOTTOMRIGHT;
        return l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : HTBOTTOM;
    }

    internal void ChromeMouseMove(Point location)
    {
        int topHover = HitTestTopBar(location);
        int botHover = HitTestBotBar(location);
        if (_frame is not null)
        {
            int edge = topHover >= 0 || botHover >= 0 ? 0 : ResizeHit(location);
            _frame.Cursor = topHover >= 0 || botHover >= 0 ? Cursors.Hand : edge switch
            {
                HTTOPLEFT or HTBOTTOMRIGHT => Cursors.SizeNWSE,
                HTTOPRIGHT or HTBOTTOMLEFT => Cursors.SizeNESW,
                HTLEFT or HTRIGHT => Cursors.SizeWE,
                HTTOP or HTBOTTOM => Cursors.SizeNS,
                _ => Cursors.Default
            };
        }
        if (topHover != _hoverTopId || botHover != _hoverBotId)
        {
            _hoverTopId = topHover;
            _hoverBotId = botHover;
            Invalidate();
        }
    }

    internal void ChromeMouseLeave()
    {
        if (_hoverTopId != -1 || _hoverBotId != -1 || _pressedTopId != -1 || _pressedBotId != -1)
        {
            _hoverTopId = -1;
            _hoverBotId = -1;
            _pressedTopId = -1;
            _pressedBotId = -1;
            Invalidate();
        }
    }

    internal void ChromeMouseDown(Point location, MouseButtons button)
    {
        if (button != MouseButtons.Left) return;
        if (ActiveForm != this) Activate();

        int topHit = HitTestTopBar(location);
        int botHit = HitTestBotBar(location);

        if (topHit != 0) _devicePopover.Dismiss();
        if (topHit != StyleButtonId) _stylePopup.Dismiss();
        if (topHit != UnlockButtonId && !_unlockRecording) _unlockPopup.Dismiss();

        if (topHit >= 0)
        {
            _pressedTopId = topHit;
            Invalidate();
            return;
        }

        if (botHit >= 0)
        {
            _pressedBotId = botHit;
            Invalidate();
            return;
        }

        // Anything else on the chassis resizes from its edge or drags the window.
        int edge = WindowState == FormWindowState.Normal ? ResizeHit(location) : 0;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, edge != 0 ? edge : 2, 0);
        FocusActiveSurface();
    }

    internal void ChromeMouseUp(Point location, MouseButtons button)
    {
        if (button != MouseButtons.Left) return;

        int prevTop = _pressedTopId;
        int prevBot = _pressedBotId;
        _pressedTopId = -1;
        _pressedBotId = -1;
        Invalidate();

        int topHit = HitTestTopBar(location);
        if (topHit >= 0 && topHit == prevTop)
        {
            switch (topHit)
            {
                case 0: // Device picker
                    TogglePicker();
                    return;

                case RotateButtonId: // Portrait / landscape
                    _ = ToggleLandscapeAsync();
                    return;

                case UnlockButtonId: // iPhone unlock
                    ToggleUnlockPopup();
                    return;

                case StyleButtonId: // Shell opacity and color
                    ToggleStylePopup();
                    return;

                case 1: // Volume Up (+)
                    if (_viewMode == StudioViewMode.IphoneOnly)
                        _ = RunIphoneButtonAsync(() => _iphoneController.PressButtonAsync("volumeUp"), 1);
                    else if (_viewMode == StudioViewMode.AndroidOnly)
                        _ = _adb.SendKeyEventAsync("24");
                    FocusActiveSurface();
                    return;

                case 2: // Volume Down (-)
                    if (_viewMode == StudioViewMode.IphoneOnly)
                        _ = RunIphoneButtonAsync(() => _iphoneController.PressButtonAsync("volumeDown"), 2);
                    else if (_viewMode == StudioViewMode.AndroidOnly)
                        _ = _adb.SendKeyEventAsync("25");
                    FocusActiveSurface();
                    return;

                case 3: // Power / Lock (⏻)
                    if (_viewMode == StudioViewMode.IphoneOnly)
                        _ = RunIphoneButtonAsync(() => _iphoneController.TogglePowerLockAsync(), 3);
                    else if (_viewMode == StudioViewMode.AndroidOnly)
                        _ = _adb.SendKeyEventAsync("26");
                    FocusActiveSurface();
                    return;

                case 4: // Minimize
                    WindowState = FormWindowState.Minimized;
                    return;

                case 5: // Close
                    _closing = true;
                    Close();
                    return;
            }
        }

        int botHit = HitTestBotBar(location);
        if (botHit >= 0 && botHit == prevBot)
        {
            if (_viewMode == StudioViewMode.AndroidOnly)
            {
                if (botHit == 0) _ = _adb.SendKeyEventAsync("4");   // Back
                if (botHit == 1) _ = _adb.SendKeyEventAsync("3");   // Home
                if (botHit == 2) _ = _adb.SendKeyEventAsync("187"); // Recents
            }
            else if (_viewMode == StudioViewMode.IphoneOnly)
            {
                if (botHit == 0) _ = _iphoneController.NavigateBackAsync();
                if (botHit == 1) _ = _iphoneController.SendHomeAsync();
                if (botHit == 2) _ = _iphoneController.OpenAppSwitcherAsync();
            }
            FocusActiveSurface();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.D))
        {
            TogglePicker();
            return true;
        }
        if (_devicePopover.HandleKey(keyData))
        {
            FocusActiveSurface();
            return true;
        }
        if (TypeBoxHandleKey(keyData)) return true;
        if (keyData == (Keys.Control | Keys.L) && _viewMode == StudioViewMode.IphoneOnly)
        {
            _ = UnlockIphoneAsync();
            return true;
        }
        if (keyData == Keys.Escape && _unlockPopup.IsOpen && !_unlockRecording)
        {
            _unlockPopup.Dismiss();
            return true;
        }
        if (keyData == Keys.Escape && _stylePopup.IsOpen)
        {
            _stylePopup.Dismiss();
            return true;
        }
        if (_viewMode == StudioViewMode.IphoneOnly && _iphoneController.WdaConnected)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.A:
                    QueueIphoneInput(() => _iphoneController.SelectAllAsync());
                    return true;

                case Keys.Control | Keys.C:
                case Keys.Control | Keys.Insert:
                    QueueIphoneInput(async () =>
                    {
                        var copied = await _iphoneController.CopyToClipboardAsync();
                        if (!string.IsNullOrEmpty(copied) && !IsDisposed)
                            BeginInvoke(() => { try { Clipboard.SetText(copied); } catch { } });
                    });
                    return true;

                case Keys.Control | Keys.X:
                case Keys.Shift | Keys.Delete:
                    QueueIphoneInput(async () =>
                    {
                        var cut = await _iphoneController.CutToClipboardAsync();
                        if (!string.IsNullOrEmpty(cut) && !IsDisposed)
                            BeginInvoke(() => { try { Clipboard.SetText(cut); } catch { } });
                    });
                    return true;

                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    string clipText = "";
                    try { if (Clipboard.ContainsText()) clipText = Clipboard.GetText(); } catch { }
                    QueueIphoneInput(() => _iphoneController.PasteFromClipboardAsync(clipText));
                    return true;

                case Keys.Control | Keys.Z:
                    QueueIphoneInput(() => _iphoneController.SendKeyboardShortcutAsync("z"));
                    return true;

                case Keys.Control | Keys.Back:
                    QueueIphoneInput(() => _iphoneController.ClearFocusedElementAsync());
                    return true;

                case Keys.Back:
                case Keys.Shift | Keys.Back:
                case Keys.Delete:
                    QueueIphoneInput(() => _iphoneController.SendBackspaceAsync());
                    return true;

                case Keys.Return:
                case Keys.Shift | Keys.Return:
                    QueueIphoneInput(() => _iphoneController.SendKeysAsync("\n"));
                    return true;

                case Keys.Tab:
                    QueueIphoneInput(() => _iphoneController.SendKeysAsync("\t"));
                    return true;
            }
        }
        else if (_viewMode == StudioViewMode.AndroidOnly)
        {
            FocusActiveSurface();
            switch (keyData)
            {
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    string androidClip = "";
                    try { if (Clipboard.ContainsText()) androidClip = Clipboard.GetText(); } catch { }
                    if (!string.IsNullOrEmpty(androidClip))
                        _ = _adb.TypeTextAsync(androidClip);
                    return true;
                case Keys.Control | Keys.C:
                    _ = _adb.SendKeyEventAsync("278");
                    return true;
                case Keys.Control | Keys.X:
                    _ = _adb.SendKeyEventAsync("277");
                    return true;
                case Keys.Back:
                case Keys.Shift | Keys.Back:
                    _ = _adb.SendKeyEventAsync("67");
                    return true;
                case Keys.Delete:
                    _ = _adb.SendKeyEventAsync("112");
                    return true;
                case Keys.Return:
                    _ = _adb.SendKeyEventAsync("66");
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsControl(e.KeyChar)) return;

        char ch = e.KeyChar; // WM_CHAR already accounts for keyboard layout and modifiers.

        if (_viewMode == StudioViewMode.IphoneOnly && _iphoneController.WdaConnected)
        {
            if (ShellStyle.TypeBox) TypeBoxAppend(ch.ToString());
            else QueueIphoneText(ch);
            e.Handled = true;
        }
        else if (_viewMode == StudioViewMode.AndroidOnly && _adb.Serial is not null)
        {
            FocusActiveSurface();
            _ = _adb.TypeTextAsync(ch.ToString());
            e.Handled = true;
        }
    }

    void ApplyWindowRegion()
    {
        // Only the screen belongs to this window. Its binary region edge sits
        // one pixel under the frame window's opaque, antialiased bezel.
        var screen = Rectangle.Inflate(_stage.Bounds, 1, 1);
        using var path = ShellRenderer.Round(screen, ShellRenderer.StageRadius * ShellRenderer.Scale + 1);
        var old = Region; Region = new Region(path); old?.Dispose();
    }

    void LayoutViewport()
    {
        int stageW = Math.Max(120, ClientSize.Width - SidePad * 2);
        int stageH = Math.Max(1, ClientSize.Height - TopBarH - BottomBarH);
        _stage.Bounds = new Rectangle(SidePad, TopBarH, stageW, stageH);
        ApplyWindowRegion();
        ResetGlassSurface();

        if (_scrcpyHwnd != 0 && _androidPanel.Visible)
            MoveWindow(_scrcpyHwnd, 0, 0, _androidPanel.Width, _androidPanel.Height, true);
        LayoutTransition();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ShellRenderer.Scale = e.DeviceDpiNew / 96f;
        _shadowSize = Size.Empty;
        _devicePopover.Dismiss(); _stylePopup.Dismiss(); _unlockPopup.Dismiss();
        FitShellToScreen();
        Invalidate();
    }

    void FitShellToScreen()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(FitShellToScreen); return; }
        MinimumSize = new Size(S(280), ShellHeight(S(280)));
        int availableHeight = Screen.FromControl(this).WorkingArea.Height - 32;
        int width = ClientSize.Width;
        if (ShellHeight(width) > availableHeight)
            width = Math.Max(MinimumSize.Width, SidePad * 2 + (int)((availableHeight - TopBarH - BottomBarH) * ScreenAspect));
        ClientSize = new Size(width, ShellHeight(width));
        LayoutViewport();
    }

    async Task PollTelemetryAsync()
    {
        if (_closing) return;
        UpdatePhoneAudio();

        if (_viewMode == StudioViewMode.IphoneOnly)
        {
            if (!_iphoneController.WdaConnected)
            {
                bool connected = await _iphoneController.ConnectWdaAsync();
                if (connected && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        _iphonePanel.MjpegHost = _iphoneController.MjpegHost;
                        _iphonePanel.WdaConnected = true;
                        _iphonePanel.StartMjpegStream();
                        _iphonePanel.StatusText = "Connected";
                        Invalidate();
                    });
                }
            }
            else if (!_iphonePanel.IsStreamingActively)
            {
                bool healthy = await _iphoneController.EnsureSessionHealthyAsync();
                if (healthy && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        if (!string.Equals(_iphonePanel.MjpegHost, _iphoneController.MjpegHost, StringComparison.OrdinalIgnoreCase) ||
                            !_iphonePanel.IsStreamingActively)
                        {
                            _iphonePanel.MjpegHost = _iphoneController.MjpegHost;
                            _iphonePanel.WdaConnected = true;
                            _iphonePanel.StartMjpegStream();
                        }
                    });
                }
            }
        }

        if (_adb.Serial is null) return;
        var t = await _adb.GetTelemetryAsync();
        if (t is null || IsDisposed) return;

        await _androidGovernor.InspectTelemetryAsync(t);

        if (t.MemAvailableMb > 0 && t.MemAvailableMb < 340 && !_boostBusy)
        {
            _boostBusy = true;
            try { await _androidGovernor.BoostRamAsync(t.ForegroundPackage); }
            finally { _boostBusy = false; }
        }
    }

    void EnableDragAndDrop(Control target, bool isIphone)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        target.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                return;

            if (!isIphone)
            {
                foreach (var file in files)
                    await _adb.PushFileToDownloadsAsync(file);
            }
            else
            {
                foreach (var file in files)
                {
                    if (File.Exists(file))
                        _iphoneService.ShareFileToIphone(file);
                }
            }
        };
    }

    NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open WAI Link", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("iPhone", null, (_, _) => SetViewMode(StudioViewMode.IphoneOnly));
        menu.Items.Add("Android", null, (_, _) => SetViewMode(StudioViewMode.AndroidOnly));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _closing = true; Close(); });

        return new NotifyIcon
        {
            Text = "WAI Link",
            Icon = Icon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
    }

    async Task WatchdogTickAsync()
    {
        if (_connecting || _closing) return;

        if (_scrcpy is { HasExited: false } && _scrcpyHwnd != 0)
        {
            if (!_streamVisible)
            {
                _streamVisible = true;
                _androidPanel.IsConnected = true;
                LayoutViewport();
                Invalidate();
            }
            return;
        }

        if (_streamVisible)
        {
            _streamVisible = false;
            _scrcpyHwnd = 0;
            _androidPanel.IsConnected = false;
            Invalidate();
        }

        if (_viewMode == StudioViewMode.AndroidOnly)
            await EnsureConnectedAsync();
    }

    async Task EnsureConnectedAsync()
    {
        if (_connecting || _closing) return;
        _connecting = true;
        try
        {
            bool ready = await _adb.IsDeviceReadyAsync(null, CancellationToken.None);
            if (!ready || _adb.Serial is null)
            {
                _androidPanel.IsConnected = false;
                _androidPanel.StatusText = "Plug in your phone with a USB cable";
                Invalidate();
                return;
            }

            _androidDeviceName = !string.IsNullOrWhiteSpace(_adb.Model) ? _adb.Model : "Android";
            _androidPanel.DeviceName = _androidDeviceName;
            _androidPanel.IsConnected = true;
            _androidPanel.StatusText = "Starting screen stream...";
            Invalidate();

            if (ShellStyle.AndroidTweaks) // changes the phone's own settings, so only on request
                _ = Task.Run(async () => await _androidGovernor.ApplyStealthSessionTuningAsync());

            await StartScrcpy(_adb.Serial);
        }
        finally
        {
            _connecting = false;
        }
    }

    async Task StartScrcpy(string serial)
    {
        KillScrcpy();
        if (!File.Exists(ScrcpyExe))
        {
            _androidPanel.StatusText = "Renderer missing in lib folder";
            return;
        }

        string title = $"WAILinkStream_{Environment.ProcessId}";
        var args = new List<string>
        {
            $"-s {serial}",
            $"--window-title={title}",
            "--window-borderless",
            "--window-x=-32000",
            "--window-y=-32000",
            "--window-width=380",
            "--window-height=740",
            "--stay-awake",
            "--max-fps=60",
            "--video-bit-rate=8M",
            "--max-size=1280"
        };

        var psi = new ProcessStartInfo(ScrcpyExe, string.Join(' ', args))
        {
            WorkingDirectory = ScrcpyDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        string serverFile = Path.Combine(ScrcpyDir, "wailink-server");
        if (File.Exists(serverFile))
            psi.EnvironmentVariables["SCRCPY_SERVER_PATH"] = serverFile;
        if (File.Exists(AdbExe))
            psi.EnvironmentVariables["ADB"] = AdbExe;

        _scrcpy = Process.Start(psi);

        if (_scrcpy is null) return;
        IphoneController.AssignToChildJob(_scrcpy);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 7000 && !_scrcpy.HasExited)
        {
            await Task.Delay(35);
            _scrcpy.Refresh();
            if (_scrcpy.MainWindowHandle != 0)
            {
                _scrcpyHwnd = _scrcpy.MainWindowHandle;
                break;
            }
        }

        if (_scrcpyHwnd == 0) return;

        var style = GetWindowLongPtr(_scrcpyHwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_POPUP);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(_scrcpyHwnd, GWL_STYLE, style);

        SetParent(_scrcpyHwnd, _androidPanel.Handle);
        MoveWindow(_scrcpyHwnd, 0, 0, _androidPanel.Width, _androidPanel.Height, true);
        ShowWindow(_scrcpyHwnd, SW_SHOW);

        _streamVisible = true;
        _androidPanel.IsConnected = true;
        _ = SetAndroidRotationAsync(_landscape); // the phone turns only with the rotate button
        LayoutViewport();
        if (_viewMode == StudioViewMode.AndroidOnly)
            FocusActiveSurface();
        Invalidate();
    }

    void KillScrcpy()
    {
        try
        {
            if (_scrcpy is { HasExited: false })
            {
                if (_scrcpyHwnd != 0)
                {
                    PostMessage(_scrcpyHwnd, WM_CLOSE, 0, 0);
                    if (!_scrcpy.WaitForExit(350))
                        _scrcpy.Kill(true);
                }
                else
                {
                    _scrcpy.Kill(true);
                }
            }
        }
        catch { }
        _scrcpy = null;
        _scrcpyHwnd = 0;
        _streamVisible = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        ReleaseIphoneContact(_iphonePanel);
        _iphoneController.ClipboardTextChanged -= OnIphoneClipboardTextChanged;
        StopPresentation();
        _deviceScan?.Stop();
        UpdatePhoneAudio(); // _closing: stops both sound routes
        RestoreAndroidRotation(wait: true);
        _watchdog.Stop();
        _telemetryTimer.Stop();
        try { _iphonePanel.StopMjpegStream(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _agentServer.Dispose(); } catch { }
        try { _iphoneService.Dispose(); } catch { }
        try { _iphoneController.Dispose(); } catch { }
        KillScrcpy();
        base.OnFormClosing(e);
    }
}

/// <summary>
/// 03 / DEVICE PICKER popover ("SWITCH DEVICE": iPhone ✓ / Android).
/// </summary>
public sealed class DevicePickerPopover : Control
{
    StudioViewMode _selectedMode = StudioViewMode.None;
    int _hoverRow = -1;
    Rectangle _row0Rect;
    Rectangle _row1Rect;

    public StudioViewMode SelectedMode
    {
        get => _selectedMode;
        set { _selectedMode = value; Invalidate(); }
    }

    public event Action<StudioViewMode>? OnModeSelected;

    public DevicePickerPopover()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(28, 33, 43);
        Cursor = Cursors.Hand;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width > 10 && Height > 10)
        {
            using var path = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), 12);
            Region = new Region(path);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int r = -1;
        if (_row0Rect.Contains(e.Location)) r = 0;
        else if (_row1Rect.Contains(e.Location)) r = 1;
        if (r != _hoverRow)
        {
            _hoverRow = r;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != -1)
        {
            _hoverRow = -1;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_row0Rect.Contains(e.Location))
            OnModeSelected?.Invoke(StudioViewMode.IphoneOnly);
        else if (_row1Rect.Contains(e.Location))
            OnModeSelected?.Invoke(StudioViewMode.AndroidOnly);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var bgPath = Theme.RoundedRect(rect, 12);
        using var bgBrush = new SolidBrush(Color.FromArgb(28, 33, 43));
        using var borderPen = new Pen(Color.FromArgb(68, 78, 96), 1.1f);
        g.FillPath(bgBrush, bgPath);
        g.DrawPath(borderPen, bgPath);

        using var hdrFont = Theme.Font(6.75f, FontStyle.Bold);
        using var hdrBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        g.DrawString("SWITCH DEVICE", hdrFont, hdrBrush, new PointF(12, 9));

        _row0Rect = new Rectangle(6, 28, Width - 12, 28);
        _row1Rect = new Rectangle(6, 59, Width - 12, 28);

        DrawOptionRow(g, _row0Rect, isIphone: true, "iPhone", _selectedMode == StudioViewMode.IphoneOnly, _hoverRow == 0);
        DrawOptionRow(g, _row1Rect, isIphone: false, "Android", _selectedMode == StudioViewMode.AndroidOnly, _hoverRow == 1);
    }

    static void DrawOptionRow(Graphics g, Rectangle rect, bool isIphone, string label, bool selected, bool hover)
    {
        if (selected || hover)
        {
            using var rowPath = Theme.RoundedRect(rect, 8);
            Color fillCol = selected
                ? Color.FromArgb(72, 206, 154, 106)
                : Color.FromArgb(36, 206, 154, 106);
            using var fillBrush = new SolidBrush(fillCol);
            g.FillPath(fillBrush, rowPath);
        }

        Color fg = selected
            ? Color.FromArgb(248, 224, 198)
            : (hover ? Color.White : Color.FromArgb(203, 213, 225));

        int ix = rect.X + 10;
        int iy = rect.Y + 7;
        if (isIphone)
            MainForm.DrawIphoneGlyph(g, ix, iy, fg);
        else
            MainForm.DrawAndroidGlyph(g, ix, iy, fg);

        using var font = Theme.Font(8.5f, selected ? FontStyle.Bold : FontStyle.Regular);
        using var textBrush = new SolidBrush(fg);
        using var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, textBrush, new RectangleF(ix + 16, rect.Y, rect.Width - 48, rect.Height), fmt);

        if (selected)
        {
            using var checkPen = new Pen(Color.FromArgb(236, 186, 138), 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int cx = rect.Right - 16;
            int cy = rect.Y + rect.Height / 2;
            g.DrawLines(checkPen, [new Point(cx - 4, cy), new Point(cx - 1, cy + 3), new Point(cx + 5, cy - 3)]);
        }
    }
}

/// <summary>
/// Neutral startup device chooser so no iPhone or Android engine starts until the user chooses.
/// </summary>
public sealed class DeviceChooserView : Panel
{
    Rectangle _iphoneBtnRect;
    Rectangle _androidBtnRect;
    int _hoverBtn = -1;

    public event Action<StudioViewMode>? OnDeviceChosen;

    public DeviceChooserView()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 12, 16);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = -1;
        if (_iphoneBtnRect.Contains(e.Location)) h = 0;
        else if (_androidBtnRect.Contains(e.Location)) h = 1;
        if (h != _hoverBtn)
        {
            _hoverBtn = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBtn != -1)
        {
            _hoverBtn = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_iphoneBtnRect.Contains(e.Location))
            OnDeviceChosen?.Invoke(StudioViewMode.IphoneOnly);
        else if (_androidBtnRect.Contains(e.Location))
            OnDeviceChosen?.Invoke(StudioViewMode.AndroidOnly);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width < 40 || rect.Height < 40) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var bgBrush = new LinearGradientBrush(
            rect,
            Color.FromArgb(16, 19, 26),
            Color.FromArgb(10, 12, 17),
            LinearGradientMode.Vertical);
        g.FillRectangle(bgBrush, ClientRectangle);

        using var centerFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        int cy = rect.Height / 2 - 58;

        using var titleFont = Theme.Font(13f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        g.DrawString("Choose a Device", titleFont, titleBrush, new RectangleF(rect.X + 20, cy - 16, rect.Width - 40, 28), centerFormat);

        using var subFont = Theme.Font(8.75f);
        using var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        g.DrawString("Pick which phone to connect and start its engine", subFont, subBrush, new RectangleF(rect.X + 24, cy + 14, rect.Width - 48, 34), centerFormat);

        int btnW = Math.Min(210, rect.Width - 56);
        int btnX = rect.X + (rect.Width - btnW) / 2;

        _iphoneBtnRect = new Rectangle(btnX, cy + 62, btnW, 42);
        _androidBtnRect = new Rectangle(btnX, cy + 114, btnW, 42);

        DrawChoiceButton(g, _iphoneBtnRect, isIphone: true, "iPhone", _hoverBtn == 0);
        DrawChoiceButton(g, _androidBtnRect, isIphone: false, "Android", _hoverBtn == 1);
    }

    static void DrawChoiceButton(Graphics g, Rectangle bounds, bool isIphone, string label, bool hover)
    {
        using var path = Theme.RoundedRect(bounds, 11);
        Color fill = hover ? Color.FromArgb(90, 214, 166, 120) : Color.FromArgb(42, 214, 166, 120);
        Color stroke = hover ? Color.FromArgb(190, 236, 186, 138) : Color.FromArgb(110, 148, 163, 184);
        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(stroke, 1.1f);
        g.FillPath(fillBrush, path);
        g.DrawPath(borderPen, path);

        Color fg = hover ? Color.FromArgb(248, 224, 198) : Color.FromArgb(226, 232, 240);
        int iconX = bounds.X + bounds.Width / 2 - 34;
        int iconY = bounds.Y + (bounds.Height - 14) / 2;
        if (isIphone)
            MainForm.DrawIphoneGlyph(g, iconX, iconY, fg);
        else
            MainForm.DrawAndroidGlyph(g, iconX, iconY, fg);

        using var font = Theme.Font(9.5f, FontStyle.Bold);
        using var textBrush = new SolidBrush(fg);
        using var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, textBrush, new RectangleF(iconX + 18, bounds.Y, 90, bounds.Height), fmt);
    }
}
