using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TAVO;

/// <summary>Shortcut (.lnk) and property-store helpers. Call from an STA thread.</summary>
internal static class Lnk
{
    public static IShellLinkW Load(string path, bool write)
    {
        var link = (IShellLinkW)new CShellLink();
        ((IPersistFile)link).Load(path, (int)(write ? Native.STGM_READWRITE : Native.STGM_READ));
        return link;
    }

    public static (string target, string iconPath, int iconIndex) Read(IShellLinkW l)
    {
        var sb = new StringBuilder(1024);
        l.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
        string target = sb.ToString();
        sb.Clear();
        l.GetIconLocation(sb, sb.Capacity, out int idx);
        return (target, sb.ToString(), idx);
    }

    public static void Save(IShellLinkW l) => ((IPersistFile)l).Save(null, true);

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    public static string? GetString(IPropertyStore ps, PropertyKey key)
    {
        if (ps.GetValue(ref key, out var pv) < 0) return null;
        try { return pv.vt == Native.VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { Native.PropVariantClear(ref pv); }
    }

    /// <summary>Sets a string property; null clears it (VT_EMPTY). Caller must Commit.</summary>
    public static int SetString(IPropertyStore ps, PropertyKey key, string? value)
    {
        var pv = new PropVariant();
        if (value != null) { pv.vt = Native.VT_LPWSTR; pv.p = Marshal.StringToCoTaskMemUni(value); }
        try { return ps.SetValue(ref key, ref pv); }
        finally { Native.PropVariantClear(ref pv); }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (hr=0x{hr:X8})", hr);
    }

    public static void NotifyChanged(string path) =>
        Native.SHChangeNotify(Native.SHCNE_UPDATEITEM, Native.SHCNF_PATHW, path, IntPtr.Zero);
}

internal static class Win
{
    /// <summary>Top-level, unowned windows that can own a taskbar button.</summary>
    public static bool IsTaskbarCandidate(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return false;
        if (Native.GetAncestor(h, Native.GA_ROOT) != h) return false;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        return (ex & Native.WS_EX_TOOLWINDOW) == 0 || (ex & Native.WS_EX_APPWINDOW) != 0;
    }

    public static List<IntPtr> TaskbarCandidates()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { if (IsTaskbarCandidate(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static string? ExePath(uint pid)
    {
        IntPtr hp = Native.OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            return Native.QueryFullProcessImageNameW(hp, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(hp); }
    }

    public static string? GetWindowAppId(IntPtr h)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (Native.SHGetPropertyStoreForWindow(h, ref iid, out var ps) < 0) return null;
        try { return Lnk.GetString(ps, Native.PKEY_AppUserModel_ID); }
        finally { Lnk.Release(ps); }
    }

    /// <summary>Sets (or clears, with null) the window's AppUserModelID. Works on other processes' windows.</summary>
    public static bool SetWindowAppId(IntPtr h, string? id)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (Native.SHGetPropertyStoreForWindow(h, ref iid, out var ps) < 0) return false;
        try
        {
            if (Lnk.SetString(ps, Native.PKEY_AppUserModel_ID, id) < 0) return false;
            return ps.Commit() >= 0;
        }
        finally { Lnk.Release(ps); }
    }
}

/// <summary>Wraps the undocumented application resolver. Returns null when it is unavailable or fails.</summary>
internal static class Resolver
{
    static IApplicationResolver8? _r;
    static bool _tried;

    public static bool Available => Get() != null;

    static IApplicationResolver8? Get()
    {
        if (_tried) return _r;
        _tried = true;
        try
        {
            var t = Type.GetTypeFromCLSID(Native.CLSID_AppResolver, true)!;
            _r = (IApplicationResolver8)Activator.CreateInstance(t)!;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or TypeLoadException)
        {
            Log.Error("application resolver unavailable; window grouping fix disabled", e);
        }
        return _r;
    }

    static string? Take(int hr, IntPtr p)
    {
        if (hr < 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? ForShortcut(string lnkPath)
    {
        var r = Get();
        if (r == null) return null;
        var iid = Native.IID_IShellItem;
        if (Native.SHCreateItemFromParsingName(lnkPath, IntPtr.Zero, ref iid, out var psi) < 0) return null;
        try { return Take(r.GetAppIDForShortcut(psi, out var p), p); }
        finally { Marshal.Release(psi); }
    }

    public static string? ForWindow(IntPtr h)
    {
        var r = Get();
        if (r == null) return null;
        return Take(r.GetAppIDForWindow(h, out var p, out _, out _, out _), p);
    }
}
