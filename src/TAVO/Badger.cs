using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TAVO;

/// <summary>Writes badged icons and points pins at them. Restores originals for pins that no longer need a badge.</summary>
internal static class Badger
{
    /// <summary>Badged icons live next to the log and state: %LOCALAPPDATA%\TAVO\icons.</summary>
    public static readonly string IconDir = Path.Combine(Log.Dir, "icons");

    static readonly Regex OwnIconName = new(@"-[0-9A-F]{8}\.ico$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool IsOurs(string iconPath) =>
        iconPath.StartsWith(IconDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings every pin in line with the plan. With enabled=false, every badged pin is restored.
    /// Returns true when a shortcut file changed. The taskbar shows the change after explorer restarts.
    /// </summary>
    public static bool Apply(List<Pin> pins, Plan plan, AppState state, bool enabled)
    {
        bool changed = false;
        foreach (var pin in pins)
        {
            try
            {
                if (enabled && plan.Badges.TryGetValue(pin.LnkPath, out var badge))
                    changed |= ApplyBadge(pin, badge, state);
                else
                    changed |= Restore(pin, state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
            {
                Log.Error($"pin '{pin.Name}' not updated", e);
            }
        }

        var present = pins.Select(p => p.LnkPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in state.Pins.Keys.Where(k => !present.Contains(k)).ToList())
        {
            state.Pins.Remove(gone);
            Log.Info($"pin removed, forgetting it: {gone}");
        }
        state.Save();
        return changed;
    }

    static bool ApplyBadge(Pin pin, BadgePlan badge, AppState state)
    {
        state.Pins.TryGetValue(pin.LnkPath, out var ps);
        bool ours = IsOurs(pin.IconPath);
        if (ps != null && !ours)
        {
            // Someone set a different icon since the last run. That icon becomes the new original.
            Log.Info($"pin '{pin.Name}': icon changed outside TAVO; using it as the new original");
            ps.OrigIconPath = pin.IconPath;
            ps.OrigIconIndex = pin.IconIndex;
            ps.IcoPath = "";
        }
        if (ps == null)
        {
            if (ours) Log.Warn($"pin '{pin.Name}' uses a TAVO icon but no original is saved; the exe icon becomes the original");
            ps = new PinState { OrigIconPath = ours ? "" : pin.IconPath, OrigIconIndex = ours ? 0 : pin.IconIndex };
        }

        string src = ps.OrigIconPath.Length == 0 ? pin.Target : Environment.ExpandEnvironmentVariables(ps.OrigIconPath);
        int srcIdx = ps.OrigIconIndex;
        string stamp = $"{src}|{srcIdx}|{FileStamp(src)}";

        string icoPath = ps.IcoPath;
        bool iconFresh = ps.Label == badge.Label && ps.ColorArgb == badge.Color.ToArgb() && ps.SourceStamp == stamp
                         && icoPath.Length > 0 && File.Exists(icoPath);
        if (!iconFresh)
        {
            byte[]? bytes = Icons.BuildBadgedIco(src, srcIdx, badge.Label, badge.Color);
            if (bytes == null && !string.Equals(src, pin.Target, StringComparison.OrdinalIgnoreCase))
                bytes = Icons.BuildBadgedIco(pin.Target, 0, badge.Label, badge.Color);
            if (bytes == null)
            {
                Log.Warn($"pin '{pin.Name}': no usable icon in {src},{srcIdx}; not badged");
                return false;
            }
            icoPath = WriteIcon(pin.Name, bytes);
        }

        bool iconDiff = !string.Equals(pin.IconPath, icoPath, StringComparison.OrdinalIgnoreCase) || pin.IconIndex != 0;
        bool idDiff = badge.UniqueAppId != null ? pin.LnkAppId != badge.UniqueAppId : ps.AppIdChanged;

        // Save the originals before the shortcut changes, so a crash cannot lose them.
        if (badge.UniqueAppId != null && !ps.AppIdChanged)
        {
            ps.OrigAppId = pin.LnkAppId;
            ps.OrigResolvedAppId = badge.OrigResolvedAppId;
        }
        ps.IcoPath = icoPath;
        ps.Label = badge.Label;
        ps.ColorArgb = badge.Color.ToArgb();
        ps.SourceStamp = stamp;
        state.Pins[pin.LnkPath] = ps;
        if (!iconDiff && !idDiff) return false;
        state.Save();

        var link = Lnk.Load(pin.LnkPath, true);
        try
        {
            if (iconDiff) Lnk.Check(link.SetIconLocation(icoPath, 0), "SetIconLocation");
            if (idDiff)
            {
                var store = (IPropertyStore)link;
                Lnk.Check(Lnk.SetString(store, Native.PKEY_AppUserModel_ID, badge.UniqueAppId ?? ps.OrigAppId), "set AppUserModel.ID");
                Lnk.Check(store.Commit(), "commit AppUserModel.ID");
            }
            Lnk.Save(link);
        }
        finally { Lnk.Release(link); }

        if (idDiff)
        {
            ps.AppIdChanged = badge.UniqueAppId != null;
            if (!ps.AppIdChanged) { ps.OrigAppId = null; ps.OrigResolvedAppId = null; }
        }
        Lnk.NotifyChanged(pin.LnkPath);
        Log.Info($"pin '{pin.Name}': badge \"{badge.Label}\"" + (idDiff ? $", taskbar ID {badge.UniqueAppId ?? "restored"}" : ""));
        return true;
    }

    static bool Restore(Pin pin, AppState state)
    {
        state.Pins.TryGetValue(pin.LnkPath, out var ps);
        bool ours = IsOurs(pin.IconPath);
        if (ps == null && !ours) return false;
        if (ps == null) Log.Warn($"pin '{pin.Name}' uses a TAVO icon but no original is saved; restoring the exe icon");

        bool restoreIcon = ours; // an icon set outside TAVO stays as it is
        bool restoreId = ps?.AppIdChanged == true;
        if (restoreIcon || restoreId)
        {
            var link = Lnk.Load(pin.LnkPath, true);
            try
            {
                if (restoreIcon) Lnk.Check(link.SetIconLocation(ps?.OrigIconPath ?? "", ps?.OrigIconIndex ?? 0), "SetIconLocation");
                if (restoreId)
                {
                    var store = (IPropertyStore)link;
                    Lnk.Check(Lnk.SetString(store, Native.PKEY_AppUserModel_ID, ps!.OrigAppId), "restore AppUserModel.ID");
                    Lnk.Check(store.Commit(), "commit AppUserModel.ID");
                }
                Lnk.Save(link);
            }
            finally { Lnk.Release(link); }
            Lnk.NotifyChanged(pin.LnkPath);
            Log.Info($"pin '{pin.Name}': original icon restored");
        }
        state.Pins.Remove(pin.LnkPath);
        return restoreIcon || restoreId;
    }

    static string WriteIcon(string pinName, byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA1.HashData(bytes))[..8];
        string safe = Regex.Replace(pinName, @"[^\w.\- ]", "_").Trim();
        if (safe.Length > 60) safe = safe[..60];
        Directory.CreateDirectory(IconDir);
        string path = Path.Combine(IconDir, $"{safe}-{hash}.ico");
        if (!File.Exists(path))
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
        }
        return path;
    }

    /// <summary>
    /// Deletes TAVO icons that no pin uses. Call only when no explorer restart is pending:
    /// until then the taskbar may still read the old files.
    /// </summary>
    public static void CleanIcons(AppState state)
    {
        if (!Directory.Exists(IconDir)) return;
        var keep = state.Pins.Values.Select(p => p.IcoPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(IconDir, "*.ico"))
        {
            if (!OwnIconName.IsMatch(Path.GetFileName(f)) || keep.Contains(f)) continue;
            try { File.Delete(f); Log.Info($"deleted unused icon {f}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"could not delete {f}: {e.Message}"); }
        }
    }

    static long FileStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }
}
