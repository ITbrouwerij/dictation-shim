using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DictationShim;

internal static class Program
{
    // Source chord: Ctrl + Win (any side, any press order).
    // Target: Shift + Alt + Space — combined with the user's already-held
    // Ctrl, the OS sees Ctrl+Shift+Alt+Space, which Whispering treats as
    // its push-to-talk hotkey.

    [STAThread]
    private static void Main()
    {
        // Single-instance guard.
        using var mutex = new Mutex(initiallyOwned: true, name: "Local\\DictationShimSingleton", out bool acquired);
        if (!acquired) return;

        ApplicationConfiguration.Initialize();

        using var trayIcon = new NotifyIcon
        {
            Icon = MakeMicIcon(),
            Text = "Dictation shim — Ctrl+Win → Shift+Alt+Space",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        Hook.Install();
        Application.ApplicationExit += (_, _) => Hook.Uninstall();
        Application.Run();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon MakeMicIcon()
    {
        // Render the Segoe Fluent Icons "Microphone" glyph (U+E720) as a
        // 32×32 white-on-transparent icon. Falls back to Segoe MDL2 Assets
        // (same code-point) on older Windows.
        var size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            string fontFamily = "Segoe Fluent Icons";
            using (var test = new Font(fontFamily, 1f))
            {
                if (!test.Name.Equals(fontFamily, StringComparison.OrdinalIgnoreCase))
                    fontFamily = "Segoe MDL2 Assets";
            }

            using var font = new Font(fontFamily, 22f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("", font, brush, new RectangleF(0, 0, size, size), fmt);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Dictation shim active").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reset stuck keys", null, (_, _) => Hook.ResetStuckKeys());
        menu.Items.Add("Restart", null, (_, _) =>
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Exit();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Application.Exit());
        return menu;
    }
}

internal static class Hook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_SPACE = 0x20;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    // Marker stamped on synthesized events so we ignore our own injections.
    private static readonly nint OUR_TAG = unchecked((nint)0xCAFEFEED);

    private static IntPtr _hookId = IntPtr.Zero;
    private static readonly LowLevelKeyboardProc _proc = HookCallback;

    private static bool _ctrlDown, _winDown, _chordActive;
    // Whether the OS currently sees Win as held. Win-down is *always*
    // suppressed at the hook; we synthesize Win-down to the OS only when we
    // resolve the press as standalone-Win or Win+otherkey (not chord). The
    // matching Win-up then either passes through (if _winInOS) or is
    // suppressed (if it stayed under our control).
    private static bool _winInOS;
    private static System.Threading.Timer? _winResolveTimer;
    // Window in which we wait to see if Ctrl arrives to make a chord. If
    // nothing happens, we release Win to the OS so standalone Win still works
    // (Start menu, Win+letter combos via the other-key fallback).
    private const int WIN_RESOLVE_DELAY_MS = 30;
    private static readonly object _stateLock = new();
    // Serialize SendTarget calls so a queued DOWN can never overtake (or be
    // overtaken by) the matching UP when the worker thread is bursty.
    private static readonly object _sendLock = new();

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DictationShim", "debug.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { /* ignore */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    public static void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        Log($"hook installed (id=0x{_hookId.ToInt64():X})");
    }

    public static void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Skip our own synthesized events.
        if (data.dwExtraInfo == OUR_TAG)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        bool isCtrl = data.vkCode == VK_LCONTROL || data.vkCode == VK_RCONTROL;
        bool isWin = data.vkCode == VK_LWIN || data.vkCode == VK_RWIN;

        // === Non-Ctrl/Win key arriving while Win is "pending resolve" ===
        // If user presses Win+E (or any non-Ctrl) within the resolve window,
        // release Win to the OS *before* this key so Win+letter combos work.
        if (!isCtrl && !isWin && isDown)
        {
            lock (_stateLock)
            {
                if (_winDown && !_chordActive && !_winInOS)
                {
                    CancelWinResolveTimer();
                    ReleaseWinToOS();
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        if (!isCtrl && !isWin)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // === Ctrl events ===
        if (isCtrl)
        {
            if (isDown)
            {
                lock (_stateLock)
                {
                    _ctrlDown = true;
                    if (_winDown && !_chordActive)
                    {
                        CancelWinResolveTimer();
                        ActivateChord();
                    }
                }
            }
            else if (isUp)
            {
                lock (_stateLock)
                {
                    _ctrlDown = false;
                    if (_chordActive)
                        DeactivateChord();
                }
            }
            // Always pass Ctrl through — apps rely on it as a modifier.
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // === Win events ===
        // Win-down is ALWAYS suppressed at the hook; we resolve it ~30 ms
        // later by either keeping it suppressed (chord case) or synthesizing
        // a Win-down to the OS (standalone / Win+letter case).
        if (isDown)
        {
            lock (_stateLock)
            {
                _winDown = true;
                CancelWinResolveTimer();
                if (_ctrlDown && !_chordActive)
                {
                    // Ctrl already held — chord activates immediately.
                    ActivateChord();
                }
                else
                {
                    // Schedule fallback release to OS.
                    _winResolveTimer = new System.Threading.Timer(_ =>
                    {
                        lock (_stateLock)
                        {
                            if (_winDown && !_chordActive && !_winInOS)
                                ReleaseWinToOS();
                            _winResolveTimer = null;
                        }
                    }, null, WIN_RESOLVE_DELAY_MS, System.Threading.Timeout.Infinite);
                }
            }
            return (IntPtr)1;
        }
        else if (isUp)
        {
            bool passThrough;
            lock (_stateLock)
            {
                _winDown = false;
                CancelWinResolveTimer();
                if (_chordActive)
                    DeactivateChord();
                passThrough = _winInOS;
                _winInOS = false;
            }
            return passThrough
                ? CallNextHookEx(_hookId, nCode, wParam, lParam)
                : (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // Caller must hold _stateLock.
    private static void CancelWinResolveTimer()
    {
        _winResolveTimer?.Dispose();
        _winResolveTimer = null;
    }

    // Caller must hold _stateLock.
    private static void ReleaseWinToOS()
    {
        // Synthesize the LWin-down event the OS missed (we suppressed the
        // real one). OUR_TAG so our own hook ignores this event.
        byte scan = (byte)MapVirtualKey(VK_LWIN, MAPVK_VK_TO_VSC);
        keybd_event((byte)VK_LWIN, scan, 0, OUR_TAG);
        _winInOS = true;
    }

    // Caller must hold _stateLock.
    private static void ActivateChord()
    {
        _chordActive = true;
        ThreadPool.QueueUserWorkItem(_ => SendTarget(down: true));
        ThreadPool.QueueUserWorkItem(_ => Clip.OnChordActivated());
        ThreadPool.QueueUserWorkItem(_ => Audio.OnChordActivated());
    }

    // Caller must hold _stateLock.
    private static void DeactivateChord()
    {
        _chordActive = false;
        ThreadPool.QueueUserWorkItem(_ => SendTarget(down: false));
        ThreadPool.QueueUserWorkItem(_ => Clip.OnChordDeactivated());
        ThreadPool.QueueUserWorkItem(_ => Audio.OnChordDeactivated());
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    private const uint MAPVK_VK_TO_VSC = 0;

    private static void SendTarget(bool down)
    {
        // Use keybd_event (not SendInput). On this system, Tauri's
        // RegisterHotKey-registered global shortcut detects keybd_event
        // synthetic events but ignores SendInput synthetic events. The
        // exact reason is undocumented but reproducible.
        // VK_CONTROL=0x11, VK_SHIFT=0x10, VK_MENU(Alt)=0x12, VK_SPACE=0x20.
        byte[] keys = { 0x11, 0x10, 0x12, 0x20 };
        if (!down) Array.Reverse(keys);

        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        lock (_sendLock)
        {
            foreach (byte k in keys)
            {
                byte scan = (byte)MapVirtualKey(k, MAPVK_VK_TO_VSC);
                // Mark with OUR_TAG so the LL hook callback recognizes our
                // own injected events and skips them — otherwise the
                // synthesized Ctrl-up events would corrupt _ctrlDown while
                // the user is still physically holding Ctrl.
                keybd_event(k, scan, flags, OUR_TAG);
            }
        }
    }

    public static void ResetStuckKeys()
    {
        // Defensive: release every key our shim or a chord could possibly have
        // left in a "down" state. Useful when push-to-talk gets out of sync.
        byte[] keys = {
            0x11, 0xA2, 0xA3,             // Ctrl, LCtrl, RCtrl
            0x10, 0xA0, 0xA1,             // Shift, LShift, RShift
            0x12, 0xA4, 0xA5,             // Alt, LAlt, RAlt
            0x5B, 0x5C,                   // LWin, RWin
            0x20,                         // Space
        };
        lock (_sendLock)
        {
            foreach (byte k in keys)
            {
                byte scan = (byte)MapVirtualKey(k, MAPVK_VK_TO_VSC);
                keybd_event(k, scan, KEYEVENTF_KEYUP, OUR_TAG);
            }
            lock (_stateLock)
            {
                _ctrlDown = false;
                _winDown = false;
                _chordActive = false;
                _winInOS = false;
                CancelWinResolveTimer();
            }
        }
        Log("ResetStuckKeys fired");
    }
}

internal static class Clip
{
    // Snapshot the user's clipboard at chord-activation, restore it after
    // Whispering has finished its paste-and-restore flow. Whispering itself
    // only preserves plain text; this preserves ALL clipboard formats
    // (rich text, HTML, images, file lists, custom app formats, …).

    [DllImport("user32.dll")]
    private static extern int GetClipboardSequenceNumber();

    private static IDataObject? _snapshot;
    private static int _snapshotSeq;
    private static CancellationTokenSource? _restoreCts;
    private static readonly object _stateLock = new();

    // Generous upper bound: long dictations + slow transcription backends
    // can easily push past a minute. If Whispering never writes the clipboard
    // within this window, we treat it as "transcription cancelled / failed"
    // and skip the restore — the clipboard hasn't been clobbered, so there's
    // nothing to restore.
    private const int RESTORE_TIMEOUT_MS = 300_000; // 5 minutes
    private const int POLL_INTERVAL_MS = 100;
    // After Whispering's clipboard write is observed, give it time to paste
    // and run its own restore attempt before we overwrite with the original.
    private const int POST_CHANGE_DELAY_MS = 350;

    public static void OnChordActivated()
    {
        // Cancel any pending restore from a previous chord.
        lock (_stateLock)
        {
            _restoreCts?.Cancel();
            _restoreCts = null;
        }

        try
        {
            var snap = RunOnSta(() =>
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return null;
                var copy = new DataObject();
                foreach (var fmt in data.GetFormats())
                {
                    try
                    {
                        var content = data.GetData(fmt);
                        if (content != null) copy.SetData(fmt, content);
                    }
                    catch { /* unreadable format */ }
                }
                return (IDataObject?)copy;
            });

            lock (_stateLock)
            {
                _snapshot = snap;
                _snapshotSeq = GetClipboardSequenceNumber();
            }
        }
        catch { /* failed to snapshot — silently skip restore */ }
    }

    public static void OnChordDeactivated()
    {
        IDataObject? snap;
        int startSeq;
        CancellationTokenSource cts;

        lock (_stateLock)
        {
            snap = _snapshot;
            startSeq = _snapshotSeq;
            _restoreCts?.Cancel();
            cts = new CancellationTokenSource();
            _restoreCts = cts;
        }

        if (snap == null) return;

        Task.Run(async () =>
        {
            try
            {
                int waited = 0;
                bool clipboardChanged = false;
                // Phase 1: wait for the first clipboard change (Whispering writes text).
                while (!cts.IsCancellationRequested && waited < RESTORE_TIMEOUT_MS)
                {
                    await Task.Delay(POLL_INTERVAL_MS, cts.Token);
                    waited += POLL_INTERVAL_MS;
                    if (GetClipboardSequenceNumber() != startSeq)
                    {
                        clipboardChanged = true;
                        break;
                    }
                }
                if (cts.IsCancellationRequested) return;
                if (!clipboardChanged)
                {
                    // Timed out without Whispering writing anything (transcription
                    // cancelled/failed, or backend stalled). Clipboard hasn't been
                    // clobbered, so there is nothing to restore — skip and exit.
                    return;
                }

                // Phase 2: Whispering's flow is write, sleep 50, paste, sleep 100,
                // restore-text. Wait long enough that its restore-attempt has run,
                // so our restore lands on top.
                await Task.Delay(POST_CHANGE_DELAY_MS, cts.Token);
                if (cts.IsCancellationRequested) return;

                RunOnSta(() =>
                {
                    try { Clipboard.SetDataObject(snap, copy: true); } catch { }
                    return 0;
                });
            }
            catch (TaskCanceledException) { }
            catch { /* swallow — clipboard restore is best-effort */ }
        });
    }

    private static T RunOnSta<T>(Func<T> fn)
    {
        T result = default!;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { result = fn(); }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (err != null) throw err;
        return result;
    }
}

internal static class Audio
{
    // Mute the default render endpoint (speakers/headphones) while a chord
    // is active so background music/video doesn't bleed into the mic and
    // doesn't distract while dictating. Restores prior mute state on chord
    // release. If the user had already muted manually, we leave it alone.

    private static bool? _wasMutedBeforeChord;
    private static CancellationTokenSource? _muteCts;
    private static readonly object _audioLock = new();
    // Wait this long before muting so Whispering's start-recording beep has
    // time to play. Brief chord taps (< this) never mute at all.
    private const int MUTE_DELAY_MS = 250;

    public static void OnChordActivated()
    {
        var cts = new CancellationTokenSource();
        lock (_audioLock)
        {
            _muteCts?.Cancel();
            _muteCts = cts;
        }
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MUTE_DELAY_MS, cts.Token);
                DoMute();
            }
            catch (TaskCanceledException) { }
            catch { /* best-effort */ }
        });
    }

    public static void OnChordDeactivated()
    {
        bool? prior;
        lock (_audioLock)
        {
            _muteCts?.Cancel();
            _muteCts = null;
            prior = _wasMutedBeforeChord;
            _wasMutedBeforeChord = null;
        }
        // Only unmute if we actually muted (user wasn't already muted).
        if (prior != false) return;
        DoUnmute();
    }

    private static void DoMute()
    {
        try
        {
            var vol = GetMasterVolume();
            if (vol == null) return;
            try
            {
                bool muted = false;
                vol.GetMute(out muted);
                lock (_audioLock)
                {
                    _wasMutedBeforeChord = muted;
                }
                if (!muted)
                {
                    Guid ctx = Guid.Empty;
                    vol.SetMute(true, ref ctx);
                }
            }
            finally { Marshal.ReleaseComObject(vol); }
        }
        catch { }
    }

    private static void DoUnmute()
    {
        try
        {
            var vol = GetMasterVolume();
            if (vol == null) return;
            try
            {
                Guid ctx = Guid.Empty;
                vol.SetMute(false, ref ctx);
            }
            finally { Marshal.ReleaseComObject(vol); }
        }
        catch { }
    }

    private static IAudioEndpointVolume? GetMasterVolume()
    {
        var enumerator = (IMMDeviceEnumerator?)new MMDeviceEnumerator();
        if (enumerator == null) return null;
        try
        {
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (hr != 0 || device == null) return null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out var iface);
                if (hr != 0 || iface == null) return null;
                return (IAudioEndpointVolume)iface;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // --- Core Audio COM interop ---

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [Flags]
    private enum CLSCTX : uint
    {
        INPROC_SERVER = 0x1, INPROC_HANDLER = 0x2, LOCAL_SERVER = 0x4,
        REMOTE_SERVER = 0x10,
        ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER,
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        // ... remaining methods unused
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        // ... remaining unused
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
