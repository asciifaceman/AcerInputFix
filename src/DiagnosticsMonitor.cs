using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

/// <summary>
/// Optional Layer-2 diagnostics. Does not alter B0 quarantine/suppression.
/// Logs GetAsyncKeyState transitions, shell HSHELL_APPCOMMAND, and
/// Consumer Control Raw Input.
/// </summary>
internal sealed class DiagnosticsMonitor : IDisposable
{
    private const int PollIntervalMs = 40;

    private const int WM_INPUT = 0x00FF;

    private const int HSHELL_APPCOMMAND = 12;

    private const int RID_INPUT = 0x10000003;
    private const int RIM_TYPEHID = 2;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RIDEV_REMOVE = 0x00000001;

    private const uint FAPPCOMMAND_MASK = 0xF000;
    private const uint FAPPCOMMAND_KEY = 0x0000;
    private const uint FAPPCOMMAND_MOUSE = 0x8000;
    private const uint FAPPCOMMAND_OEM = 0x1000;

    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;
    private const int VK_MEDIA_NEXT_TRACK = 0xB0;
    private const int VK_MEDIA_PREV_TRACK = 0xB1;
    private const int VK_MEDIA_STOP = 0xB2;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private static readonly (int Vk, string Name)[] WatchedKeys =
    [
        (VK_VOLUME_MUTE, "VK_VOLUME_MUTE"),
        (VK_VOLUME_DOWN, "VK_VOLUME_DOWN"),
        (VK_VOLUME_UP, "VK_VOLUME_UP"),
        (VK_MEDIA_NEXT_TRACK, "VK_MEDIA_NEXT_TRACK"),
        (VK_MEDIA_PREV_TRACK, "VK_MEDIA_PREV_TRACK"),
        (VK_MEDIA_STOP, "VK_MEDIA_STOP"),
        (VK_MEDIA_PLAY_PAUSE, "VK_MEDIA_PLAY_PAUSE"),
        (VK_LSHIFT, "VK_LSHIFT"),
        (VK_RSHIFT, "VK_RSHIFT"),
        (VK_LCONTROL, "VK_LCONTROL"),
        (VK_RCONTROL, "VK_RCONTROL"),
        (VK_LMENU, "VK_LMENU"),
        (VK_RMENU, "VK_RMENU"),
        (VK_LWIN, "VK_LWIN"),
        (VK_RWIN, "VK_RWIN"),
    ];

    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly Dictionary<int, bool> _keyDown = new();

    private MessageWindow? _window;
    private System.Windows.Forms.Timer? _pollTimer;
    private bool _enabled;
    private bool _rawInputRegistered;
    private bool _shellHookRegistered;
    private int _shellHookMsg;
    private bool _disposed;

    public DiagnosticsMonitor(Action<string> log)
    {
        _log = log;

        foreach (var (vk, _) in WatchedKeys)
            _keyDown[vk] = false;
    }

