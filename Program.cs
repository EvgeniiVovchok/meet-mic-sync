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
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMicSync");
    private static readonly string LogPath = Path.Combine(Dir, "log.txt");
    private static readonly string SettingsPath = Path.Combine(Dir, "settings.ini");

    private static bool _enabled;

    public static string FilePath => LogPath;

    public static bool Enabled
    {
        get { lock (Gate) return _enabled; }
        set
        {
            lock (Gate)
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                SaveSettings();
                if (_enabled)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(LogPath,
                        $"--- logging enabled {DateTime.Now:O} ---{Environment.NewLine}");
                }
            }
        }
    }

    static Log()
    {
        _enabled = LoadEnabled();
    }

    public static void Write(string message)
    {
        if (!_enabled)
            return;

        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            if (!_enabled)
                return;
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        Debug.WriteLine(line);
    }

    private static bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return false;
            foreach (var raw in File.ReadAllLines(SettingsPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("Logging=", StringComparison.OrdinalIgnoreCase))
                    return line.Equals("Logging=1", StringComparison.OrdinalIgnoreCase)
                        || line.Equals("Logging=true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* default off */ }
        return false;
    }

    private static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, $"Logging={(_enabled ? "1" : "0")}{Environment.NewLine}");
        }
        catch { /* ignore */ }
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
        _micWatcher = new MicMuteWatcher(OnMicMuteChanged, OnCaptureDeviceStateChanged, OnMicReseed);
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
            var ok = _sender.TrySyncMeetMute(wantMuted: null, out var detail);
            Log.Write($"TEST toggle ok={ok} detail={detail}");
            SetTray(ok ? $"Test OK → {detail}" : "Test FAIL — Meet window not found");
            _tray.ShowBalloonTip(2000, "Meet Mic Sync",
                ok ? $"Toggled Meet: {detail}" : "Meet window not found. Open a Meet tab.",
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
                if (!File.Exists(Log.FilePath))
                {
                    MessageBox.Show(
                        "No log file yet.\n\nEnable logging from the tray menu first, then reproduce the issue.",
                        "Meet Mic Sync",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

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

        var loggingItem = new ToolStripMenuItem();
        void RefreshLoggingItem()
        {
            loggingItem.Text = Log.Enabled ? "Disable logging" : "Enable logging";
        }
        RefreshLoggingItem();
        loggingItem.Click += (_, _) =>
        {
            Log.Enabled = !Log.Enabled;
            RefreshLoggingItem();
            _tray.ShowBalloonTip(
                2000,
                "Meet Mic Sync",
                Log.Enabled ? "Logging enabled." : "Logging disabled.",
                ToolTipIcon.Info);
        };
        menu.Opening += (_, _) => RefreshLoggingItem();
        menu.Items.Add(loggingItem);

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

    private void OnMicReseed(bool muted)
    {
        lock (_gate)
            _lastMuted = muted;
        Log.Write($"mic reseed muted={muted}");
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

        TriggerFrom("mic-mute", muted ? "muted" : "unmuted", wantMuted: muted);
    }

    private void OnLenovoOsd(string info)
    {
        // CapsLock, NumLock and Vantage raise the same FnHotkey window as mic mute.
        // Those keys do not change the capture mute flag, so they are not a trigger.
        Log.Write($"lenovo OSD ignored: {info}");
    }

    private void OnCaptureDeviceStateChanged(string deviceId, int newState)
    {
        // 1=ACTIVE 2=DISABLED. UNPLUGGED is a device going away, not the mute key.
        Log.Write($"capture device state id={deviceId} state={newState}");
        // Lenovo hardware mute often disables the default capture endpoint.
        if (newState is 1 or 2)
        {
            bool wantMuted = newState != 1;
            TriggerFrom("device-state", $"state={newState}", wantMuted: wantMuted);
        }
    }

    private void TriggerFrom(string source, string info, bool? wantMuted)
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
            var ok = _sender.TrySyncMeetMute(wantMuted, out var detail);
            Log.Write($"sync after {source} ({info}) wantMuted={wantMuted}: ok={ok} detail={detail}");
            SetTray(ok
                ? $"Synced via {source} → {detail}"
                : $"Saw {source}, but Meet sync failed");
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
    private readonly Action<bool> _onReseed;
    private readonly List<EndpointSubscription> _subscriptions = new();
    private readonly DeviceNotificationClient _deviceClient;
    private readonly object _subscribeGate = new();

    private IMMDeviceEnumerator? _enumerator;
    private string? _targetId;
    private bool _seeded;
    private int _resubscribeQueued;

    private sealed class EndpointSubscription
    {
        public required string Id;
        public required IMMDevice Device;
        public required IAudioEndpointVolume Volume;
        public required VolumeCallback Callback;
    }

    public MicMuteWatcher(Action<bool> onMuteChanged, Action<string, int> onDeviceStateChanged, Action<bool> onReseed)
    {
        _onMuteChanged = onMuteChanged;
        _onDeviceStateChanged = onDeviceStateChanged;
        _onReseed = onReseed;
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
            var n = SubscribeToDefault();
            Log.Write($"mic: subscribed endpoints={n}");
            return n > 0;
        }
        catch (Exception ex)
        {
            Log.Write($"mic Start exception: {ex}");
            return false;
        }
    }

    /// <summary>Best-effort current mute of default capture (communications, then console).</summary>
    public bool? TryReadMuted()
    {
        try
        {
            if (_enumerator is null)
                return null;

            if (TryGetMute(_enumerator, ERole.eCommunications, out var muted) ||
                TryGetMute(_enumerator, ERole.eConsole, out muted))
                return muted;

            // Fall back to any subscribed endpoint.
            foreach (var s in _subscriptions.ToArray())
            {
                try
                {
                    s.Volume.GetMute(out var m);
                    return m != 0;
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"TryReadMuted failed: {ex.Message}");
        }

        return null;
    }

    private static bool TryGetMute(IMMDeviceEnumerator enumerator, ERole role, out bool muted)
    {
        muted = false;
        var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, role, out var device);
        if (hr != 0 || device is null)
            return false;

        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            var actHr = device.Activate(ref iid, ClsCtx.ALL, IntPtr.Zero, out var obj);
            if (actHr != 0 || obj is null)
                return false;

            var volume = (IAudioEndpointVolume)obj;
            try
            {
                volume.GetMute(out var m);
                muted = m != 0;
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void OnDefaultDeviceChanged()
    {
        // Notification callbacks must not call the enumerator; that deadlocks
        // and stops mute events after a headset connects.
        Log.Write("mic: default capture device changed — queued resubscribe");
        QueueResubscribe();
    }

    private void OnAnyDeviceStateChanged(string deviceId, int newState)
    {
        // Capture endpoints use {0.0.1....}; render uses {0.0.0....}.
        if (deviceId.IndexOf("{0.0.1.", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var watched = string.Equals(deviceId, _targetId, StringComparison.OrdinalIgnoreCase);
        Log.Write($"mic: OnDeviceStateChanged id={deviceId} state={newState} watched={watched}");

        // A headset appearing is not a mute press. Only the device we follow
        // can disable/enable the endpoint Lenovo uses for hardware mute.
        if (watched && newState is 1 or 2)
            _onDeviceStateChanged(deviceId, newState);

        QueueResubscribe();
    }

    private void QueueResubscribe()
    {
        if (Interlocked.Exchange(ref _resubscribeQueued, 1) == 1)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                SubscribeToDefault();
            }
            catch (Exception ex)
            {
                Log.Write($"mic resubscribe failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _resubscribeQueued, 0);
            }
        });
    }

    /// <summary>Follow the default communications mic (headset when one is connected).</summary>
    private int SubscribeToDefault()
    {
        lock (_subscribeGate)
        {
            if (_enumerator is null)
                return 0;

            if (!TryOpenDefault(out var device, out var id) || device is null)
            {
                Log.Write("mic: no default capture endpoint");
                return _subscriptions.Count;
            }

            if (_subscriptions.Count == 1 &&
                string.Equals(_subscriptions[0].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                Marshal.ReleaseComObject(device);
                return 1;
            }

            Log.Write($"mic: switching to {id}");
            DetachAll();
            _targetId = id;
            if (!TrySubscribe(device))
            {
                Marshal.ReleaseComObject(device);
                return 0;
            }

            return 1;
        }
    }

    private bool TryOpenDefault(out IMMDevice? device, out string id)
    {
        device = null;
        id = "";
        if (_enumerator is null)
            return false;

        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out device);
        if (hr != 0 || device is null)
            hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
        if (hr != 0 || device is null)
            return false;

        device.GetId(out id);
        return true;
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
            if (!string.Equals(id, _targetId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"mic notify ignored stale id={id} muted={muted}");
                return;
            }

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
        else
        {
            _onReseed(muted != 0);
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
    /// <param name="wantMuted">
    /// true = make sure Meet is muted; false = unmuted; null = toggle (test menu).
    /// </param>
    public bool TrySyncMeetMute(bool? wantMuted, out string detail)
    {
        detail = "";
        if (!TryFindMeetWindow(out var hwnd, out detail))
        {
            Log.Write("Meet window not found. Browser titles:");
            LogBrowserTitles();
            return false;
        }

        // State-based UIA: click only the toolbar button that achieves the desired state.
        // Blind toggle caused "unmute works, mute doesn't" when Meet/Windows drifted apart.
        if (wantMuted is bool desired)
        {
            var action = TryApplyMeetMuteState(hwnd, desired, out var via);
            detail += $" [{via}]";
            Log.Write($"Meet sync wantMuted={desired}: {via}");
            return action;
        }

        // Explicit toggle (tray test / Lenovo OSD without mute-flag change):
        // click whichever self-mute toolbar button is visible, else Ctrl+D.
        if (TryClickEitherSelfMute(hwnd, out var toggleVia))
        {
            detail += $" [{toggleVia}]";
            Log.Write($"Meet toggle via UIA: {toggleVia}");
            return true;
        }

        if (EnsureForeground(hwnd))
        {
            Thread.Sleep(40);
            SendCtrlD();
            detail += " [Ctrl+D]";
            Log.Write("Meet toggle via Ctrl+D");
            return true;
        }

        SendCtrlD();
        detail += " [Ctrl+D-nofocus]";
        return true;
    }

    private static bool TryApplyMeetMuteState(IntPtr hwnd, bool wantMuted, out string via)
    {
        // Labels that appear when Meet is currently UNMUTED (click → mute).
        string[] muteLabels =
        [
            "Turn off microphone",
            "Выключить микрофон",
            "Mikrofon ausschalten"
        ];
        // Labels that appear when Meet is currently MUTED (click → unmute).
        string[] unmuteLabels =
        [
            "Turn on microphone",
            "Включить микрофон",
            "Mikrofon einschalten"
        ];

        var needClick = wantMuted ? muteLabels : unmuteLabels;
        var alreadyOk = wantMuted ? unmuteLabels : muteLabels;

        if (FindToolbarButton(hwnd, alreadyOk, out var present))
        {
            via = $"already-{(wantMuted ? "muted" : "unmuted")}:{present}";
            return true;
        }

        // Invoke does not need the Meet window to be focused, and it still
        // reaches the button when the fullscreen toolbar is hidden (offscreen).
        if (TryInvokeDesired(hwnd, needClick, alreadyOk, out via))
            return true;

        // No mouse clicks. The camera's "Video settings" arrow sits beside the
        // mic button and opens Backgrounds and effects; a click aimed at the mic
        // lands there often enough to matter.
        if (TryMuteViaCtrlD(hwnd, alreadyOk, out via))
        {
            if (!wantMuted)
                via = via.Replace("mute:", "unmute:");
            return true;
        }

        via = wantMuted ? "FAILED-mute" : "FAILED-unmute";
        return false;
    }

    /// <summary>
    /// UIA Invoke on the self-mute button. Works while another window is focused
    /// and while Meet is fullscreen. Restores the previous foreground window.
    /// </summary>
    private static bool TryInvokeDesired(IntPtr hwnd, string[] needClick, string[] alreadyOk, out string via)
    {
        via = "";
        if (!FindToolbarButton(hwnd, needClick, out var el, out var label) || el is null)
            return false;

        var prev = GetForegroundWindow();
        try
        {
            if (!el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pattern) ||
                pattern is not System.Windows.Automation.InvokePattern invoke)
            {
                Log.Write($"toolbar '{label}' has no Invoke pattern");
                return false;
            }

            invoke.Invoke();
            Log.Write($"toolbar invoke '{label}'");
            // A new meeting is slow to rename the button. Ctrl+D after a short
            // miss toggles it back and steals focus.
            if (WaitForToolbarButton(hwnd, alreadyOk, timeoutMs: 1200, out _))
            {
                via = $"invoke:{label}";
                return true;
            }

            Log.Write($"invoke '{label}' did not change Meet state");
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"invoke error: {ex.Message}");
            return false;
        }
        finally
        {
            RestoreForeground(prev);
        }
    }

    private static void RestoreForeground(IntPtr prev)
    {
        if (prev == IntPtr.Zero || GetForegroundWindow() == prev)
            return;
        if (SetForegroundWindow(prev) && GetForegroundWindow() == prev)
            return;
        EnsureForeground(prev);
    }

    /// <summary>
    /// Focus Meet page content (not just the Chrome frame) then send Ctrl+D.
    /// </summary>
    private static bool TryMuteViaCtrlD(IntPtr hwnd, string[] successLabels, out string via)
    {
        via = "";
        var prev = GetForegroundWindow();
        try
        {
            EnsureForeground(hwnd);
            // Focus the page without a mouse click. A click in the meeting
            // hits the self-view or the video-settings arrow and opens
            // Backgrounds and effects.
            FocusMeetPage(hwnd);
            Thread.Sleep(40);
            SendCtrlD();
            if (WaitForToolbarButton(hwnd, successLabels, timeoutMs: 700, out var okLabel))
            {
                via = $"mute:Ctrl+D-focused/{okLabel}";
                Log.Write($"Ctrl+D verified → {okLabel}");
                return true;
            }

            Log.Write("Ctrl+D did not verify Meet mute UI change");
            return false;
        }
        finally
        {
            RestoreForeground(prev);
        }
    }

    private static void FocusMeetPage(IntPtr hwnd)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null)
                return;

            var doc = root.FindFirst(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Document));
            (doc ?? root).SetFocus();
        }
        catch (Exception ex)
        {
            Log.Write($"focus page: {ex.Message}");
        }
    }

    private static bool TryClickEitherSelfMute(IntPtr hwnd, out string via)
    {
        string[] all =
        [
            "Turn off microphone", "Turn on microphone",
            "Выключить микрофон", "Включить микрофон",
            "Mikrofon ausschalten", "Mikrofon einschalten"
        ];
        if (!FindToolbarButton(hwnd, all, out var el, out var name) || el is null)
        {
            via = "";
            return false;
        }

        try
        {
            if (el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pattern) &&
                pattern is System.Windows.Automation.InvokePattern invoke)
                invoke.Invoke();
            else
            {
                via = "";
                return false;
            }
        }
        catch
        {
            via = "";
            return false;
        }

        via = $"UIA-toggle:{name}";
        return true;
    }

    private static bool WaitForToolbarButton(IntPtr hwnd, string[] names, int timeoutMs, out string foundName)
    {
        foundName = "";
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 <= deadline)
        {
            if (FindToolbarButton(hwnd, names, out foundName))
                return true;
            Thread.Sleep(40);
        }
        return false;
    }

    private static bool FindToolbarButton(IntPtr hwnd, string[] names, out string foundName)
        => FindToolbarButton(hwnd, names, out _, out foundName);

    private static bool FindToolbarButton(
        IntPtr hwnd,
        string[] names,
        out System.Windows.Automation.AutomationElement? element,
        out string foundName)
    {
        element = null;
        foundName = "";
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null)
                return false;

            System.Windows.Rect windowRect;
            try { windowRect = root.Current.BoundingRectangle; }
            catch { return false; }

            if (windowRect.IsEmpty || windowRect.Height <= 0)
                return false;

            // Only consider controls in the bottom toolbar band.
            var toolbarTop = windowRect.Top + windowRect.Height * 0.72;

            System.Windows.Automation.AutomationElement? best = null;
            var bestScore = int.MinValue;
            var bestName = "";

            foreach (var name in names)
            {
                var cond = new System.Windows.Automation.AndCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty, name),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Button));

                var matches = root.FindAll(System.Windows.Automation.TreeScope.Descendants, cond);
                foreach (System.Windows.Automation.AutomationElement el in matches)
                {
                    try
                    {
                        if (!el.Current.IsEnabled)
                            continue;

                        var offscreen = el.Current.IsOffscreen;
                        var rect = el.Current.BoundingRectangle;
                        var inToolbar = !rect.IsEmpty && rect.Height > 0 && rect.Top >= toolbarTop;
                        // The mic icon is the narrow button. The wider pill is "Audio
                        // settings" / "Video settings"; invoking or clicking that opens
                        // Backgrounds and effects.
                        var tight = !rect.IsEmpty && rect.Width is >= 36 and <= 100;
                        // Prefer the visible bottom-bar control. Fullscreen hides that bar,
                        // so an offscreen button with the same name is still a valid target.
                        var score = (offscreen ? 1 : inToolbar ? 3 : 2) * 10 + (tight ? 1 : 0);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = el;
                            bestName = name;
                        }
                    }
                    catch { /* stale UIA node */ }
                }
            }

            if (best is null)
                return false;

            element = best;
            foundName = bestName;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"UIA find error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
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

internal enum InputType : uint
{
    INPUT_MOUSE = 0,
    INPUT_KEYBOARD = 1
}

[Flags]
internal enum KeyEventF : uint
{
    KEYEVENTF_KEYUP = 0x0002,
    KEYEVENTF_SCANCODE = 0x0008
}

[Flags]
internal enum MouseEventF : uint
{
    MOVE = 0x0001,
    LEFTDOWN = 0x0002,
    LEFTUP = 0x0004,
    ABSOLUTE = 0x8000,
    VIRTUALDESK = 0x4000
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
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
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public MouseEventF dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
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
