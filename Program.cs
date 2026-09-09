using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MeetMicSync;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var app = new SyncApplication();
        Application.Run(app.Context);
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMicSync", "log.txt");

    public static string FilePath => Path;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, line + Environment.NewLine);
        }
        Debug.WriteLine(line);
    }
}

internal sealed class SyncApplication : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly MeetMuteSender _sender = new();
    private readonly MicMuteWatcher _micWatcher;
    private readonly LenovoOsdWatcher _osdWatcher;
    private readonly object _gate = new();
    private long _lastActionTicks;
    private bool? _lastMuted;

    private static readonly long DebounceTicks = TimeSpan.FromMilliseconds(400).Ticks;

    public ApplicationContext Context { get; }

    public SyncApplication()
    {
        // Fresh log each run
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Log.FilePath)!);
            File.WriteAllText(Log.FilePath, $"--- MeetMicSync start {DateTime.Now:O} ---{Environment.NewLine}");
        }
        catch { /* ignore */ }

        _micWatcher = new MicMuteWatcher(OnMicMuteChanged, OnCaptureDeviceStateChanged);
        _osdWatcher = new LenovoOsdWatcher(OnLenovoOsd);

        _tray = new NotifyIcon
        {
            Text = "Meet Mic Sync",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        Context = new ApplicationContext();

        var micOk = _micWatcher.Start();
        var osdOk = _osdWatcher.Start();
        Log.Write($"micWatcher={micOk} osdWatcher={osdOk}");
        SetTray(micOk || osdOk
            ? "Meet Mic Sync — listening (mic+OSD)"
            : "Meet Mic Sync — failed to start watchers");

        if (!AppInstall.StartupShortcutExists())
        {
            _tray.ShowBalloonTip(
                5000,
                "Meet Mic Sync",
                "Tip: right-click this icon → “Start with Windows…” to run automatically at sign-in.",
                ToolTipIcon.Info);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Test Meet mute (Ctrl+D)", null, (_, _) =>
        {
            var ok = _sender.TryToggleMeetMute(out var detail);
            Log.Write($"TEST Ctrl+D ok={ok} detail={detail}");
            SetTray(ok ? $"Test OK → {detail}" : "Test FAIL — Meet window not found");
            _tray.ShowBalloonTip(2000, "Meet Mic Sync",
                ok ? $"Muted/unmuted Meet: {detail}" : "Meet window not found. Open a Meet tab.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem();
        void RefreshStartupItem()
        {
            if (AppInstall.StartupShortcutExists())
            {
                startupItem.Text = "Remove from Windows Startup…";
                startupItem.Click -= OnEnableStartupClick;
                startupItem.Click -= OnDisableStartupClick;
                startupItem.Click += OnDisableStartupClick;
            }
            else
            {
                startupItem.Text = "Start with Windows…";
                startupItem.Click -= OnEnableStartupClick;
                startupItem.Click -= OnDisableStartupClick;
                startupItem.Click += OnEnableStartupClick;
            }
        }

        RefreshStartupItem();
        menu.Opening += (_, _) => RefreshStartupItem();
        menu.Items.Add(startupItem);

        menu.Items.Add("Open log", null, (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Log.FilePath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Write($"open log failed: {ex.Message}");
            }
        });
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    private void OnEnableStartupClick(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(
            AppInstall.BuildConfirmMessage(),
            "Start Meet Mic Sync with Windows?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            Log.Write("install: user cancelled Enable Startup");
            return;
        }

        var result = AppInstall.EnableStartup();
        if (!result.Success)
        {
            MessageBox.Show(result.Message, "Meet Mic Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (result.NeedsRestart)
        {
            MessageBox.Show(
                result.Message,
                "Meet Mic Sync",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            try
            {
                AppInstall.RestartFromInstalledCopy();
            }
            catch (Exception ex)
            {
                Log.Write($"restart from install dir failed: {ex.Message}");
                MessageBox.Show(
                    "The Startup shortcut was created, but the app could not restart from the new folder automatically.\n\n" +
                    $"Please run:\n{AppInstall.InstalledExePath}",
                    "Meet Mic Sync",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _tray.Visible = false;
            Application.Exit();
            return;
        }

        MessageBox.Show(result.Message, "Meet Mic Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _tray.ShowBalloonTip(2500, "Meet Mic Sync", "Will start automatically when you sign in.", ToolTipIcon.Info);
    }

    private void OnDisableStartupClick(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(
            AppInstall.BuildRemoveConfirmMessage(),
            "Remove from Windows Startup?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            Log.Write("install: user cancelled Remove Startup");
            return;
        }

        var result = AppInstall.DisableStartup();
        MessageBox.Show(
            result.Message,
            "Meet Mic Sync",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private void OnMicMuteChanged(bool muted)
    {
        lock (_gate)
        {
            if (_lastMuted is null)
            {
                _lastMuted = muted;
                Log.Write($"mic seed muted={muted}");
                return;
            }

            if (_lastMuted == muted)
                return;

            _lastMuted = muted;
            Log.Write($"mic mute changed → muted={muted}");
        }

        TriggerFrom("mic-mute", muted ? "muted" : "unmuted");
    }

    private void OnLenovoOsd(string info)
    {
        Log.Write($"lenovo OSD/event: {info}");
        TriggerFrom("lenovo-osd", info);
    }

    private void OnCaptureDeviceStateChanged(string deviceId, int newState)
    {
        // 1=ACTIVE 2=DISABLED 4=NOTPRESENT 8=UNPLUGGED
        Log.Write($"capture device state id={deviceId} state={newState}");
        // Lenovo hardware mute often disables the endpoint instead of soft-mute.
        if (newState is 1 or 2 or 8)
            TriggerFrom("device-state", $"state={newState}");
    }

    private void TriggerFrom(string source, string info)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _lastActionTicks < DebounceTicks)
            {
                Log.Write($"debounce skip ({source})");
                return;
            }
            _lastActionTicks = now;
        }

        // Audio/OSD callbacks are NOT on the UI thread. SendInput from a
        // background thread is ignored by Chrome — always marshal first.
        void Run()
        {
            var ok = _sender.TryToggleMeetMute(out var detail);
            Log.Write($"toggle after {source} ({info}): ok={ok} detail={detail}");
            SetTray(ok
                ? $"Synced via {source} → {detail}"
                : $"Saw {source}, but Meet toggle failed");
        }

        var menu = _tray.ContextMenuStrip;
        if (menu is { IsHandleCreated: true } && menu.InvokeRequired)
            menu.BeginInvoke(Run);
        else
            Run();
    }

    private void SetTray(string text)
    {
        void Apply() => _tray.Text = text.Length <= 63 ? text : text[..63];

        try
        {
            if (_tray.ContextMenuStrip?.InvokeRequired == true)
                _tray.ContextMenuStrip.BeginInvoke(Apply);
            else
                Apply();
        }
        catch
        {
            Apply();
        }
    }

    public void Dispose()
    {
        _osdWatcher.Dispose();
        _micWatcher.Dispose();
        _tray.Dispose();
    }
}

/// <summary>
/// Core Audio mute + device-state callbacks — zero polling while idle.
/// Subscribes to every active capture endpoint (not only the default).
/// </summary>
internal sealed class MicMuteWatcher : IDisposable
{
    private readonly Action<bool> _onMuteChanged;
    private readonly Action<string, int> _onDeviceStateChanged;
    private readonly List<EndpointSubscription> _subscriptions = new();
    private readonly DeviceNotificationClient _deviceClient;

    private IMMDeviceEnumerator? _enumerator;
    private bool _seeded;

    private sealed class EndpointSubscription
    {
        public required string Id;
        public required IMMDevice Device;
        public required IAudioEndpointVolume Volume;
        public required VolumeCallback Callback;
    }

    public MicMuteWatcher(Action<bool> onMuteChanged, Action<string, int> onDeviceStateChanged)
    {
        _onMuteChanged = onMuteChanged;
        _onDeviceStateChanged = onDeviceStateChanged;
        _deviceClient = new DeviceNotificationClient(
            OnDefaultDeviceChanged,
            OnAnyDeviceStateChanged);
    }

    public bool Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_deviceClient);
            var n = AttachAllCaptureEndpoints();
            Log.Write($"mic: subscribed endpoints={n}");
            return n > 0;
        }
        catch (Exception ex)
        {
            Log.Write($"mic Start exception: {ex}");
            return false;
        }
    }

    private void OnDefaultDeviceChanged()
    {
        Log.Write("mic: default capture device changed — resubscribing");
        try
        {
            DetachAll();
            AttachAllCaptureEndpoints();
        }
        catch (Exception ex)
        {
            Log.Write($"mic reattach failed: {ex.Message}");
        }
    }

    private void OnAnyDeviceStateChanged(string deviceId, int newState)
    {
        // Capture endpoints use {0.0.1....}; render uses {0.0.0....}.
        if (deviceId.IndexOf("{0.0.1.", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        Log.Write($"mic: OnDeviceStateChanged id={deviceId} state={newState}");
        _onDeviceStateChanged(deviceId, newState);
        try
        {
            DetachAll();
            AttachAllCaptureEndpoints();
        }
        catch (Exception ex)
        {
            Log.Write($"mic state-change reattach failed: {ex.Message}");
        }
    }

    private int AttachAllCaptureEndpoints()
    {
        if (_enumerator is null)
            return 0;

        // 0x1 = DEVICE_STATE_ACTIVE only for mute subscription; state callback covers disable.
        var hr = _enumerator.EnumAudioEndpoints(EDataFlow.eCapture, 0x1, out var collPtr);
        if (hr != 0 || collPtr == IntPtr.Zero)
        {
            Log.Write($"mic: EnumAudioEndpoints hr=0x{hr:X8}");
            return AttachDefaultFallback();
        }

        var coll = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collPtr);
        Marshal.Release(collPtr);
        coll.GetCount(out var count);

        var attached = 0;
        for (uint i = 0; i < count; i++)
        {
            coll.Item(i, out var device);
            if (device is null) continue;
            if (TrySubscribe(device))
                attached++;
            else
                Marshal.ReleaseComObject(device);
        }

        Marshal.ReleaseComObject(coll);
        return attached > 0 ? attached : AttachDefaultFallback();
    }

    private int AttachDefaultFallback()
    {
        if (_enumerator is null) return 0;
        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var device);
        if (hr != 0 || device is null)
            hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
        if (hr != 0 || device is null) return 0;
        return TrySubscribe(device) ? 1 : 0;
    }

    private bool TrySubscribe(IMMDevice device)
    {
        device.GetId(out var id);
        device.GetState(out var state);

        var iid = typeof(IAudioEndpointVolume).GUID;
        var actHr = device.Activate(ref iid, ClsCtx.ALL, IntPtr.Zero, out var obj);
        if (actHr != 0 || obj is null)
        {
            Log.Write($"mic: Activate failed id={id} hr=0x{actHr:X8}");
            return false;
        }

        var volume = (IAudioEndpointVolume)obj;
        var callback = new VolumeCallback(muted =>
        {
            Log.Write($"mic notify id={id} muted={muted}");
            _onMuteChanged(muted);
        });

        var regHr = volume.RegisterControlChangeNotify(callback);
        Log.Write($"mic: attach id={id} state={state} reg=0x{regHr:X8}");
        if (regHr != 0)
        {
            Marshal.ReleaseComObject(volume);
            return false;
        }

        _subscriptions.Add(new EndpointSubscription
        {
            Id = id,
            Device = device,
            Volume = volume,
            Callback = callback
        });

        volume.GetMute(out var muted);
        if (!_seeded)
        {
            _seeded = true;
            _onMuteChanged(muted != 0);
        }

        return true;
    }

    private void DetachAll()
    {
        foreach (var s in _subscriptions)
        {
            try { s.Volume.UnregisterControlChangeNotify(s.Callback); } catch { /* ignore */ }
            Marshal.ReleaseComObject(s.Volume);
            Marshal.ReleaseComObject(s.Device);
        }
        _subscriptions.Clear();
    }

    public void Dispose()
    {
        DetachAll();
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceClient); } catch { /* ignore */ }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }
}

/// <summary>
/// Watches Lenovo FnHotkeyUtility / Vantage OSD appearing via WinEvent
/// (EVENT_OBJECT_SHOW). Event-driven — no timers.
/// </summary>
internal sealed class LenovoOsdWatcher : IDisposable
{
    private readonly Action<string> _onOsd;
    private readonly List<IntPtr> _hooks = new();
    private WinEventDelegate? _proc; // keep alive

    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;

    public LenovoOsdWatcher(Action<string> onOsd) => _onOsd = onOsd;

    public bool Start()
    {
        _proc = WinEventProc;
        // Narrow event set only — still fully event-driven (no timers).
        foreach (var ev in new[] { EVENT_OBJECT_SHOW, EVENT_OBJECT_UNCLOAKED, EVENT_SYSTEM_DIALOGSTART })
        {
            var hook = SetWinEventHook(ev, ev, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (hook != IntPtr.Zero)
                _hooks.Add(hook);
            Log.Write($"osd: hook event=0x{ev:X} handle=0x{hook:X}");
        }

        return _hooks.Count > 0;
    }

    private void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0)
            return;

        try
        {
            if (!IsWindowVisible(hwnd))
                return;

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            string processName;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                processName = p.ProcessName;
            }
            catch
            {
                return;
            }

            if (!IsLenovoHotkeyProcess(processName))
                return;

            var className = GetClass(hwnd);
            var title = GetTitle(hwnd);

            // Skip IME / infrastructure windows.
            if (className is "IME" or "MSCTFIME UI" ||
                className.Contains("BroadcastEvent", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("GDI+", StringComparison.OrdinalIgnoreCase))
                return;

            // FnHotkeyUtility often uses dialog (#32770) or custom OSD classes.
            var info = $"evt=0x{eventType:X} proc={processName} class={className} title={title} hwnd=0x{hwnd:X}";
            _onOsd(info);
        }
        catch (Exception ex)
        {
            Log.Write($"osd proc error: {ex.Message}");
        }
    }

    private static bool IsLenovoHotkeyProcess(string name) =>
        name.Contains("FnHotkey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("LenovoUtility", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LenovoVantage", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("LenovoVantage", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ImController", StringComparison.OrdinalIgnoreCase);

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
            UnhookWinEvent(hook);
        _hooks.Clear();
        _proc = null;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

internal sealed class VolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action<bool> _onMute;
    public VolumeCallback(Action<bool> onMute) => _onMute = onMute;

    public int OnNotify(IntPtr pNotify)
    {
        if (pNotify == IntPtr.Zero) return 0;
        var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotify)!;
        _onMute(data.bMuted != 0);
        return 0;
    }
}

internal sealed class DeviceNotificationClient : IMMNotificationClient
{
    private readonly Action _onDefaultCaptureChanged;
    private readonly Action<string, int> _onDeviceStateChanged;

    public DeviceNotificationClient(Action onDefaultCaptureChanged, Action<string, int> onDeviceStateChanged)
    {
        _onDefaultCaptureChanged = onDefaultCaptureChanged;
        _onDeviceStateChanged = onDeviceStateChanged;
    }

    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string deviceId)
    {
        if (flow == EDataFlow.eCapture)
            _onDefaultCaptureChanged();
    }

    public void OnDeviceAdded(string deviceId) { }
    public void OnDeviceRemoved(string deviceId) { }

    public void OnDeviceStateChanged(string deviceId, int newState)
        => _onDeviceStateChanged(deviceId, newState);

    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
}

internal sealed class MeetMuteSender
{
    public bool TryToggleMeetMute(out string detail)
    {
        detail = "";
        if (!TryFindMeetWindow(out var hwnd, out detail))
        {
            Log.Write("Meet window not found. Browser titles:");
            LogBrowserTitles();
            return false;
        }

        // 1) Prefer UI Automation click — works even when SendInput focus fails.
        if (TryInvokeMuteButton(hwnd, out var via))
        {
            detail += $" [{via}]";
            Log.Write($"Meet mute via UIA: {via}");
            return true;
        }

        Log.Write("UIA mute button not found — falling back to Ctrl+D");

        // 2) Fallback: force foreground on UI thread, then real Ctrl+D.
        if (!EnsureForeground(hwnd))
            Log.Write("WARNING: SetForegroundWindow did not stick — Ctrl+D may miss");

        Thread.Sleep(40);
        SendCtrlD();
        detail += " [Ctrl+D]";
        return true;
    }

    private static bool TryInvokeMuteButton(IntPtr hwnd, out string via)
    {
        via = "";
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null)
                return false;

            string[] names =
            [
                "Turn off microphone",
                "Turn on microphone",
                "Выключить микрофон",
                "Включить микрофон",
                "Mikrofon ausschalten",
                "Mikrofon einschalten"
            ];

            foreach (var name in names)
            {
                var cond = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.NameProperty, name);
                var el = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, cond);
                if (el is null)
                    continue;

                if (el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pattern) &&
                    pattern is System.Windows.Automation.InvokePattern invoke)
                {
                    invoke.Invoke();
                    via = $"UIA:{name}";
                    return true;
                }
            }

            // Broader: any button whose name contains microphone / микрофон.
            var buttons = root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Button));

            foreach (System.Windows.Automation.AutomationElement el in buttons)
            {
                string? name;
                try { name = el.Current.Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!name.Contains("microphone", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("микрофон", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pattern) &&
                    pattern is System.Windows.Automation.InvokePattern invoke)
                {
                    invoke.Invoke();
                    via = $"UIA-fuzzy:{name}";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"UIA error: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static void LogBrowserTitles()
    {
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (!IsBrowserOrMeetProcess(h, out var processName)) return true;
            var title = GetWindowTitle(h);
            if (title.Length == 0) return true;
            Log.Write($"  browser {processName}: {Truncate(title, 80)}");
            return true;
        }, IntPtr.Zero);
    }

    private static bool TryFindMeetWindow(out IntPtr hwnd, out string detail)
    {
        IntPtr found = IntPtr.Zero;
        string foundDetail = "";

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
                return true;

            var title = GetWindowTitle(h);
            if (title.Length == 0)
                return true;

            var looksLikeMeet =
                title.Contains("Meet -", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Meet –", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("meet.google.com", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith("Meet ", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Meet", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Google Meet", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeMeet)
                return true;

            if (!IsBrowserOrMeetProcess(h, out var processName))
                return true;

            found = h;
            foundDetail = $"{processName}: {Truncate(title, 48)}";
            return false;
        }, IntPtr.Zero);

        hwnd = found;
        detail = foundDetail;
        return hwnd != IntPtr.Zero;
    }

    private static bool IsBrowserOrMeetProcess(IntPtr hwnd, out string processName)
    {
        processName = "";
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            processName = p.ProcessName;
            return processName is "chrome" or "msedge" or "brave" or "GoogleMeet" or "Chromium" or "firefox";
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static bool EnsureForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd)
            return true;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9); // SW_RESTORE only when minimized

        // Classic unlock: synthetic Alt lets SetForegroundWindow succeed more often.
        keybd_event(0x12 /* VK_MENU */, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2 /* KEYUP */, UIntPtr.Zero);

        var foreground = GetForegroundWindow();
        var foreThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var currentThread = GetCurrentThreadId();

        if (foreThread != currentThread)
            AttachThreadInput(foreThread, currentThread, true);
        if (targetThread != currentThread)
            AttachThreadInput(targetThread, currentThread, true);

        BringWindowToTop(hwnd);
        var ok = SetForegroundWindow(hwnd);

        if (targetThread != currentThread)
            AttachThreadInput(targetThread, currentThread, false);
        if (foreThread != currentThread)
            AttachThreadInput(foreThread, currentThread, false);

        // Brief wait for focus to stick (only on mute press, not a poll loop).
        var deadline = Environment.TickCount64 + 150;
        while (Environment.TickCount64 < deadline)
        {
            if (GetForegroundWindow() == hwnd)
                return true;
            Thread.Sleep(10);
        }

        Log.Write($"foreground ok={ok} now=0x{GetForegroundWindow():X} want=0x{hwnd:X}");
        return GetForegroundWindow() == hwnd;
    }

    private static void SendCtrlD()
    {
        // Prefer scan codes — closer to a physical keyboard for Chrome.
        byte scanCtrl = (byte)MapVirtualKey(0x11, 0);
        byte scanD = (byte)MapVirtualKey(0x44, 0);

        var inputs = new[]
        {
            KeyScan(0x11, scanCtrl, keyUp: false),
            KeyScan(0x44, scanD, keyUp: false),
            KeyScan(0x44, scanD, keyUp: true),
            KeyScan(0x11, scanCtrl, keyUp: true)
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Log.Write($"SendInput Ctrl+D sent={sent}/4");
    }

    private static INPUT KeyScan(ushort vk, byte scan, bool keyUp) => new()
    {
        type = InputType.INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0, // ignored when SCANCODE is set
                wScan = scan,
                dwFlags = (keyUp ? KeyEventF.KEYEVENTF_KEYUP : 0) | KeyEventF.KEYEVENTF_SCANCODE
            }
        }
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

