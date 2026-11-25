using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace TAVO;

internal static class ExplorerRestarter
{
    /// <summary>
    /// Restarts the shell explorer.exe (the one that owns the taskbar). Other explorer processes are left alone.
    /// Blocks for up to about 25 seconds; do not call on the UI thread. Returns true when a new taskbar is up.
    /// </summary>
    /// <remarks>
    /// The shell keeps its image list in %LOCALAPPDATA%\IconCache.db. It saves the file on a clean exit
    /// (sign-out, shutdown, "Exit Explorer") and reloads it at start, so taskbar pins can keep old images.
    /// This method deletes the file and then terminates the shell; a terminated shell does not save the file,
    /// so the new shell builds a fresh image list. Observed and fixed on build 26300.
    /// </remarks>
    public static bool Restart()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Log.Error("refusing to restart explorer from an elevated process: the new shell would run elevated");
            return false;
        }

        IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            Log.Warn("no taskbar window; starting explorer");
            return StartShell(0);
        }
        Native.GetWindowThreadProcessId(tray, out uint oldPid);

        Process shell;
        try { shell = Process.GetProcessById((int)oldPid); }
        catch (ArgumentException) { return WaitForTaskbar(oldPid, 5000) || StartShell(oldPid); }

        DeleteShellIconCache(); // the shell has the file closed while it runs; it reads it only at start

        using (shell)
        {
            Log.Info($"terminating explorer (pid {oldPid})");
            try
            {
                shell.Kill();
                if (!shell.WaitForExit(5000)) { Log.Error("explorer did not terminate within 5 s"); return false; }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Error("could not terminate explorer", e);
                return false;
            }
        }

        // Winlogon restarts a terminated shell by itself. A clean exit needs a manual start.
        if (WaitForTaskbar(oldPid, 4000)) { Log.Info("explorer restarted"); return true; }
        return StartShell(oldPid);
    }

    static void DeleteShellIconCache()
    {
        string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconCache.db");
        try
        {
            if (!File.Exists(db)) return;
            File.Delete(db);
            Log.Info("deleted IconCache.db; the shell rebuilds it");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not delete IconCache.db ({e.Message}); taskbar pins may keep old images");
        }
    }

    static bool StartShell(uint oldPid)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        bool ok = WaitForTaskbar(oldPid, 15000);
        if (ok) Log.Info("explorer started"); else Log.Error("explorer did not come back within 15 s");
        return ok;
    }

    static bool WaitForTaskbar(uint oldPid, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr t = Native.FindWindowW("Shell_TrayWnd", null);
            if (t != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(t, out uint pid);
                if (pid != 0 && pid != oldPid) return true;
            }
            Thread.Sleep(250);
        }
        return false;
    }
}

/// <summary>Raises a callback when the taskbar is (re)created, e.g. after explorer restarts.</summary>
internal sealed class ShellRestartWatcher : NativeWindow, IDisposable
{
    readonly uint _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
    readonly Action _onRestart;

    public ShellRestartWatcher(Action onRestart)
    {
        _onRestart = onRestart;
        // A hidden top-level window: message-only windows do not get the TaskbarCreated broadcast.
        CreateHandle(new CreateParams { Caption = "TAVO.ShellWatcher" });
    }

    protected override void WndProc(ref Message m)
    {
        if (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated)
        {
            try { _onRestart(); }
            catch (Exception e) { Log.Error("taskbar-created handler failed", e); }
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

/// <summary>Calls back (on a worker thread) when a value under an HKCU key changes.</summary>
internal sealed class RegistryWatcher : IDisposable
{
    const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, bool watchSubtree, uint filter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle hEvent, bool async);

    readonly ManualResetEvent _stop = new(false);

    public RegistryWatcher(string subKey, Action onChange)
    {
        var t = new Thread(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            if (key == null) { Log.Warn($"registry watch: HKCU\\{subKey} not found"); return; }
            using var changed = new AutoResetEvent(false);
            while (true)
            {
                // The notification is tied to this thread, so the thread must live as long as the watch.
                int rc = RegNotifyChangeKeyValue(key.Handle, false, REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);
                if (rc != 0) { Log.Warn($"registry watch failed (error {rc}); relying on other triggers"); return; }
                if (WaitHandle.WaitAny(new WaitHandle[] { changed, _stop }) == 1) return;
                try { onChange(); }
                catch (Exception e) { Log.Error("registry change handler failed", e); }
            }
        }) { IsBackground = true, Name = "TAVO.RegistryWatch" };
        t.Start();
    }

    public void Dispose() => _stop.Set();
}

internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "TAVO";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, false);
        Log.Info($"start with Windows: {(on ? "on" : "off")}");
    }
}
