using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        _mutex = new Mutex(
            true,
            @"Local\AcerInputFix",
            out bool firstInstance
        );

        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    // From our captured failure:
    //
    // VK_MEDIA_NEXT_TRACK receives DOWN without UP.
    // Windows begins repeating it after its normal typematic delay.
    //
    // Normal taps should release well before this.
    private const int StuckTimeoutMs = 350;

    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint VK_MEDIA_NEXT_TRACK = 0xB0;

    private const uint LLKHF_INJECTED = 0x10;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Lets our hook distinguish our own corrective input.
    private static readonly UIntPtr OurExtraInfo =
        new UIntPtr(0x41434658); // "ACFX"

    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _runRepairsItem;
    private readonly ToolStripMenuItem _totalRepairsItem;
    private readonly ToolStripMenuItem _lastRepairItem;
    private readonly ToolStripMenuItem _enabledItem;

    private readonly object _stateLock = new();
    private readonly object _logLock = new();

    private readonly System.Threading.Timer _stuckTimer;

    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook = IntPtr.Zero;

    private bool _nextTrackPending;
    private bool _suppressBogusNextTrack;
    private bool _enabled = true;

    private long _runRepairs;
    private Stats _stats;

    private readonly string _baseDir;
    private readonly string _logPath;
    private readonly string _statsPath;

    public TrayContext()
    {
        _baseDir = AppContext.BaseDirectory;
        _logPath = Path.Combine(_baseDir, "AcerInputFix.log");
        _statsPath = Path.Combine(_baseDir, "stats.json");

        _stats = LoadStats();

        RotateLogIfNeeded();
        Log("AcerInputFix starting.");
        Log(
            $"Interop sizes: INPUT={Marshal.SizeOf<INPUT>()}, " +
            $"KEYBDINPUT={Marshal.SizeOf<KEYBDINPUT>()}"
        );

        _stuckTimer = new System.Threading.Timer(
            StuckTimerExpired,
            null,
            Timeout.Infinite,
            Timeout.Infinite
        );

        _statusItem = new ToolStripMenuItem("Status: Active")
        {
            Enabled = false
        };

        _runRepairsItem = new ToolStripMenuItem("Repairs this run: 0")
        {
            Enabled = false
        };

        _totalRepairsItem = new ToolStripMenuItem(
            $"Lifetime repairs: {_stats.TotalRepairs}"
        )
        {
            Enabled = false
        };

        _lastRepairItem = new ToolStripMenuItem(
            FormatLastRepair()
        )
        {
            Enabled = false
        };

        _enabledItem = new ToolStripMenuItem("Enabled")
        {
            Checked = true,
            CheckOnClick = true
        };

        _enabledItem.CheckedChanged += (_, _) =>
        {
            _enabled = _enabledItem.Checked;

            if (!_enabled)
            {
                lock (_stateLock)
                {
                    _nextTrackPending = false;
                    _suppressBogusNextTrack = false;
                    _stuckTimer.Change(
                        Timeout.Infinite,
                        Timeout.Infinite
                    );
                }

                Log("Fix paused by user.");
            }
            else
            {
                Log("Fix enabled by user.");
            }

            RefreshMenu();
        };

        var openLogItem = new ToolStripMenuItem("Open log");
        openLogItem.Click += (_, _) =>
        {
            EnsureLogExists();

            Process.Start(new ProcessStartInfo
            {
                FileName = _logPath,
                UseShellExecute = true
            });
        };

        var openFolderItem = new ToolStripMenuItem("Open folder");
        openFolderItem.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _baseDir,
                UseShellExecute = true
            });
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitFix();

        _menu = new ContextMenuStrip();

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_runRepairsItem);
        _menu.Items.Add(_totalRepairsItem);
        _menu.Items.Add(_lastRepairItem);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(_enabledItem);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(openLogItem);
        _menu.Items.Add(openFolderItem);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(exitItem);

        _menu.Opening += (_, _) => RefreshMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Acer Input Fix - Active",
            ContextMenuStrip = _menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) =>
        {
            _menu.Show(Cursor.Position);
        };

        InstallHook();

        RefreshMenu();
    }

    private void InstallHook()
    {
        _hookProc = HookCallback;

        using Process process = Process.GetCurrentProcess();

        IntPtr module = GetModuleHandle(null);

        _hook = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _hookProc,
            module,
            0
        );

        if (_hook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();

            Log($"ERROR: SetWindowsHookEx failed: {error}");

            MessageBox.Show(
                $"AcerInputFix could not install its keyboard hook.\n\n" +
                $"Win32 error: {error}",
                "AcerInputFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );

            ExitFix();
            return;
        }

        Log("Low-level keyboard hook installed.");
    }

    private IntPtr HookCallback(
        int nCode,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(
                _hook,
                nCode,
                wParam,
                lParam
            );
        }

        var data =
            Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Always allow the corrective KEYUP generated by this app.
        if (data.dwExtraInfo == OurExtraInfo)
        {
            return CallNextHookEx(
                _hook,
                nCode,
                wParam,
                lParam
            );
        }

        bool isInjected =
            (data.flags & LLKHF_INJECTED) != 0;

        bool isNextTrack =
            data.vkCode == VK_MEDIA_NEXT_TRACK;

        int msg = wParam.ToInt32();

        bool isDown =
            msg == WM_KEYDOWN ||
            msg == WM_SYSKEYDOWN;

        bool isUp =
            msg == WM_KEYUP ||
            msg == WM_SYSKEYUP;

        if (_enabled &&
            isInjected &&
            isNextTrack)
        {
            lock (_stateLock)
            {
                // Once the watchdog has identified a stuck B0, do not
                // allow the continuing injected repeat stream to reach
                // applications. The timer sends one synthetic KEYUP to
                // release the original DOWN that already got through.
                if (_suppressBogusNextTrack)
                {
                    if (isUp)
                    {
                        _suppressBogusNextTrack = false;
                        _nextTrackPending = false;

                        Log(
                            "Bogus VK_MEDIA_NEXT_TRACK stream ended; " +
                            "suppression cleared."
                        );
                    }

                    return new IntPtr(1);
                }
            }

            if (isDown)
            {
                HandleNextTrackDown();
            }
            else if (isUp)
            {
                HandleNextTrackUp();
            }
        }

        return CallNextHookEx(
            _hook,
            nCode,
            wParam,
            lParam
        );
    }

    private void HandleNextTrackDown()
    {
        lock (_stateLock)
        {
            // First DOWN starts the watchdog.
            //
            // Repeated DOWN events do not extend the timer. If it
            // really is stuck, we want to release based on the
            // original DOWN.
            if (!_nextTrackPending)
            {
                _nextTrackPending = true;

                _stuckTimer.Change(
                    StuckTimeoutMs,
                    Timeout.Infinite
                );
            }
        }
    }

    private void HandleNextTrackUp()
    {
        lock (_stateLock)
        {
            if (!_nextTrackPending)
                return;

            _nextTrackPending = false;

            _stuckTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite
            );
        }
    }

    private void StuckTimerExpired(object? state)
    {
        if (!_enabled)
            return;

        lock (_stateLock)
        {
            if (!_nextTrackPending ||
                _suppressBogusNextTrack)
            {
                return;
            }

            // The initial B0 DOWN has been held longer than a normal tap.
            // Mark the stream as bogus before generating the release so
            // every later injected B0 repeat will be swallowed.
            _suppressBogusNextTrack = true;
            _nextTrackPending = false;
        }

        if (SendNextTrackKeyUp())
        {
            long runCount =
                Interlocked.Increment(ref _runRepairs);

            lock (_stateLock)
            {
                _stats.TotalRepairs++;
                _stats.LastRepair = DateTimeOffset.Now;

                SaveStats();
            }

            Log(
                $"REPAIR #{runCount}: " +
                $"VK_MEDIA_NEXT_TRACK remained down for " +
                $"{StuckTimeoutMs} ms; sent corrective KEYUP and " +
                $"started suppressing the bogus repeat stream."
            );
        }
        else
        {
            lock (_stateLock)
            {
                _suppressBogusNextTrack = false;
            }

            int error = Marshal.GetLastWin32Error();

            Log(
                $"ERROR: SendInput corrective KEYUP failed. " +
                $"Win32 error {error}."
            );
        }
    }

    private static bool SendNextTrackKeyUp()
    {
        INPUT[] inputs =
        [
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)VK_MEDIA_NEXT_TRACK,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = OurExtraInfo
                    }
                }
            }
        ];

        uint sent = SendInput(
            1,
            inputs,
            Marshal.SizeOf<INPUT>()
        );

        return sent == 1;
    }

    private void RefreshMenu()
    {
        _statusItem.Text =
            _enabled
                ? "Status: Active"
                : "Status: Paused";

        _runRepairsItem.Text =
            $"Repairs this run: " +
            $"{Interlocked.Read(ref _runRepairs)}";

        lock (_stateLock)
        {
            _totalRepairsItem.Text =
                $"Lifetime repairs: {_stats.TotalRepairs}";

            _lastRepairItem.Text =
                FormatLastRepair();
        }

        _trayIcon.Text =
            _enabled
                ? "Acer Input Fix - Active"
                : "Acer Input Fix - Paused";
    }

    private string FormatLastRepair()
    {
        if (_stats.LastRepair is null)
            return "Last repair: Never";

        return
            $"Last repair: " +
            $"{_stats.LastRepair.Value.LocalDateTime:g}";
    }

    private Stats LoadStats()
    {
        try
        {
            if (!File.Exists(_statsPath))
                return new Stats();

            string json =
                File.ReadAllText(_statsPath);

            return
                JsonSerializer.Deserialize<Stats>(json)
                ?? new Stats();
        }
        catch (Exception ex)
        {
            LogFallback(
                $"Could not read stats.json: {ex.Message}"
            );

            return new Stats();
        }
    }

    private void SaveStats()
    {
        try
        {
            string json =
                JsonSerializer.Serialize(
                    _stats,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                );

            File.WriteAllText(
                _statsPath,
                json
            );
        }
        catch (Exception ex)
        {
            Log(
                $"ERROR saving stats: {ex.Message}"
            );
        }
    }

    private void EnsureLogExists()
    {
        if (!File.Exists(_logPath))
            File.WriteAllText(_logPath, "");
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath))
                return;

            var info = new FileInfo(_logPath);

            if (info.Length < 2 * 1024 * 1024)
                return;

            string old =
                Path.Combine(
                    _baseDir,
                    "AcerInputFix.old.log"
                );

            if (File.Exists(old))
                File.Delete(old);

            File.Move(_logPath, old);
        }
        catch
        {
            // Logging should never prevent the fixer from starting.
        }
    }

    private void Log(string message)
    {
        lock (_logLock)
        {
            try
            {
                File.AppendAllText(
                    _logPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  " +
                    $"{message}{Environment.NewLine}"
                );
            }
            catch
            {
                // Never let logging break the workaround.
            }
        }
    }

    private void LogFallback(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "AcerInputFix.log"
                ),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  " +
                $"{message}{Environment.NewLine}"
            );
        }
        catch
        {
        }
    }

    private void ExitFix()
    {
        Log("AcerInputFix exiting.");

        lock (_stateLock)
        {
            _nextTrackPending = false;
            _suppressBogusNextTrack = false;
        }

        _stuckTimer.Change(
            Timeout.Infinite,
            Timeout.Infinite
        );

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stuckTimer.Dispose();
            _trayIcon.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Stats
    {
        public long TotalRepairs { get; set; }

        public DateTimeOffset? LastRepair { get; set; }
    }

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true
    )]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(
        IntPtr hhk
    );

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport(
        "user32.dll",
        SetLastError = true
    )]
    private static extern uint SendInput(
        uint cInputs,
        INPUT[] pInputs,
        int cbSize
    );

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode
    )]
    private static extern IntPtr GetModuleHandle(
        string? lpModuleName
    );
}