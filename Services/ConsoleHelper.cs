using ProcessLog.Models;

namespace ProcessLog.Services;

/// <summary>
/// Colored console output helpers for process event display.
/// </summary>
internal static class ConsoleHelper
{
    private static readonly object ConsoleLock = new();

    /// <summary>
    /// Writes a process event to the console with color coding.
    /// Shows the full CSV-formatted line. Green for starts, red for exits.
    /// </summary>
    public static void WriteEvent(ProcessEventRecord record)
    {
        var color = record.EventType switch
        {
            ProcessEventType.Start => ConsoleColor.Green,
            ProcessEventType.Exit => ConsoleColor.Red,
            _ => ConsoleColor.Gray
        };

        var line = CsvLogger.FormatEvent(record);

        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }
    }

    /// <summary>
    /// Writes an informational message in cyan.
    /// </summary>
    public static void WriteInfo(string message)
    {
        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(message);
            Console.ForegroundColor = prev;
        }
    }

    /// <summary>
    /// Writes a warning message in yellow.
    /// </summary>
    public static void WriteWarning(string message)
    {
        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine($"[WARN] {message}");
            Console.ForegroundColor = prev;
        }
    }

    /// <summary>
    /// Writes an error message in red to stderr.
    /// </summary>
    public static void WriteError(string message)
    {
        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[ERROR] {message}");
            Console.ForegroundColor = prev;
        }
    }
}
