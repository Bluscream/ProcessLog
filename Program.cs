using ProcessLog.Services;

// ── Parse CLI arguments ──────────────────────────────────────────────────────
var logPath = Path.Combine(Path.GetTempPath(), "processes.csv");

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--log-path" or "-l" && i + 1 < args.Length)
    {
        logPath = args[++i];
    }
    else if (args[i] is "--help" or "-h")
    {
        Console.WriteLine("ProcessLog v2.0 — Windows process start/exit monitor");
        Console.WriteLine();
        Console.WriteLine("Usage: ProcessLog [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -l, --log-path <path>  CSV output path (default: %TEMP%\\processes.csv)");
        Console.WriteLine("  -h, --help             Show this help message");
        return;
    }
}

// ── Elevation check ──────────────────────────────────────────────────────────
if (!NativeInterop.IsCurrentProcessElevated())
{
    ConsoleHelper.WriteWarning(
        "ProcessLog requires administrator privileges. Attempting UAC elevation...");

    if (NativeInterop.TryRestartAsElevated())
        return; // Successfully relaunched as admin

    Environment.Exit(1);
}

// ── Setup & run ──────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent immediate termination
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (!cts.IsCancellationRequested)
        cts.Cancel();
};

ConsoleHelper.WriteInfo(@"
  ╔═══════════════════════════════╗
  ║   ProcessLog v2.0             ║
  ║   Process Start/Exit Monitor  ║
  ╚═══════════════════════════════╝
");

using var logger = new CsvLogger(logPath);
using var processInfo = new ProcessInfoService();
using var monitor = new ProcessMonitor(logger, processInfo);

await monitor.RunAsync(cts.Token);

// ── Shutdown ─────────────────────────────────────────────────────────────────
logger.WriteMarker($"Monitor stopped at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
monitor.PrintStats();
ConsoleHelper.WriteInfo("ProcessLog shut down gracefully.");