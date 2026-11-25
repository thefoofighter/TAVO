using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TAVO;

/// <summary>Product identity and version of one executable.</summary>
internal sealed class AppIdentity
{
    public required string ExePath { get; init; }
    /// <summary>Grouping key: ProductName|FileDescription, lower case. Launcher and main exe of one install share it.</summary>
    public required string ProductKey { get; init; }
    public required string ProductName { get; init; }
    public required int[] Version { get; init; } // always 4 parts
    public required string Tag { get; init; }    // prerelease tag such as "alpha3", or ""

    public string VersionKey => string.Join(".", Version) + (Tag.Length > 0 ? "-" + Tag : "");

    static readonly Dictionary<string, (long stamp, AppIdentity? id)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Regex VersionRx = new(@"(\d+)[._](\d+)(?:[._](\d+))?(?:[._](\d+))?", RegexOptions.Compiled);
    static readonly Regex TagRx = new(@"(?<![A-Za-z])(alpha|beta|preview|pre|rc)[ ._-]?(\d*)(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Cached by path and file time. Returns null when the file has no readable version resource.</summary>
    public static AppIdentity? For(string exePath)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(exePath).Ticks; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (Cache.TryGetValue(exePath, out var hit) && hit.stamp == stamp) return hit.id;
        var id = Read(exePath);
        Cache[exePath] = (stamp, id);
        return id;
    }

    static AppIdentity? Read(string exePath)
    {
        FileVersionInfo fvi;
        try { fvi = FileVersionInfo.GetVersionInfo(exePath); }
        catch (FileNotFoundException) { return null; }

        string prod = (fvi.ProductName ?? "").Trim();
        string desc = (fvi.FileDescription ?? "").Trim();
        string exe = NormalizeExeName(Path.GetFileNameWithoutExtension(exePath));
        string key = prod.Length + desc.Length > 0 ? $"{prod}|{desc}".ToLowerInvariant() : exe.ToLowerInvariant();

        int[] v = { fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart };
        if (v.All(x => x == 0))
            v = ParseVersion(fvi.ProductVersion) ?? ParseVersion(fvi.FileVersion) ?? ParseFromDirs(exePath, ParseVersion) ?? v;
        string tag = ParseTag(fvi.ProductVersion) ?? ParseTag(fvi.FileVersion) ?? ParseFromDirs(exePath, ParseTag) ?? "";

        return new AppIdentity
        {
            ExePath = exePath, ProductKey = key, ProductName = prod.Length > 0 ? prod : exe, Version = v, Tag = tag,
        };
    }

    internal static string NormalizeExeName(string name)
    {
        foreach (var suffix in new[] { "-launcher", "_launcher", " launcher", "launcher" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        return name;
    }

    internal static int[]? ParseVersion(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = VersionRx.Match(s);
        if (!m.Success) return null;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (m.Groups[i + 1].Success && int.TryParse(m.Groups[i + 1].Value, out int n)) v[i] = n;
        return v;
    }

    internal static string? ParseTag(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = TagRx.Match(s);
        return m.Success ? (m.Groups[1].Value + m.Groups[2].Value).ToLowerInvariant() : null;
    }

    /// <summary>Searches the exe's folder and up to three parent folder names (e.g. "UE_5.3", "Kanzi 4_2_0_1146_alpha3").</summary>
    static T? ParseFromDirs<T>(string exePath, Func<string?, T?> parse) where T : class
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath) ?? "");
        for (int i = 0; i < 4 && dir?.Parent != null; i++, dir = dir.Parent)
        {
            var r = parse(dir.Name);
            if (r != null) return r;
        }
        return null;
    }
}

/// <summary>One pinned taskbar shortcut.</summary>
internal sealed class Pin
{
    public required string LnkPath { get; init; }
    public required string Name { get; init; }
    public required string Target { get; init; }
    public required string IconPath { get; init; }
    public required int IconIndex { get; init; }
    public string? LnkAppId { get; init; }
    /// <summary>False for a shortcut the taskbar pin list no longer references.</summary>
    public bool IsPinned { get; init; } = true;
    public AppIdentity? App { get; init; }
}

