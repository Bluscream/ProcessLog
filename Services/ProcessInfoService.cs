using System.Management;

namespace ProcessLog.Services;

/// <summary>
/// WMI-based helpers for querying process metadata (command lines, names).
/// </summary>
internal static class ProcessInfoService
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 50;

    /// <summary>
    /// Attempts to retrieve the command line of a process by PID.
    /// Retries a few times since the process may still be initializing.
    /// </summary>
    public static string GetCommandLine(int processId)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={processId}");

                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        var cmdLine = obj["CommandLine"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(cmdLine))
                            return cmdLine;
                    }
                }
            }
            catch
            {
                // Process may have exited or we lack permissions — retry
            }

            if (attempt < MaxRetries - 1)
                Thread.Sleep(RetryDelayMs);
        }

        return string.Empty;
    }

    /// <summary>
    /// Resolves a process name by PID via WMI. Best-effort: returns empty if the process
    /// has already exited or is inaccessible.
    /// </summary>
    public static string GetProcessName(int processId)
    {
        if (processId <= 0)
            return string.Empty;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Process WHERE ProcessId={processId}");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (name is not null)
                        return name;
                }
            }
        }
        catch
        {
            // Process gone or access denied — not an error for our purposes
        }

        return string.Empty;
    }
}
