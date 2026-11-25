using System.Text.RegularExpressions;

namespace TAVO;

/// <param name="UniqueAppId">Set when pins of different versions share one taskbar ID and must be split.</param>
/// <param name="OrigResolvedAppId">The pin's resolved ID before TAVO changed anything.</param>
internal sealed record BadgePlan(string Label, Color Color, string? UniqueAppId, string? OrigResolvedAppId);

/// <summary>A running window of this exe (or of the same product in the same folder) belongs on the pin with AppId.</summary>
internal sealed record WindowRule(string ExePath, string Dir, string ProductKey, string AppId, string Label);

internal sealed class Plan
{
    public Dictionary<string, BadgePlan> Badges { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WindowRule> WindowRules { get; } = new();
    public List<string> Groups { get; } = new(); // "Blender: 5.0, 5.3"
}

internal static class Planner
{
    /// <summary>Badges every pin of a product that has two or more distinct pinned versions.</summary>
    public static Plan Build(List<Pin> pins, AppState state)
    {
        var plan = new Plan();
        foreach (var g in pins.Where(p => p.App != null && p.IsPinned).GroupBy(p => p.App!.ProductKey))
        {
            var members = g.ToList();
            var versions = members.Select(m => m.App!)
                .GroupBy(a => a.VersionKey).Select(x => x.First())
                .OrderBy(a => a.Version, VersionComparer.Instance).ThenBy(a => a.Tag, StringComparer.Ordinal)
                .ToList();
            if (versions.Count < 2) continue;

            var labels = Labels(versions);

            // The ID each pin had before TAVO touched it. Pins that resolve to one ID across versions collide.
            var origIds = members.ToDictionary(m => m.LnkPath, m => OriginalResolvedId(m, state), StringComparer.OrdinalIgnoreCase);
            var colliding = members
                .GroupBy(m => origIds[m.LnkPath] ?? "")
                .Where(x => x.Key.Length > 0 && x.Select(m => m.App!.VersionKey).Distinct().Count() > 1)
                .SelectMany(x => x).Select(m => m.LnkPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in members)
            {
                string label = labels[m.App!.VersionKey];
                var color = Icons.Palette[versions.FindIndex(v => v.VersionKey == m.App.VersionKey) % Icons.Palette.Length];
                string? unique = colliding.Contains(m.LnkPath) ? UniqueAppId(m.App) : null;
                plan.Badges[m.LnkPath] = new BadgePlan(label, color, unique, origIds[m.LnkPath]);

                string? windowId = unique ?? origIds[m.LnkPath];
                if (!string.IsNullOrEmpty(windowId))
                    plan.WindowRules.Add(new WindowRule(m.Target, Path.GetDirectoryName(m.Target) ?? "", g.Key, windowId, label));
            }
            plan.Groups.Add($"{versions[0].ProductName}: {string.Join(", ", versions.Select(v => labels[v.VersionKey]))}");
        }
        return plan;
    }

    static string? OriginalResolvedId(Pin pin, AppState state)
    {
        if (state.Pins.TryGetValue(pin.LnkPath, out var ps) && ps.AppIdChanged) return ps.OrigResolvedAppId;
        return Resolver.ForShortcut(pin.LnkPath) ?? pin.LnkAppId;
    }

    /// <summary>
    /// Per version: major.minor. A version whose major.minor another pinned version shares gets a third part
    /// (a fourth if that still clashes). Versions with equal numbers fall back to major.minor plus a short
    /// prerelease tag (alpha3 -> a3). Example: 5.3.1, 5.3.2, 5.8 -> "5.3.1", "5.3.2", "5.8".
    /// </summary>
    public static Dictionary<string, string> Labels(List<AppIdentity> versions)
    {
        var map = new Dictionary<string, string>();
        foreach (var v in versions)
        {
            string? label = null;
            for (int k = 2; k <= 4 && label == null; k++)
            {
                string mine = Num(v.Version, k);
                if (!versions.Any(o => o.VersionKey != v.VersionKey && Num(o.Version, k) == mine)) label = mine;
            }
            map[v.VersionKey] = label ?? Num(v.Version, 2) + ShortTag(v.Tag);
        }
        // Labels with different part counts cannot match, so a clash is only possible in odd tag cases.
        foreach (var dup in map.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).SelectMany(g => g).ToList())
            map[dup.Key] = dup.Key;
        return map;
    }

    static string Num(int[] v, int k) => string.Join(".", v.Take(k));

    static string ShortTag(string tag)
    {
        if (tag.Length == 0) return "";
        var m = Regex.Match(tag, @"^([a-z]+)(\d*)$");
        if (!m.Success) return tag;
        string word = m.Groups[1].Value switch { "alpha" => "a", "beta" => "b", "preview" or "pre" => "p", var w => w };
        return word + m.Groups[2].Value;
    }

    static string UniqueAppId(AppIdentity app)
    {
        string product = Regex.Replace(app.ProductName, "[^A-Za-z0-9]", "");
        string tag = Regex.Replace(app.Tag, "[^A-Za-z0-9]", "");
        string id = $"TAVO.{product}.{string.Join(".", app.Version)}" + (tag.Length > 0 ? "." + tag : "");
        return id.Length <= 128 ? id : id[..128];
    }
}

internal sealed class VersionComparer : IComparer<int[]>
{
    public static readonly VersionComparer Instance = new();
    public int Compare(int[]? a, int[]? b)
    {
        if (a == null || b == null) return (a == null).CompareTo(b == null);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }
}
