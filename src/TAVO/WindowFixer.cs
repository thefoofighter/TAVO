namespace TAVO;

/// <summary>
/// Keeps each running window on its own version's pin: when a window of a badged app appears,
/// it gets the AppUserModelID of the matching pin. Runs on the UI thread (out-of-context WinEvent hook).
/// </summary>
internal sealed class WindowFixer : IDisposable
{
    readonly Native.WinEventProc _proc; // held in a field so the GC keeps the delegate alive while hooked
    IntPtr _hook;
    List<WindowRule> _rules = new();
    readonly HashSet<IntPtr> _touched = new();

    public WindowFixer() => _proc = OnWinEvent;

    public void Start()
    {
        if (!Resolver.Available) { Log.Warn("window fix off: resolver unavailable"); return; }
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Warn("SetWinEventHook failed; windows are fixed only on rescans");
    }

    /// <summary>Replaces the rules and re-checks every open window.</summary>
    public void SetRules(List<WindowRule> rules)
    {
        _rules = rules;
        _touched.RemoveWhere(h => !Native.IsWindow(h));
        if (_rules.Count == 0) return;
        foreach (var h in Win.TaskbarCandidates()) Fix(h);
    }

    /// <summary>Removes the IDs TAVO set on windows that are still open.</summary>
    public void ClearAll()
    {
        foreach (var h in _touched)
            if (Native.IsWindow(h)) Win.SetWindowAppId(h, null);
        _touched.Clear();
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF || hwnd == IntPtr.Zero) return;
        // An exception must not escape into the native callback.
        try { Fix(hwnd); }
        catch (Exception e) { Log.Error("window fix failed", e); }
    }

    void Fix(IntPtr hwnd)
    {
        if (_rules.Count == 0 || !Win.IsTaskbarCandidate(hwnd)) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;
        string? exe = Win.ExePath(pid);
        if (exe == null) return;

        var rule = _rules.FirstOrDefault(r => string.Equals(r.ExePath, exe, StringComparison.OrdinalIgnoreCase));
        if (rule == null)
        {
            // Launcher pins: the pin targets blender-launcher.exe, the window belongs to blender.exe in the same folder.
            string dir = Path.GetDirectoryName(exe) ?? "";
            var sameDir = _rules.Where(r => string.Equals(r.Dir, dir, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameDir.Count == 0) return;
            var id = AppIdentity.For(exe);
            if (id == null) return;
            rule = sameDir.FirstOrDefault(r => r.ProductKey == id.ProductKey);
            if (rule == null) return;
        }

        string? current = Win.GetWindowAppId(hwnd);
        if (current == rule.AppId) return;
        if (current == null && Resolver.ForWindow(hwnd) == rule.AppId) return; // already lands on the right pin

        if (Win.SetWindowAppId(hwnd, rule.AppId))
        {
            _touched.Add(hwnd);
            Log.Info($"window 0x{hwnd.ToInt64():X} {Path.GetFileName(exe)} -> {rule.AppId} ({rule.Label})");
        }
        else Log.Warn($"could not set the taskbar ID on window 0x{hwnd.ToInt64():X} ({Path.GetFileName(exe)})");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
}
