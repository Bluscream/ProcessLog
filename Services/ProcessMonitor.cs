using System.Management;
using ProcessLog.Models;

namespace ProcessLog.Services;

/// <summary>
/// Core process monitoring engine. Subscribes to WMI process start and stop traces
/// and dispatches events to the CSV logger and console output.
/// </summary>
internal sealed class ProcessMonitor : IDisposable
{
    private readonly CsvLogger _logger;
    private readonly ProcessInfoService _processInfo;
    private readonly int _selfPid;
    private readonly Timer _cacheTrimTimer;
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _disposed;

    // Statistics
    private long _startCount;
    private long _exitCount;
    private long _errorCount;

    public long StartCount => Interlocked.Read(ref _startCount);
    public long ExitCount => Interlocked.Read(ref _exitCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public ProcessMonitor(CsvLogger logger, ProcessInfoService processInfo)
    {
        _logger = logger;
        _processInfo = processInfo;
        _selfPid = Environment.ProcessId;

        // Trim stale cache entries every 5 minutes (processes that started but we never saw exit)
        _cacheTrimTimer = new Timer(_ => _processInfo.TrimCache(TimeSpan.FromMinutes(10)),
            null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Begins monitoring process starts and exits. Blocks until the cancellation token fires.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        _startWatcher = CreateWatcher("SELECT * FROM Win32_ProcessStartTrace");
        _startWatcher.EventArrived += OnProcessStart;
        _startWatcher.Start();

        _stopWatcher = CreateWatcher("SELECT * FROM Win32_ProcessStopTrace");
        _stopWatcher.EventArrived += OnProcessStop;
        _stopWatcher.Start();

        _logger.WriteMarker($"Monitor started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (PID: {_selfPid})");
        ConsoleHelper.WriteInfo($"Monitoring process starts and exits. Logging to: {_logger.FilePath}");
        ConsoleHelper.WriteInfo("Press Ctrl+C to stop...\n");

        // Block until cancellation is requested
        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    private void OnProcessStart(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var processId = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
            var parentId = Convert.ToInt32(e.NewEvent.Properties["ParentProcessID"].Value);
            var processName = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? string.Empty;
            var timeCreated = Convert.ToUInt64(e.NewEvent.Properties["TIME_CREATED"].Value);
            var timestamp = DateTime.FromFileTime((long)timeCreated);

            // Skip our own process to avoid noise
            if (processId == _selfPid)
                return;

            // Resolve additional info on the thread pool to avoid blocking the WMI callback
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var commandLine = _processInfo.GetCommandLine(processId);
                    var parentName = ProcessInfoService.GetProcessName(parentId);

                    var record = new ProcessEventRecord
                    {
                        Timestamp = timestamp,
                        EventType = ProcessEventType.Start,
                        ProcessId = processId,
                        ProcessName = processName,
                        ParentProcessId = parentId,
                        ParentProcessName = parentName,
                        CommandLine = commandLine
                    };

                    _logger.LogEvent(record);
                    ConsoleHelper.WriteEvent(record);
                    Interlocked.Increment(ref _startCount);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errorCount);
                    ConsoleHelper.WriteWarning($"Error resolving start details for PID {processId}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            ConsoleHelper.WriteWarning($"Error processing start event: {ex.Message}");
        }
    }

    private void OnProcessStop(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var processId = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
            var parentId = Convert.ToInt32(e.NewEvent.Properties["ParentProcessID"].Value);
            var processName = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? string.Empty;
            var exitCode = Convert.ToInt32(e.NewEvent.Properties["ExitStatus"].Value);
            var timeCreated = Convert.ToUInt64(e.NewEvent.Properties["TIME_CREATED"].Value);
            var timestamp = DateTime.FromFileTime((long)timeCreated);

            // Skip our own process
            if (processId == _selfPid)
                return;

            // Parent name resolution is best-effort — parent may have also exited
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var parentName = ProcessInfoService.GetProcessName(parentId);

                    // Retrieve cached command line from the start event
                    var commandLine = _processInfo.GetCachedCommandLine(processId);

                    var record = new ProcessEventRecord
                    {
                        Timestamp = timestamp,
                        EventType = ProcessEventType.Exit,
                        ProcessId = processId,
                        ProcessName = processName,
                        ParentProcessId = parentId,
                        ParentProcessName = parentName,
                        CommandLine = commandLine,
                        ExitCode = exitCode
                    };

                    _logger.LogEvent(record);
                    ConsoleHelper.WriteEvent(record);
                    Interlocked.Increment(ref _exitCount);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errorCount);
                    ConsoleHelper.WriteWarning($"Error resolving exit details for PID {processId}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            ConsoleHelper.WriteWarning($"Error processing stop event: {ex.Message}");
        }
    }

    private static ManagementEventWatcher CreateWatcher(string wql)
    {
        return new ManagementEventWatcher(new WqlEventQuery(wql));
    }

    /// <summary>
    /// Prints a summary of events tracked during this session.
    /// </summary>
    public void PrintStats()
    {
        ConsoleHelper.WriteInfo(
            $"\nSession summary: {StartCount} starts, {ExitCount} exits, {ErrorCount} errors" +
            $" (cache: {_processInfo.CacheSize} entries)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cacheTrimTimer.Dispose();

        try { _startWatcher?.Stop(); } catch { }
        try { _stopWatcher?.Stop(); } catch { }

        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
    }
}