    public bool Enabled
    {
        get
        {
            lock (_lock)
                return _enabled;
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_enabled == enabled)
                return;

            _enabled = enabled;

            if (enabled)
                StartUnlocked();
            else
                StopUnlocked();
        }
    }

    public void Mark(string label)
    {
        // Manual markers must always hit the log so broken/recovered/cycle
        // breadcrumbs correlate even when Layer-2 streaming diagnostics are off.
        _log($"DIAG MARK: {label}");
    }

    private void StartUnlocked()
    {
        _window ??= new MessageWindow(OnWindowMessage);
        _window.EnsureCreated();

        if (_shellHookMsg == 0)
        {
            _shellHookMsg = RegisterWindowMessage("SHELLHOOK");
            if (_shellHookMsg == 0)
            {
                int error = Marshal.GetLastWin32Error();
                _log(
                    $"DIAG ERROR: RegisterWindowMessage(SHELLHOOK) " +
                    $"failed. Win32 error {error}."
                );
            }
        }

        if (!_shellHookRegistered && _shellHookMsg != 0)
        {
            if (RegisterShellHookWindow(_window.Handle))
            {
                _shellHookRegistered = true;
                _log(
                    "DIAG: Registered shell hook window for " +
                    "HSHELL_APPCOMMAND."
                );
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                _log(
                    $"DIAG ERROR: RegisterShellHookWindow failed. " +
                    $"Win32 error {error}."
                );
            }
        }

        if (!_rawInputRegistered)
        {
            if (TryRegisterConsumerRawInput(_window.Handle, remove: false))
            {
                _rawInputRegistered = true;
                _log(
                    "DIAG: Registered Raw Input for Consumer Control " +
                    "(UsagePage=0x0C Usage=0x01, RIDEV_INPUTSINK)."
                );
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                _log(
                    $"DIAG ERROR: RegisterRawInputDevices failed. " +
                    $"Win32 error {error}."
                );
            }
        }

        SnapshotKeyState(logSnapshot: true);

        _pollTimer ??= new System.Windows.Forms.Timer
        {
            Interval = PollIntervalMs
        };

        _pollTimer.Tick -= PollTimerOnTick;
        _pollTimer.Tick += PollTimerOnTick;
        _pollTimer.Start();

        _log(
            "DIAG: Enabled " +
            "(GetAsyncKeyState + HSHELL_APPCOMMAND + Consumer Raw Input). " +
            "B0 suppression behavior unchanged."
        );
    }

    private void StopUnlocked()
    {
        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimerOnTick;
        }

        if (_shellHookRegistered && _window is not null)
        {
            DeregisterShellHookWindow(_window.Handle);
            _shellHookRegistered = false;
            _log("DIAG: Unregistered shell hook window.");
        }

        if (_rawInputRegistered && _window is not null)
        {
            TryRegisterConsumerRawInput(_window.Handle, remove: true);
            _rawInputRegistered = false;
            _log("DIAG: Unregistered Consumer Control Raw Input.");
        }

        _log("DIAG: Disabled.");
    }

    private void PollTimerOnTick(object? sender, EventArgs e)
    {
        if (!Enabled)
            return;

        foreach (var (vk, name) in WatchedKeys)
        {
            short state = GetAsyncKeyState(vk);
            bool down = (state & 0x8000) != 0;

            bool wasDown;
            lock (_lock)
            {
                wasDown = _keyDown[vk];
                if (wasDown == down)
                    continue;

                _keyDown[vk] = down;
            }

            string transition = down ? "DOWN" : "UP";
            _log(
                $"DIAG KEYSTATE: {name} (0x{vk:X2}) {transition} " +
                $"(GetAsyncKeyState=0x{(ushort)state:X4})"
            );
        }
    }

    private void SnapshotKeyState(bool logSnapshot)
    {
        var parts = new List<string>();

        foreach (var (vk, name) in WatchedKeys)
        {
            short state = GetAsyncKeyState(vk);
            bool down = (state & 0x8000) != 0;
            _keyDown[vk] = down;

            if (down)
                parts.Add($"{name}=DOWN");
        }

        if (!logSnapshot)
            return;

        if (parts.Count == 0)
            _log("DIAG KEYSTATE snapshot: (none of watched keys DOWN)");
        else
            _log($"DIAG KEYSTATE snapshot: {string.Join(", ", parts)}");
    }

    private void OnWindowMessage(ref Message m)
    {
        if (!Enabled)
            return;

        if (_shellHookMsg != 0 && m.Msg == _shellHookMsg)
        {
            if (m.WParam.ToInt32() == HSHELL_APPCOMMAND)
                LogAppCommandFromShell(m.LParam);

            return;
        }

        if (m.Msg == WM_INPUT)
            LogRawInput(m);
    }

    private void LogAppCommandFromShell(IntPtr lParamValue)
    {
        // Shell HSHELL_APPCOMMAND packs the same APPCOMMAND lParam layout
        // used by WM_APPCOMMAND (cmd + device + key state).
        long lParam = lParamValue.ToInt64();
        int hiWord = (int)((lParam >> 16) & 0xFFFF);
        int keyState = (int)(lParam & 0xFFFF);

        int cmd = hiWord & ~((int)FAPPCOMMAND_MASK);
        uint device = (uint)hiWord & FAPPCOMMAND_MASK;

        string deviceName = device switch
        {
            FAPPCOMMAND_KEY => "KEY",
            FAPPCOMMAND_MOUSE => "MOUSE",
            FAPPCOMMAND_OEM => "OEM",
            _ => $"0x{device:X4}"
        };

        _log(
            $"DIAG APPCOMMAND: cmd={cmd} ({DescribeAppCommand(cmd)}) " +
            $"device={deviceName} keyState=0x{keyState:X4} " +
            $"(via HSHELL_APPCOMMAND)"
        );
    }

    private void LogRawInput(Message m)
    {
        IntPtr hRawInput = m.LParam;

        uint needed = 0;
        int result = GetRawInputData(
            hRawInput,
            RID_INPUT,
            IntPtr.Zero,
            ref needed,
            (uint)Marshal.SizeOf<RAWINPUTHEADER>()
        );

        if (result == -1 || needed == 0)
            return;

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);

        try
        {
            uint size = needed;
            result = GetRawInputData(
                hRawInput,
                RID_INPUT,
                buffer,
                ref size,
                (uint)Marshal.SizeOf<RAWINPUTHEADER>()
            );

            if (result == -1)
            {
                int error = Marshal.GetLastWin32Error();
                _log(
                    $"DIAG ERROR: GetRawInputData failed. " +
                    $"Win32 error {error}."
                );
                return;
            }

            var header =
                Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

            if (header.dwType != RIM_TYPEHID)
                return;

            // RAWHID follows the header. On 64-bit, header is 24 bytes.
            int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
            var hid = Marshal.PtrToStructure<RAWHID>(
                IntPtr.Add(buffer, headerSize)
            );

            IntPtr dataPtr = IntPtr.Add(
                buffer,
                headerSize + Marshal.SizeOf<RAWHID>()
            );

            int totalBytes = (int)(hid.dwSizeHid * hid.dwCount);
            if (totalBytes < 0 || totalBytes > 4096)
            {
                _log(
                    $"DIAG RAWINPUT: HID device=0x{header.hDevice.ToInt64():X} " +
                    $"sizeHid={hid.dwSizeHid} count={hid.dwCount} " +
                    "(unexpected size; skipped dump)"
                );
                return;
            }

            byte[] bytes = new byte[totalBytes];
            if (totalBytes > 0)
                Marshal.Copy(dataPtr, bytes, 0, totalBytes);

            string hex = FormatHex(bytes);
            string decoded = TryDecodeConsumerReport(bytes);
            string deviceName = DescribeRawInputDevice(header.hDevice);

            _log(
                $"DIAG RAWINPUT: HID device=0x{header.hDevice.ToInt64():X} " +
                $"[{deviceName}] sizeHid={hid.dwSizeHid} count={hid.dwCount} " +
                $"data=[{hex}]{decoded}"
            );
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string TryDecodeConsumerReport(byte[] bytes)
    {
        // Col07 format observed: Report ID 0x33, then little-endian 16-bit usage.
        if (bytes.Length >= 3 && bytes[0] == 0x33)
        {
            ushort usage = (ushort)(bytes[1] | (bytes[2] << 8));
            string usageName = usage switch
            {
                0x0000 => "neutral",
                0x00E2 => "Mute",
                0x00E9 => "VolumeIncrement",
                0x00EA => "VolumeDecrement",
                0x00B5 => "ScanNextTrack",
                0x00B6 => "ScanPreviousTrack",
                0x00CD => "PlayPause",
                0x006F => "BrightnessIncrement",
                0x0070 => "BrightnessDecrement",
                _ => $"Usage_0x{usage:X4}"
            };

            return $" consumer(reportId=0x33,usage=0x{usage:X4}/{usageName})";
        }

        return "";
    }

    private static string DescribeRawInputDevice(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(
            hDevice,
            RIDI_DEVICENAME,
            IntPtr.Zero,
            ref size
        );

        if (size == 0)
            return "name=?";

        IntPtr nameBuf = Marshal.AllocHGlobal((int)(size * 2));

        try
        {
            uint nameSize = size;
            int got = GetRawInputDeviceInfo(
                hDevice,
                RIDI_DEVICENAME,
                nameBuf,
                ref nameSize
            );

            if (got < 0)
                return "name=?";

            string? name = Marshal.PtrToStringUni(nameBuf);
            return string.IsNullOrEmpty(name) ? "name=?" : name;
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuf);
        }
    }

    private static string FormatHex(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";

        var sb = new StringBuilder(bytes.Length * 3);

        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');

            sb.Append(bytes[i].ToString("X2"));
        }

        return sb.ToString();
    }

    // Values from winuser.h — earlier drafts had media/volume numbers swapped.
    private static string DescribeAppCommand(int cmd) =>
        cmd switch
        {
            1 => "APPCOMMAND_BROWSER_BACKWARD",
            2 => "APPCOMMAND_BROWSER_FORWARD",
            3 => "APPCOMMAND_BROWSER_REFRESH",
            4 => "APPCOMMAND_BROWSER_STOP",
            5 => "APPCOMMAND_BROWSER_SEARCH",
            6 => "APPCOMMAND_BROWSER_FAVORITES",
            7 => "APPCOMMAND_BROWSER_HOME",
            8 => "APPCOMMAND_VOLUME_MUTE",
            9 => "APPCOMMAND_VOLUME_DOWN",
            10 => "APPCOMMAND_VOLUME_UP",
            11 => "APPCOMMAND_MEDIA_NEXTTRACK",
            12 => "APPCOMMAND_MEDIA_PREVIOUSTRACK",
            13 => "APPCOMMAND_MEDIA_STOP",
            14 => "APPCOMMAND_MEDIA_PLAY_PAUSE",
            15 => "APPCOMMAND_LAUNCH_MAIL",
            16 => "APPCOMMAND_LAUNCH_MEDIA_SELECT",
            17 => "APPCOMMAND_LAUNCH_APP1",
            18 => "APPCOMMAND_LAUNCH_APP2",
            46 => "APPCOMMAND_MEDIA_PLAY",
            47 => "APPCOMMAND_MEDIA_PAUSE",
            48 => "APPCOMMAND_MEDIA_RECORD",
            49 => "APPCOMMAND_MEDIA_FAST_FORWARD",
            50 => "APPCOMMAND_MEDIA_REWIND",
            51 => "APPCOMMAND_MEDIA_CHANNEL_UP",
            52 => "APPCOMMAND_MEDIA_CHANNEL_DOWN",
            _ => $"APPCOMMAND_{cmd}"
        };

    private static bool TryRegisterConsumerRawInput(
        IntPtr hwnd,
        bool remove)
    {
        var devices = new RAWINPUTDEVICE[1];
        devices[0] = new RAWINPUTDEVICE
        {
            usUsagePage = 0x0C,
            usUsage = 0x01,
            dwFlags = remove
                ? RIDEV_REMOVE
                : RIDEV_INPUTSINK,
            hwndTarget = remove ? IntPtr.Zero : hwnd
        };

        return RegisterRawInputDevices(
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<RAWINPUTDEVICE>()
        );
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_enabled)
                StopUnlocked();

            _pollTimer?.Dispose();
            _pollTimer = null;

            _window?.DestroyHandle();
            _window = null;

            _disposed = true;
        }
    }

    private delegate void WindowMessageHandler(ref Message m);

    private sealed class MessageWindow : NativeWindow
    {
        private readonly WindowMessageHandler _handler;

        public MessageWindow(WindowMessageHandler handler)
        {
            _handler = handler;
        }

        public void EnsureCreated()
        {
            if (Handle != IntPtr.Zero)
                return;

            // Hidden top-level window (not HWND_MESSAGE): RegisterShellHookWindow
            // and RIDEV_INPUTSINK are more reliable on a real top-level HWND.
            CreateHandle(
                new CreateParams
                {
                    Caption = "AcerInputFix.Diagnostics",
                    ClassName = null,
                    Style = unchecked((int)0x80000000), // WS_POPUP
                    ExStyle = 0x08000080, // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                    X = 0,
                    Y = 0,
                    Width = 0,
                    Height = 0
                }
            );
        }

        protected override void WndProc(ref Message m)
        {
            _handler(ref m);
            base.WndProc(ref m);
        }
    }

    private const uint RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public int dwSizeHid;
        public int dwCount;
        // Followed by bRawData[1] inline; read via pointer arithmetic.
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DeregisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputData(
        IntPtr hRawInput,
        int uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader
    );

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern int GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize
    );
}
