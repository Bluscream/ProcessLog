using System.Text;
using ProcessLog.Models;

namespace ProcessLog.Services;

/// <summary>
/// Thread-safe RFC 4180-compliant CSV logger for process events.
/// </summary>
internal sealed class CsvLogger : IDisposable
{
    private static readonly string[] HeaderColumns =
    [
        "Timestamp",
        "EventType",
        "PID",
        "ProcessName",
        "ParentPID",
        "ParentProcessName",
        "CommandLine",
        "ExitCode"
    ];

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

        var fileExists = File.Exists(filePath) && new FileInfo(filePath).Length > 0;

        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8)
        {
            AutoFlush = true
        };

        if (!fileExists)
            WriteHeaderRow();
    }

    /// <summary>
    /// Appends a process event record as a CSV row.
    /// </summary>
    public void LogEvent(ProcessEventRecord record)
    {
        var fields = new[]
        {
            record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            record.EventType.ToString(),
            record.ProcessId.ToString(),
            record.ProcessName,
            record.ParentProcessId.ToString(),
            record.ParentProcessName,
            record.CommandLine,
            record.ExitCode?.ToString() ?? string.Empty
        };

        var line = string.Join(",", fields.Select(EscapeCsvField));

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

    private void WriteHeaderRow()
    {
        var line = string.Join(",", HeaderColumns);
        _writer.WriteLine(line);
    }

    /// <summary>
    /// RFC 4180 CSV escaping: wraps fields in quotes if they contain
    /// commas, double-quotes, or newlines. Doubles any internal quotes.
    /// </summary>
    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return string.Empty;

        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
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
