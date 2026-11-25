namespace TAVO;

internal static class Program
{
    public const string MutexName = @"Local\TAVO.SingleInstance";
    public const string ProjectUrl = "https://github.com/thefoofighter/TAVO";
    public static string Version => Application.ProductVersion;

    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Length > 0) return Cli.Run(args);

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first) return 0; // already running

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);
        Application.Run(new TrayApp());
        return 0;
    }
}

internal static class Cli
{
    const string Usage = """
        TAVO - Taskbar App Version Overlay
          (no arguments)       Run in the tray.
          --plan               Show what TAVO would do. Changes nothing.
          --apply              Apply badges once and exit.
          --restore            Restore every badged pin and exit.
          --refresh-explorer   Reset the shell icon cache and restart Windows Explorer.
          --version            Print the version.
        --apply and --restore refuse to run while the tray app is running.
        """;

    public static int Run(string[] args)
    {
        if (!Console.IsOutputRedirected) Native.AttachConsole(-1); // WinExe: borrow the parent console
        string cmd = args[0].ToLowerInvariant();
        if (cmd is "--version" or "-v") { Console.WriteLine($"TAVO {Program.Version}"); return 0; }
        if (cmd is "--help" or "-h" or "/?") { Console.WriteLine(Usage); return 0; }
        if (cmd is "--apply" or "--restore" && Mutex.TryOpenExisting(Program.MutexName, out var m))
        {
            m.Dispose();
            Console.WriteLine("TAVO is running in the tray. Exit it first.");
            return 2;
        }

        Log.Echo = cmd != "--plan";
        var state = AppState.Load();
        switch (cmd)
        {
            case "--plan":
            {
                var pins = PinScanner.Scan();
                PrintPlan(pins, Planner.Build(pins, state));
                return 0;
            }
            case "--apply":
            {
                var pins = PinScanner.Scan();
                var plan = Planner.Build(pins, state);
                PrintPlan(pins, plan);
                bool changed = Badger.Apply(pins, plan, state, enabled: true);
                Console.WriteLine(changed ? "Shortcuts changed. Restart explorer to show them." : "Nothing to change.");
                return 0;
            }
            case "--restore":
            {
                bool changed = Badger.Apply(PinScanner.Scan(), new Plan(), state, enabled: false);
                Console.WriteLine(changed ? "Originals restored. Restart explorer to show them." : "Nothing to restore.");
                return 0;
            }
            case "--refresh-explorer":
                return ExplorerRestarter.Restart() ? 0 : 1;
            default:
                Console.WriteLine(Usage);
                return 1;
        }
    }

    static void PrintPlan(List<Pin> pins, Plan plan)
    {
        Console.WriteLine($"Icon folder: {Badger.IconDir}");
        Console.WriteLine("Pins:");
        foreach (var p in pins)
        {
            string app = p.App == null ? "(no exe target)" : $"{p.App.ProductName} {string.Join(".", p.App.Version)}{(p.App.Tag.Length > 0 ? " " + p.App.Tag : "")}  key={p.App.ProductKey}";
            Console.WriteLine($"  {p.Name,-38} {app}{(p.IsPinned ? "" : "  (not pinned)")}");
        }
        Console.WriteLine("Groups with 2+ versions:");
        if (plan.Groups.Count == 0) Console.WriteLine("  (none)");
        foreach (var g in plan.Groups) Console.WriteLine($"  {g}");
        Console.WriteLine("Badges:");
        foreach (var (lnk, b) in plan.Badges)
            Console.WriteLine($"  {Path.GetFileNameWithoutExtension(lnk),-38} \"{b.Label}\"  #{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" + (b.UniqueAppId != null ? $"  new ID {b.UniqueAppId}" : ""));
        Console.WriteLine("Window rules:");
        foreach (var r in plan.WindowRules) Console.WriteLine($"  {r.ExePath} -> {r.AppId}");
    }
}
