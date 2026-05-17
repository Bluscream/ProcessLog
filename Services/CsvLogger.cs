using System.Text;
using ProcessLog.Models;

namespace ProcessLog.Services;

/// <summary>
/// Thread-safe semicolon-delimited logger for process events.
/// Skips empty fields to avoid consecutive separators.
/// </summary>
internal sealed class CsvLogger : IDisposable
{
    private const char Sep = ';';

    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private bool _disposed;

    public string FilePath { get; }

    public CsvLogger(string filePath)
    {
        FilePath = filePath;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    /// <summary>
    /// Formats a process event record as a semicolon-delimited line,
    /// skipping empty fields to avoid consecutive separators.
    /// Format: datetime;+/-;process_name;pid;parent_pid;parent_name;command_line[;exit_code]
    /// </summary>
    public static string FormatEvent(ProcessEventRecord record)
    {
        var parts = new List<string>(8)
        {
            record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            record.EventType == ProcessEventType.Start ? "+" : "-",
            record.ProcessName,
            record.ProcessId.ToString()
        };

        // Parent info — only add non-empty fields
        if (record.ParentProcessId > 0)
            parts.Add(record.ParentProcessId.ToString());

        if (!string.IsNullOrEmpty(record.ParentProcessName))
            parts.Add(record.ParentProcessName);

        if (!string.IsNullOrEmpty(record.CommandLine))
            parts.Add(EscapeField(record.CommandLine));

        if (record.ExitCode.HasValue)
            parts.Add(record.ExitCode.Value.ToString());

        return string.Join(Sep, parts);
    }

    /// <summary>
    /// Appends a process event record as a formatted line.
    /// </summary>
    public void LogEvent(ProcessEventRecord record)
    {
        var line = FormatEvent(record);

        lock (_writeLock)
        {
            _writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Writes a freeform marker line (e.g., "# Monitor started at ...").
    /// </summary>
    public void WriteMarker(string message)
    {
        lock (_writeLock)
        {
            _writer.WriteLine($"# {message}");
        }
    }

    /// <summary>
    /// Escapes a field value: wraps in quotes if it contains the separator,
    /// double-quotes, or newlines. Doubles any internal quotes.
    /// </summary>
    private static string EscapeField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return string.Empty;

        if (field.Contains(Sep) || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return $"\"{field.Replace("\"", "\"\"")}\"";

        return field;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_writeLock)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
