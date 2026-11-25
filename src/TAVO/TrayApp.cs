namespace TAVO;

internal sealed class TrayApp : ApplicationContext
{
    readonly AppState _state;
    readonly Control _ui = new(); // owns a UI-thread handle for BeginInvoke from watcher threads
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new() { ShowItemToolTips = true };
    readonly ToolStripMenuItem _refresh, _enabled, _autostart;
    readonly List<ToolStripItem> _infoItems = new();
    readonly Font _regularFont, _boldFont;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 1500 };
    readonly System.Windows.Forms.Timer _periodic = new() { Interval = 10 * 60 * 1000 };
    readonly WindowFixer _fixer = new();
    readonly ShellRestartWatcher _shellWatch;
    FileSystemWatcher? _watcher;
    RegistryWatcher? _pinListWatcher;
    Plan _plan = new();
    bool _restarting;
    bool Pending => _state.RefreshPending;

    public TrayApp()
    {
        _ = _ui.Handle;
        _state = AppState.Load();

        _refresh = new ToolStripMenuItem("Refresh explorer", null, (_, _) => RefreshExplorer())
        {
            ToolTipText = "Restart Windows Explorer and reset its icon cache, so the taskbar shows badge changes.\nOpen File Explorer windows close.",
        };
        var rescan = new ToolStripMenuItem("Rescan now", null, (_, _) => Rescan("manual"));
        _enabled = new ToolStripMenuItem("Show version badges", null, (_, _) => ToggleEnabled()) { Checked = _state.Enabled };
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());
        var openLog = new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder());
        var about = new ToolStripMenuItem($"TAVO {Program.Version} on GitHub", null, (_, _) => OpenUrl(Program.ProjectUrl));
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripSeparator(), _refresh, rescan, new ToolStripSeparator(),
            _enabled, _autostart, openLog, new ToolStripSeparator(), about, exit,
        });
        _regularFont = new Font(_menu.Font, FontStyle.Regular);
        _boldFont = new Font(_menu.Font, FontStyle.Bold);

        _tray = new NotifyIcon { Icon = Icons.TrayIcon(), Text = "TAVO", ContextMenuStrip = _menu, Visible = true };

        if (!_state.AutostartInitialized)
        {
            TrySetAutostart(true); // the tool is meant to run always; the menu turns it off
            _state.AutostartInitialized = true;
            _state.Save();
        }
        _autostart.Checked = Autostart.IsEnabled();

        _debounce.Tick += (_, _) => { _debounce.Stop(); Rescan("pins changed"); };
        _periodic.Tick += (_, _) => Rescan("periodic"); // also catches in-place app updates (same path, new version)
        _periodic.Start();
        StartWatcher();
        _pinListWatcher = new RegistryWatcher(PinScanner.TaskbandKey, Kick); // unpinning an implicit pin changes only this key
        _fixer.Start();
        _shellWatch = new ShellRestartWatcher(OnShellRestarted);
        Log.Info($"TAVO started, icon folder {Badger.IconDir}");
        bool pendingFromLastSession = Pending;
        Rescan("startup");
        if (pendingFromLastSession)
            _tray.ShowBalloonTip(6000, "TAVO", "Some version badges are not on the taskbar yet. Right-click the TAVO tray icon and choose Refresh explorer.", ToolTipIcon.Info);
    }

    void Rescan(string reason)
    {
        try
        {
            var pins = PinScanner.Scan();
            _plan = Planner.Build(pins, _state);
            bool changed = Badger.Apply(pins, _plan, _state, _state.Enabled);
            if (changed) MarkPending();
            else if (!Pending) Badger.CleanIcons(_state);
            _fixer.SetRules(_state.Enabled ? _plan.WindowRules : new List<WindowRule>());
            Log.Info($"scan ({reason}): {pins.Count} pins, {_plan.Badges.Count} badged, shortcuts changed: {changed}");
        }
        catch (Exception e)
        {
            // Top of a timer or menu callback: log and keep the tray app alive. The next scan retries.
            Log.Error($"scan ({reason}) failed", e);
        }
        UpdateUi();
    }

    void MarkPending()
    {
        if (Pending) return;
        _state.RefreshPending = true;
        _state.Save();
        _tray.ShowBalloonTip(6000, "TAVO", "Version badges changed. Right-click the TAVO tray icon and choose Refresh explorer to show them.", ToolTipIcon.Info);
    }

    // A taskbar restart by someone else does not reset the shell's icon cache, so it does not clear the pending flag.
    void OnShellRestarted() => Log.Info("taskbar recreated");

    void UpdateUi()
    {
        foreach (var it in _infoItems) { _menu.Items.Remove(it); it.Dispose(); }
        _infoItems.Clear();

        var lines = !_state.Enabled ? new List<string> { "Version badges are off" }
            : _plan.Groups.Count > 0 ? new List<string>(_plan.Groups)
            : new List<string> { "No app has two pinned versions" };
        if (Pending) lines.Add("Changes show after Refresh explorer");
        for (int i = 0; i < lines.Count; i++)
        {
            var item = new ToolStripMenuItem(lines[i]) { Enabled = false };
            _menu.Items.Insert(i, item);
            _infoItems.Add(item);
        }
        _refresh.Font = Pending ? _boldFont : _regularFont;

        string tip = "TAVO" + (_state.Enabled && _plan.Groups.Count > 0 ? "\n" + string.Join("\n", _plan.Groups) : "")
                     + (Pending ? "\nRefresh explorer to show changes" : "");
        _tray.Text = tip.Length <= 127 ? tip : tip[..127];
    }

    async void RefreshExplorer()
    {
        if (_restarting) return;
        _restarting = true;
        _refresh.Enabled = false;
        try
        {
            bool ok = await Task.Run(ExplorerRestarter.Restart);
            if (ok)
            {
                _state.RefreshPending = false;
                _state.Save();
                Badger.CleanIcons(_state);
                UpdateUi();
            }
            else _tray.ShowBalloonTip(6000, "TAVO", "Explorer did not restart cleanly. See the log.", ToolTipIcon.Warning);
        }
        catch (Exception e)
        {
            // async void handler: an escaping exception would end the process.
            Log.Error("refresh explorer failed", e);
        }
        finally
        {
            _restarting = false;
            _refresh.Enabled = true;
        }
    }

    void ToggleEnabled()
    {
        _state.Enabled = !_state.Enabled;
        _enabled.Checked = _state.Enabled;
        _state.Save();
        if (!_state.Enabled) _fixer.ClearAll();
        Rescan(_state.Enabled ? "badges on" : "badges off");
    }

    void ToggleAutostart()
    {
        TrySetAutostart(!Autostart.IsEnabled());
        _autostart.Checked = Autostart.IsEnabled();
    }

    static void TrySetAutostart(bool on)
    {
        try { Autostart.Set(on); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("could not change start-with-Windows", e);
        }
    }

    static void OpenLogFolder()
    {
        Directory.CreateDirectory(Log.Dir);
        OpenUrl(Log.Dir);
    }

    static void OpenUrl(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) { Log.Error($"could not open {target}", e); }
    }

    void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(PinScanner.PinRoot, "*.lnk")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            _watcher.Created += (_, _) => Kick();
            _watcher.Changed += (_, _) => Kick();
            _watcher.Deleted += (_, _) => Kick();
            _watcher.Renamed += (_, _) => Kick();
            _watcher.Error += (_, e) => { Log.Warn($"pin watcher error: {e.GetException().Message}"); Kick(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or FileNotFoundException)
        {
            Log.Error("cannot watch the pin folders; relying on periodic scans", e);
        }
    }

    // Called on a thread-pool thread. Our own shortcut writes also land here; the follow-up scan is a no-op.
    void Kick()
    {
        try { _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); }); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { /* the app is exiting */ }
    }

    protected override void ExitThreadCore()
    {
        Log.Info("TAVO exiting");
        _tray.Visible = false;
        _tray.Dispose();
        _watcher?.Dispose();
        _pinListWatcher?.Dispose();
        _fixer.Dispose();
        _shellWatch.Dispose();
        _debounce.Dispose();
        _periodic.Dispose();
        _menu.Dispose();
        _regularFont.Dispose();
        _boldFont.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