#region Core Audio + Win32 interop

internal enum EDataFlow { eRender, eCapture, eAll }
internal enum ERole { eConsole, eMultimedia, eCommunications }

[Flags]
internal enum ClsCtx : uint { ALL = 0x17 }

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
internal class AudioVolumeNotificationData
{
    public Guid guidEventContext;
    public int bMuted;
    public float fMasterVolume;
    public uint nChannels;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint pcDevices);
    [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, ClsCtx dwClsCtx, IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
    [PreserveSig] int GetChannelCount(out uint pnChannelCount);
    [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
    [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, Guid pguidEventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, Guid pguidEventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid pguidEventContext);
    [PreserveSig] int GetMute(out int pbMute);
    // Remaining vtable slots (unused) — required for correct layout if later methods are called.
    [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    [PreserveSig] int VolumeStepUp(Guid pguidEventContext);
    [PreserveSig] int VolumeStepDown(Guid pguidEventContext);
    [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr pNotify);
}

internal enum InputType : uint { INPUT_KEYBOARD = 1 }
internal enum VirtualKey : ushort { VK_CONTROL = 0x11, VK_D = 0x44 }
[Flags] internal enum KeyEventF : uint
{
    KEYEVENTF_KEYUP = 0x0002,
    KEYEVENTF_SCANCODE = 0x0008
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public InputType type;
    public InputUnion U;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public KeyEventF dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

#endregion
