using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    private const string TaskName = "AcerInputFix";
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // Start Menu / Explorer launches are usually not elevated. Prefer the
        // existing Highest logon task so Col07 reset works without a UAC prompt.
        if (!IsProcessElevated() && TryRelaunchViaLogonTask())
            return;

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

    private static bool IsProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool TryRelaunchViaLogonTask()
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (query is null)
                return false;

            query.WaitForExit(5000);
            if (query.ExitCode != 0)
                return false;

            using var run = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            run?.WaitForExit(5000);
            return run is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    // Quarantine window for injected Next Track before classifying Acer fault.
    private const int StuckTimeoutMs = 350;

    // Avoid disable/enable thrash if repairs fire again right after a cycle.
    private const int Col07AutoCycleCooldownMs = 8000;

    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    private const uint VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    private const uint LLKHF_INJECTED = 0x10;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Lets our hook distinguish input replayed by this app.
    private static readonly UIntPtr OurExtraInfo =
        new UIntPtr(0x41434658); // "ACFX"

    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _runRepairsItem;
    private readonly ToolStripMenuItem _totalRepairsItem;
    private readonly ToolStripMenuItem _lastRepairItem;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _autoResetCol07Item;
    private readonly ToolStripMenuItem _diagnosticsItem;

    private readonly object _stateLock = new();
    private readonly object _logLock = new();

    private readonly System.Threading.Timer _stuckTimer;
    private readonly DiagnosticsMonitor _diagnostics;
    private readonly bool _isElevated;

    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook = IntPtr.Zero;

    private bool _nextTrackPending;
    private bool _suppressBogusNextTrack;
    private bool _enabled = true;
    private bool _autoResetCol07 = true;

    private long _runRepairs;
    private long _lastCol07CycleTick;
    private int _col07CycleInProgress;
    private Stats _stats;

    private readonly string _installDir;
    private readonly string _dataDir;
    private readonly string _logPath;
    private readonly string _statsPath;

    public TrayContext()
    {
        _installDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        _dataDir = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            ),
            "AcerInputFix"
        );

        Directory.CreateDirectory(_dataDir);

        _logPath = Path.Combine(_dataDir, "AcerInputFix.log");
        _statsPath = Path.Combine(_dataDir, "stats.json");

        TryMigrateLegacyDataFiles();

        _stats = LoadStats();
        _autoResetCol07 = _stats.AutoResetCol07;
        _isElevated = IsProcessElevated();

        RotateLogIfNeeded();
        Log($"AcerInputFix {GetAppVersion()} starting.");
        Log($"Install dir: {_installDir}");
        Log($"Data dir: {_dataDir}");
        Log(
            $"Interop sizes: INPUT={Marshal.SizeOf<INPUT>()}, " +
            $"KEYBDINPUT={Marshal.SizeOf<KEYBDINPUT>()}"
        );
        Log(
            $"Elevation: {(_isElevated ? "Administrator" : "Limited")}  " +
            $"AutoResetCol07: {_autoResetCol07}"
        );

        if (_autoResetCol07 && !_isElevated)
        {
            Log(
                "WARNING: Auto-reset Col07 is on but this process is not " +
                "elevated. Layer-1 B0 suppression still works; Layer-2 " +
                "taskbar repair via Col07 cycle will be skipped. " +
                "Start via the AcerInputFix logon task or reinstall with " +
                "the sign-in startup option enabled."
            );
        }

        _diagnostics = new DiagnosticsMonitor(Log);

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

        _autoResetCol07Item = new ToolStripMenuItem(
            "Auto-reset Col07 on repair"
        )
        {
            Checked = _autoResetCol07,
            CheckOnClick = true
        };

        _autoResetCol07Item.CheckedChanged += (_, _) =>
        {
            _autoResetCol07 = _autoResetCol07Item.Checked;

            lock (_stateLock)
            {
                _stats.AutoResetCol07 = _autoResetCol07;
                SaveStats();
            }

            Log(
                _autoResetCol07
                    ? "Auto-reset Col07 enabled."
                    : "Auto-reset Col07 disabled."
            );

            if (_autoResetCol07 && !_isElevated)
            {
                Log(
                    "WARNING: Auto-reset Col07 needs Administrator. " +
                    "B0 suppression continues; Col07 cycles will be skipped."
                );
            }

            RefreshMenu();
        };

        _diagnosticsItem = new ToolStripMenuItem(
            "Layer-2 diagnostics"
        )
        {
            Checked = false,
            CheckOnClick = true
        };

        _diagnosticsItem.CheckedChanged += (_, _) =>
        {
            _diagnostics.SetEnabled(_diagnosticsItem.Checked);
            RefreshMenu();
        };

        var openLogItem = new ToolStripMenuItem("Open log");
        openLogItem.Click += (_, _) =>
        {
            try
            {
                EnsureLogExists();

                Process.Start(new ProcessStartInfo
                {
                    FileName = _logPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not open the log file.\n\n{ex.Message}\n\n" +
                    $"Expected path:\n{_logPath}",
                    "AcerInputFix",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        };

        var openFolderItem = new ToolStripMenuItem("Open data folder");
        openFolderItem.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _dataDir,
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
        _menu.Items.Add(_autoResetCol07Item);
        _menu.Items.Add(_diagnosticsItem);

        _menu.Items.Add(new ToolStripSeparator());

        var markBrokenItem = new ToolStripMenuItem(
            "DIAG: Mark taskbar previews broken"
        );

        markBrokenItem.Click += (_, _) =>
        {
            Log(
                $"DIAG MARK: taskbar previews BROKEN " +
                $"(B0 suppress active={IsB0SuppressActive()})"
            );
        };

        var markRecoveredItem = new ToolStripMenuItem(
            "DIAG: Mark taskbar previews recovered"
        );

        markRecoveredItem.Click += (_, _) =>
        {
            Log(
                $"DIAG MARK: taskbar previews RECOVERED " +
                $"(B0 suppress active={IsB0SuppressActive()})"
            );
        };

        var testPreviousTrackResetItem =
            new ToolStripMenuItem("Test synthetic Previous Track");

        testPreviousTrackResetItem.Click += (_, _) =>
        {
            _diagnostics.Mark(
                "about to send synthetic VK_MEDIA_PREV_TRACK"
            );

            if (SendSyntheticMediaTap(VK_MEDIA_PREV_TRACK))
            {
                Log(
                    "TEST: Sent synthetic VK_MEDIA_PREV_TRACK " +
                    "DOWN+UP reset sequence."
                );

                _diagnostics.Mark(
                    "synthetic VK_MEDIA_PREV_TRACK completed"
                );
            }
            else
            {
                int error = Marshal.GetLastWin32Error();

                Log(
                    $"ERROR: Synthetic Previous Track reset failed. " +
                    $"Win32 error {error}."
                );
            }
        };

        var testPlayPauseResetItem =
            new ToolStripMenuItem("Test synthetic Play/Pause");

        testPlayPauseResetItem.Click += (_, _) =>
        {
            _diagnostics.Mark(
                "about to send synthetic VK_MEDIA_PLAY_PAUSE"
            );

            if (SendSyntheticMediaTap(VK_MEDIA_PLAY_PAUSE))
            {
                Log(
                    "TEST: Sent synthetic VK_MEDIA_PLAY_PAUSE " +
                    "DOWN+UP reset sequence."
                );

                _diagnostics.Mark(
                    "synthetic VK_MEDIA_PLAY_PAUSE completed"
                );
            }
            else
            {
                int error = Marshal.GetLastWin32Error();

                Log(
                    $"ERROR: Synthetic Play/Pause reset failed. " +
                    $"Win32 error {error}."
                );
            }
        };

        var cycleCol07Item = new ToolStripMenuItem(
            "DIAG: Cycle Col07 (disable/enable)"
        );

        cycleCol07Item.Click += (_, _) => CycleCol07Diagnostic();

        _menu.Items.Add(markBrokenItem);
        _menu.Items.Add(markRecoveredItem);
        _menu.Items.Add(testPreviousTrackResetItem);
        _menu.Items.Add(testPlayPauseResetItem);
        _menu.Items.Add(cycleCol07Item);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(openLogItem);
        _menu.Items.Add(openFolderItem);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(exitItem);

        _menu.Opening += (_, _) =>
        {
            RefreshMenu();
            Log(
                $"DIAG MARK: tray menu opened " +
                $"(B0 suppress active={IsB0SuppressActive()})"
            );
        };

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

        // Events replayed by this app must be allowed through untouched.
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
            bool replayLegitimateTap = false;

            lock (_stateLock)
            {
                // If we already classified this as the Acer fault, swallow
                // the entire remaining B0 stream until its real UP arrives.
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

                if (isDown)
                {
                    // First injected B0 DOWN: quarantine it. Do not let
                    // Explorer or applications see it yet.
                    if (!_nextTrackPending)
                    {
                        _nextTrackPending = true;

                        _stuckTimer.Change(
                            StuckTimeoutMs,
                            Timeout.Infinite
                        );
                    }

                    // Suppress the initial DOWN and any repeat DOWNs that
                    // arrive while we decide whether this is legitimate.
                    return new IntPtr(1);
                }

                if (isUp && _nextTrackPending)
                {
                    // A matching UP arrived quickly enough to be a real
                    // Next Track tap. Suppress the original UP too, then
                    // replay one clean DOWN+UP sequence outside the lock.
                    _nextTrackPending = false;

                    _stuckTimer.Change(
                        Timeout.Infinite,
                        Timeout.Infinite
                    );

                    replayLegitimateTap = true;
                }
            }

            if (replayLegitimateTap)
            {
                if (!SendSyntheticMediaTap(VK_MEDIA_NEXT_TRACK))
                {
                    int error = Marshal.GetLastWin32Error();

                    Log(
                        $"ERROR: Could not replay legitimate " +
                        $"VK_MEDIA_NEXT_TRACK tap. Win32 error {error}."
                    );
                }

                return new IntPtr(1);
            }
        }

        return CallNextHookEx(
            _hook,
            nCode,
            wParam,
            lParam
        );
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

            // No matching UP arrived within the quarantine window.
            // The original DOWN never reached Windows, so there is nothing
            // to release downstream. Keep swallowing this injected B0
            // stream until Acer eventually emits its real UP.
            _nextTrackPending = false;
            _suppressBogusNextTrack = true;
        }

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
            $"Quarantined stuck VK_MEDIA_NEXT_TRACK after " +
            $"{StuckTimeoutMs} ms; suppressing bogus stream " +
            $"before it reaches Windows."
        );

        Log(
            $"DIAG MARK: REPAIR #{runCount} classified Acer B0 fault " +
            "(quarantine/suppression active)"
        );

        if (_autoResetCol07)
        {
            // Layer-1 is already handled (B0 swallowed). Reset Col07 so
            // Layer-2 taskbar/preview state can recover without a physical key.
            RequestCol07Cycle(
                reason: $"auto after REPAIR #{runCount}",
                interactiveConfirm: false
            );
        }
    }

    private bool IsB0SuppressActive()
    {
        lock (_stateLock)
            return _suppressBogusNextTrack || _nextTrackPending;
    }

    private void CycleCol07Diagnostic()
    {
        RequestCol07Cycle(
            reason: "manual tray action",
            interactiveConfirm: true
        );
    }

    private void RequestCol07Cycle(
        string reason,
        bool interactiveConfirm)
    {
        if (interactiveConfirm)
        {
            var confirm = MessageBox.Show(
                "Temporarily disable then re-enable the Acer Col07 " +
                "consumer-control HID device?\n\n" +
                "Volume, brightness, and media keys will stop working " +
                "for about one second.\n\n" +
                "This usually requires running AcerInputFix as Administrator.",
                "AcerInputFix — Cycle Col07",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning
            );

            if (confirm != DialogResult.OK)
            {
                Log("DIAG Col07: Cycle cancelled by user.");
                return;
            }
        }

        if (!_isElevated)
        {
            Log(
                $"DIAG Col07: Cycle skipped ({reason}) — not elevated. " +
                "Restart as Administrator for Layer-2 Col07 reset."
            );
            return;
        }

        if (Interlocked.CompareExchange(
                ref _col07CycleInProgress, 1, 0) != 0)
        {
            Log(
                $"DIAG Col07: Cycle skipped ({reason}) — " +
                "another cycle is already running."
            );
            return;
        }

        if (!interactiveConfirm)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastCol07CycleTick);

            if (last != 0 &&
                now - last < Col07AutoCycleCooldownMs)
            {
                Interlocked.Exchange(ref _col07CycleInProgress, 0);

                Log(
                    $"DIAG Col07: Auto-reset skipped ({reason}) — " +
                    $"cooldown {Col07AutoCycleCooldownMs} ms " +
                    $"(B0 suppress still active={IsB0SuppressActive()})."
                );
                return;
            }
        }

        bool suppressBefore = IsB0SuppressActive();

        Log(
            $"DIAG MARK: Col07 cycle START reason={reason} " +
            $"(B0 suppress active={suppressBefore})"
        );

        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = false;

            try
            {
                ok = Col07DeviceControl.TryCycle(Log, settleMs: 750);
            }
            catch (Exception ex)
            {
                Log($"DIAG Col07: Cycle threw: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(
                    ref _lastCol07CycleTick,
                    Environment.TickCount64
                );
                Interlocked.Exchange(ref _col07CycleInProgress, 0);
            }

            bool suppressAfter = IsB0SuppressActive();

            Log(
                $"DIAG MARK: Col07 cycle END ok={ok} reason={reason} " +
                $"(B0 suppress active={suppressAfter})"
            );

            if (!ok)
            {
                Log(
                    "DIAG Col07: Cycle failed — see earlier log lines. " +
                    "If Win32 5, restart elevated and retry."
                );
            }
        });
    }

    private static string GetAppVersion()
    {
        var assembly = typeof(Program).Assembly;

        string? informational =
            assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        Version? version = assembly.GetName().Version;
        return version?.ToString() ?? "unknown";
    }

    private static bool IsProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool SendSyntheticMediaTap(uint virtualKey)
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
                        wVk = (ushort)virtualKey,
                        wScan = 0,
                        dwFlags = 0,
                        time = 0,
                        dwExtraInfo = OurExtraInfo
                    }
                }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)virtualKey,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = OurExtraInfo
                    }
                }
            }
        ];

        uint sent = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<INPUT>()
        );

        return sent == inputs.Length;
    }

    private void RefreshMenu()
    {
        string elevationNote =
            _isElevated ? "" : " (needs Admin)";

        _statusItem.Text =
            _enabled
                ? _autoResetCol07
                    ? $"Status: Active + Col07 auto-reset{elevationNote}"
                    : "Status: Active (B0 only)"
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
                ? _autoResetCol07 && _isElevated
                    ? "Acer Input Fix - Active + Col07 reset"
                    : "Acer Input Fix - Active"
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

            Stats stats =
                JsonSerializer.Deserialize<Stats>(json)
                ?? new Stats();

            // Old stats.json files omit AutoResetCol07; default that to on.
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty(
                        "AutoResetCol07",
                        out _))
                {
                    stats.AutoResetCol07 = true;
                }
            }

            return stats;
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

    private void TryMigrateLegacyDataFiles()
    {
        try
        {
            string legacyStats =
                Path.Combine(_installDir, "stats.json");
            string legacyLog =
                Path.Combine(_installDir, "AcerInputFix.log");

            if (!File.Exists(_statsPath) && File.Exists(legacyStats))
                File.Copy(legacyStats, _statsPath);

            if (!File.Exists(_logPath) && File.Exists(legacyLog))
                File.Copy(legacyLog, _logPath);
        }
        catch
        {
            // Migration is best-effort (Program Files may be unreadable/unwritable).
        }
    }

    private void EnsureLogExists()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);

            if (!File.Exists(_logPath))
                File.WriteAllText(_logPath, "");
        }
        catch
        {
            // Never throw into tray click handlers.
        }
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
                    _dataDir,
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
                Directory.CreateDirectory(_dataDir);

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
            string dataDir = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "AcerInputFix"
            );

            Directory.CreateDirectory(dataDir);

            File.AppendAllText(
                Path.Combine(dataDir, "AcerInputFix.log"),
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

        _diagnostics.SetEnabled(false);
        _diagnostics.Dispose();

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
            _diagnostics.Dispose();
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

        public bool AutoResetCol07 { get; set; } = true;
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