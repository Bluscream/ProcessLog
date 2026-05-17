using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcessLog.Services;

/// <summary>
/// Helpers for querying process metadata (command lines, names).
/// Uses direct PEB reading (fast) with WMI fallback (reliable).
/// Maintains a command line cache so exit events can reference start data.
/// </summary>
internal sealed class ProcessInfoService : IDisposable
{
    // Progressive backoff: 10ms, 25ms, 50ms, 100ms, 200ms
    private static readonly int[] RetryDelays = [10, 25, 50, 100, 200];

    /// <summary>
    /// Cache of command lines by PID, populated on start events and consumed on exit events.
    /// Uses ConcurrentDictionary for thread-safe access from WMI callback threads.
    /// </summary>
    private readonly ConcurrentDictionary<int, CachedProcessInfo> _cache = new();

    /// <summary>
    /// Retrieves the command line for a process, trying the fast PEB path first
    /// then falling back to WMI. Caches the result for later exit event lookup.
    /// </summary>
    public string GetCommandLine(int processId)
    {
        // Fast path: direct PEB reading (~1ms)
        var cmdLine = GetCommandLineFromPeb(processId);

        // Slow path: WMI with progressive backoff retries
        if (string.IsNullOrEmpty(cmdLine))
            cmdLine = GetCommandLineFromWmi(processId);

        // Last resort: try to get at least the executable path
        if (string.IsNullOrEmpty(cmdLine))
            cmdLine = GetExecutablePath(processId);

        // Cache for exit event lookup
        if (!string.IsNullOrEmpty(cmdLine))
            _cache.AddOrUpdate(processId, _ => new CachedProcessInfo(cmdLine), (_, _) => new CachedProcessInfo(cmdLine));

        return cmdLine ?? string.Empty;
    }

    /// <summary>
    /// Looks up a cached command line from a prior start event.
    /// Returns the cached value and removes it from the cache.
    /// </summary>
    public string GetCachedCommandLine(int processId)
    {
        if (_cache.TryRemove(processId, out var info))
            return info.CommandLine;

        return string.Empty;
    }

    /// <summary>
    /// Resolves a process name by PID. Tries the fast managed API first, then WMI.
    /// </summary>
    public static string GetProcessName(int processId)
    {
        if (processId <= 0)
            return string.Empty;

        // Fast path: managed Process API
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            // Process may have exited
        }

        // Slow path: WMI
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
            // Process gone or access denied
        }

        return string.Empty;
    }

    /// <summary>
    /// Periodically trims stale entries from the command line cache
    /// (processes that started but never had an exit event observed).
    /// Call occasionally to prevent unbounded growth.
    /// </summary>
    public void TrimCache(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleKeys = _cache
            .Where(kvp => kvp.Value.CachedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
            _cache.TryRemove(key, out _);
    }

    public int CacheSize => _cache.Count;

    #region PEB Command Line Reading (Fast Path)

    // Windows API constants
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const int ProcessBasicInformation = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    /// <summary>
    /// Reads the command line directly from the process's PEB (Process Environment Block).
    /// This is the same technique used by Process Explorer and is ~50x faster than WMI.
    /// </summary>
    private static string? GetCommandLineFromPeb(int processId)
    {
        var hProcess = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
            if (hProcess == IntPtr.Zero)
                return null;

            // Step 1: Get PEB address
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(hProcess, ProcessBasicInformation, ref pbi, Marshal.SizeOf(pbi), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
                return null;

            // Step 2: Read PEB to get ProcessParameters pointer
            // PEB.ProcessParameters is at offset 0x20 on x64
            var pebBuffer = new byte[IntPtr.Size];
            if (!ReadProcessMemory(hProcess, pbi.PebBaseAddress + 0x20, pebBuffer, pebBuffer.Length, out _))
                return null;

            var processParametersPtr = (IntPtr)(IntPtr.Size == 8
                ? BitConverter.ToInt64(pebBuffer, 0)
                : BitConverter.ToInt32(pebBuffer, 0));

            // Step 3: Read RTL_USER_PROCESS_PARAMETERS.CommandLine (UNICODE_STRING)
            // CommandLine is at offset 0x70 on x64 (Length: ushort, MaxLength: ushort, padding, Buffer: pointer)
            var unicodeStringBuffer = new byte[IntPtr.Size + 4]; // 2 ushorts + 1 pointer
            if (!ReadProcessMemory(hProcess, processParametersPtr + 0x70, unicodeStringBuffer, unicodeStringBuffer.Length, out _))
                return null;

            var length = BitConverter.ToUInt16(unicodeStringBuffer, 0);
            if (length == 0)
                return null;

            var bufferPtr = (IntPtr)(IntPtr.Size == 8
                ? BitConverter.ToInt64(unicodeStringBuffer, 4 + (IntPtr.Size == 8 ? 4 : 0)) // account for padding on x64
                : BitConverter.ToInt32(unicodeStringBuffer, 4));

            // Step 4: Read the actual command line string
            var cmdLineBuffer = new byte[length];
            if (!ReadProcessMemory(hProcess, bufferPtr, cmdLineBuffer, cmdLineBuffer.Length, out var bytesRead))
                return null;

            var result = Encoding.Unicode.GetString(cmdLineBuffer, 0, bytesRead);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                CloseHandle(hProcess);
        }
    }

    #endregion

    #region WMI Command Line Reading (Fallback)

    private static string? GetCommandLineFromWmi(int processId)
    {
        for (int attempt = 0; attempt < RetryDelays.Length; attempt++)
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
                // Process may have exited or we lack permissions
            }

            if (attempt < RetryDelays.Length - 1)
                Thread.Sleep(RetryDelays[attempt]);
        }

        return null;
    }

    #endregion

    #region Executable Path (Last Resort)

    /// <summary>
    /// Tries to get at least the main executable path as a fallback
    /// when command line retrieval fails entirely.
    /// </summary>
    private static string? GetExecutablePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    public void Dispose()
    {
        _cache.Clear();
    }

    private sealed record CachedProcessInfo(string CommandLine)
    {
        public DateTime CachedAt { get; } = DateTime.UtcNow;
    }
}