internal static class PinScanner
{
    public static string PinRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
    public static string TaskBarDir => Path.Combine(PinRoot, "TaskBar");
    public static string ImplicitDir => Path.Combine(PinRoot, "ImplicitAppShortcuts");

    public const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    public static List<Pin> Scan()
    {
        var list = new List<Pin>();
        var referenced = TaskbandStrings();
        foreach (var path in PinFiles())
        {
            // A shortcut can stay in the folder after unpinning. It is still scanned, so an old badge gets removed.
            bool pinned = referenced == null || IsReferenced(path, referenced);
            try { list.Add(Read(path, pinned)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                Log.Warn($"skipped pin {path}: {e.Message}");
            }
        }
        return list;
    }

    /// <summary>
    /// Text found in the taskbar's pin list (Taskband Favorites/FavoritesResolve), lower case.
    /// ImplicitAppShortcuts keeps shortcuts after an app is unpinned, so only shortcuts named here are pins.
    /// Returns null when the key cannot be read; callers then treat every shortcut as a pin.
    /// </summary>
    static List<string>? TaskbandStrings()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(TaskbandKey);
            if (key == null) return null;
            var found = new List<string>();
            foreach (var name in new[] { "Favorites", "FavoritesResolve" })
            {
                if (key.GetValue(name) is not byte[] b || b.Length < 2) continue;
                foreach (var text in new[] { System.Text.Encoding.Unicode.GetString(b), System.Text.Encoding.Unicode.GetString(b, 1, b.Length - 1) })
                    foreach (Match m in Regex.Matches(text, @"[\x20-\x7E]{3,}"))
                        found.Add(m.Value.ToLowerInvariant());
            }
            return found;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"cannot read the taskbar pin list: {e.Message}");
            return null;
        }
    }

    internal static bool IsReferenced(string lnkPath, List<string> referenced)
    {
        string name = Path.GetFileName(lnkPath).ToLowerInvariant();
        if (!lnkPath.StartsWith(ImplicitDir, StringComparison.OrdinalIgnoreCase))
            return referenced.Any(s => s.EndsWith(name, StringComparison.Ordinal));
        // Implicit shortcuts: FavoritesResolve holds "...\ImplicitAppShortcuts\<hash>\<name>.lnk";
        // Favorites holds the folder hash and the file name as separate ID-list items.
        string hash = (Path.GetFileName(Path.GetDirectoryName(lnkPath)) ?? "").ToLowerInvariant();
        string tail = hash + "\\" + name;
        return referenced.Any(s => s.EndsWith(tail, StringComparison.Ordinal))
               || (referenced.Contains(hash) && referenced.Any(s => s.EndsWith(name, StringComparison.Ordinal)));
    }

    static IEnumerable<string> PinFiles()
    {
        if (Directory.Exists(TaskBarDir))
            foreach (var f in Directory.EnumerateFiles(TaskBarDir, "*.lnk")) yield return f;
        if (Directory.Exists(ImplicitDir))
            foreach (var f in Directory.EnumerateFiles(ImplicitDir, "*.lnk", SearchOption.AllDirectories)) yield return f;
    }

    static Pin Read(string path, bool pinned)
    {
        var link = Lnk.Load(path, false);
        try
        {
            var (target, iconPath, iconIdx) = Lnk.Read(link);
            string? appId = Lnk.GetString((IPropertyStore)link, Native.PKEY_AppUserModel_ID);
            bool isExe = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target);
            return new Pin
            {
                LnkPath = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Target = target,
                IconPath = iconPath,
                IconIndex = iconIdx,
                LnkAppId = appId,
                IsPinned = pinned,
                App = isExe ? AppIdentity.For(target) : null,
            };
        }
        finally { Lnk.Release(link); }
    }
}
