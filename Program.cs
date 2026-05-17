using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

class ProcessMonitor
{
    private readonly string _logFilePath = Path.Combine(Path.GetTempPath(), "processes.csv");
    private readonly object _logLock = new object();
    private bool _running = true;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    protected static extern bool GetTokenInformation(IntPtr TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, ref int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    protected enum TOKEN_INFORMATION_CLASS {
        TokenUser = 1,
        TokenGroups,
        TokenPrivileges,
        TokenOwner,
        TokenPrimaryGroup,
        TokenDefaultDacl,
        TokenSource,
        TokenType,
        TokenImpersonationLevel,
        TokenStatistics,
        TokenRestrictedSids,
        TokenSessionId,
        TokenGroupsAndPrivileges,
        TokenSessionReference,
        TokenSandBoxInert,
        TokenAuditPolicy,
        TokenOrigin,
        TokenElevationType,
        TokenLinkedToken,
        TokenElevation,
        TokenHasRestrictions,
        TokenAccessInformation,
        TokenVirtualizationAllowed,
        TokenVirtualizationEnabled,
        TokenIntegrityLevel,
        TokenUIAccess,
        TokenMandatoryPolicy,
        TokenLogonSid,
        TokenIsAppContainer,
        TokenCapabilities,
        TokenAppContainerSid,
        TokenAppContainerNumber,
        TokenUserClaimAttributes,
        TokenDeviceClaimAttributes,
        TokenRestrictedUserClaimAttributes,
        TokenRestrictedDeviceClaimAttributes,
        TokenDeviceGroups,
        TokenRestrictedDeviceGroups,
        TokenSecurityAttributes,
        TokenIsRestricted,
        TokenProcessTrustLevel,
        TokenPrivateNameSpace,
        TokenSingletonAttributes,
        TokenBnoIsolation,
        TokenChildProcessFlags,
        TokenIsLessPrivilegedAppContainer,
        TokenIsSandboxed,
        TokenIsAppSilo,
        TokenLoggingInformation,
        TokenLearningMode,
        MaxTokenInfoClass
    }
    protected enum TOKEN_ELEVATION_TYPE {
        TokenElevationTypeDefault = 1,
        TokenElevationTypeFull,
        TokenElevationTypeLimited
    }
    const uint TOKEN_QUERY = 0x0008;

    private bool IsProcessElevated(Process process)
    {
        IntPtr tokenHandle;
        if (!OpenProcessToken(process.Handle, TOKEN_QUERY, out tokenHandle))
            return false;

        try
        {
            int elevation;
            int size = Marshal.SizeOf(typeof(int));
            int returnLength = 0;
            IntPtr elevationPtr = Marshal.AllocHGlobal(size);
            
            try
            {
                if (!GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevationType, elevationPtr, size, ref returnLength))
                {
                    return false;
                }
                
                elevation = Marshal.ReadInt32(elevationPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(elevationPtr);
            }
            return elevation == 1;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
    private void RestartAsElevated()
    {
        try
        {
            var startInfo = new ProcessStartInfo();
            startInfo.UseShellExecute = true;
            startInfo.WorkingDirectory = Environment.CurrentDirectory;
            startInfo.FileName = Process.GetCurrentProcess().MainModule.FileName;
            startInfo.Verb = "runas";

            Process.Start(startInfo);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to restart with elevated privileges: {ex.Message}");
            Console.WriteLine("Please run this application as Administrator.");
            Environment.Exit(1);
        }
    }

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath));

        lock (_logLock)
        {
            Console.WriteLine("Process Monitor Started");
            File.AppendAllText(_logFilePath, $"Process Monitor Started at {DateTime.Now}\n");
        }

        if (!IsCurrentProcessElevated())
        {
            Console.WriteLine("Process Monitor requires elevated privileges. Attempting to restart with elevated permissions...");
            RestartAsElevated();
            return;
        }

        var startWatch = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        startWatch.EventArrived += new EventArrivedEventHandler(startWatch_EventArrived);
        startWatch.Start();

        Console.ReadLine(); // Keep the application running to monitor processes

        //while (_running)
        //{
        //    Process[] processes = Process.GetProcesses();

        //    foreach (var process in processes)
        //    {
        //        try
        //        {
        //            var startTime = process.StartTime;
        //            var commandLine = GetCommandLine(process);
        //            // var isElevated = IsProcessElevated(process);

        //            LogProcess(process.ProcessName, process.Id, startTime, commandLine);
        //        }
        //        catch (Exception ex)
        //        {
        //            Console.Error.WriteLine($"Error monitoring process {process.ProcessName}: {ex.Message}");
        //            File.AppendAllText(_logFilePath, $"Error monitoring process {process.ProcessName}: {ex.Message}\n");
        //        }
        //    }

        //    Thread.Sleep(500); // Reduce CPU usage
        //}
    }

    private void startWatch_EventArrived(object sender, EventArrivedEventArgs e) {
        try {
            var processId = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
            var parentId = Convert.ToInt32(e.NewEvent.Properties["ParentProcessID"].Value);
            var processName = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? "";
            var timeCreated = Convert.ToUInt64(e.NewEvent.Properties["TIME_CREATED"].Value);
        
            var startTime = DateTime.FromFileTime((long)timeCreated);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var commandLine = GetCommandLine(processId);
                    var parentName = GetProcessName(parentId);
                
                    LogProcess(processName, processId, parentId, parentName, startTime, commandLine);
                }
                catch (Exception ex)
                {
                    lock (_logLock)
                    {
                        Console.Error.WriteLine($"Error handling process start event details: {ex.Message}");
                        File.AppendAllText(_logFilePath, $"Error handling process start event details: {ex.Message}\n");
                    }
                }
            });
        } catch (Exception ex) {
            lock (_logLock)
            {
                Console.Error.WriteLine($"Error queuing process start event: {ex.Message}");
                File.AppendAllText(_logFilePath, $"Error queuing process start event: {ex.Message}\n");
            }
        }
    }

    private string GetCommandLine(int processId)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={processId}"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        var commandLine = item["CommandLine"];
                        if (commandLine != null && !string.IsNullOrWhiteSpace(commandLine.ToString()))
                            return commandLine.ToString();
                    }
                }
            }
            catch { }
            Thread.Sleep(50);
        }

        return "";
    }

    private string GetProcessName(int processId)
    {
        if (processId <= 0) return "";
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT Name FROM Win32_Process WHERE ProcessId={processId}"))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    var name = item["Name"];
                    if (name != null) return name.ToString();
                }
            }
        }
        catch { }
        return "";
    }

    private void LogProcess(string processName, int processId, int parentId, string parentName, DateTime startTime, string commandLine)
    {
        var logMessage = $"{startTime:yyyy-MM-dd HH:mm:ss},{processId},{processName},{parentId},{parentName},{commandLine}\n";

        lock (_logLock)
        {
            Console.Write(logMessage);
            File.AppendAllText(_logFilePath, logMessage);
        }
    }

    public void Stop()
    {
        _running = false;
    }
}

class Program
{
    static void Main(string[] args)
    {
        var monitor = new ProcessMonitor();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Prevent the process from terminating
            monitor.Stop();
        };

        monitor.Start();

        Console.WriteLine("Press Ctrl+C to exit...");
    }
}