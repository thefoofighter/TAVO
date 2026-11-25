namespace TAVO;

internal static class Log
{
    static readonly object Gate = new();

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TAVO");
    public static string FilePath => Path.Combine(Dir, "tavo.log");

    /// <summary>Also write each line to stdout (CLI modes).</summary>
    public static bool Echo { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? e = null) =>
        Write("ERROR", e == null ? msg : $"{msg}: {e.GetType().Name}: {e.Message}");

    static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {msg}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { /* Logging must never take the app down; the line is lost. */ }
            catch (UnauthorizedAccessException) { /* Same as above. */ }
            if (Echo) Console.WriteLine(line);
        }
    }
}
