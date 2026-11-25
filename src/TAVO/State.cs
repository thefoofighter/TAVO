using System.Text.Json;

namespace TAVO;

/// <summary>What TAVO changed on one pin, so it can be restored.</summary>
internal sealed class PinState
{
    public string OrigIconPath { get; set; } = "";
    public int OrigIconIndex { get; set; }
    public bool AppIdChanged { get; set; }
    public string? OrigAppId { get; set; }
    /// <summary>The ID Windows resolved for the pin before TAVO changed it. Used for collision checks.</summary>
    public string? OrigResolvedAppId { get; set; }
    public string IcoPath { get; set; } = "";
    public string Label { get; set; } = "";
    public int ColorArgb { get; set; }
    public string SourceStamp { get; set; } = "";
}

internal sealed class AppState
{
    public bool Enabled { get; set; } = true;
    public bool AutostartInitialized { get; set; }
    /// <summary>
    /// Shortcuts changed since the last TAVO refresh. A sign-out does not clear this: the shell saves its
    /// old images at sign-out and reloads them at sign-in.
    /// </summary>
    public bool RefreshPending { get; set; }
    public Dictionary<string, PinState> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static string FilePath => Path.Combine(Log.Dir, "state.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppState Load()
    {
        if (!File.Exists(FilePath)) return new AppState();
        try
        {
            var s = JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Opts);
            if (s != null)
            {
                s.Pins = new Dictionary<string, PinState>(s.Pins ?? new(), StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Error("state.json unreadable; kept as state.json.bad and starting fresh", e);
            try { File.Move(FilePath, FilePath + ".bad", true); } catch (IOException) { }
        }
        return new AppState();
    }

    /// <summary>Writes atomically (temp file, then rename).</summary>
    public void Save()
    {
        Directory.CreateDirectory(Log.Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, FilePath, true);
    }
}
