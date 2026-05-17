namespace ProcessLog.Models;

/// <summary>
/// Represents a single process lifecycle event (start or exit).
/// </summary>
public sealed record ProcessEventRecord
{
    public required DateTime Timestamp { get; init; }
    public required ProcessEventType EventType { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required int ParentProcessId { get; init; }
    public string ParentProcessName { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public int? ExitCode { get; init; }

    /// <summary>
    /// Returns a human-readable one-line summary for console output.
    /// </summary>
    public string ToDisplayString()
    {
        var prefix = EventType == ProcessEventType.Start ? "+" : "-";
        var exitInfo = ExitCode.HasValue ? $" (exit: {ExitCode.Value})" : string.Empty;
        var parent = string.IsNullOrEmpty(ParentProcessName)
            ? $"PID:{ParentProcessId}"
            : $"{ParentProcessName}({ParentProcessId})";

        return $"{prefix} [{Timestamp:HH:mm:ss.fff}] {ProcessName}({ProcessId}) <- {parent}{exitInfo}";
    }
}

/// <summary>
/// Discriminator for process lifecycle events.
/// </summary>
public enum ProcessEventType
{
    Start,
    Exit
}
