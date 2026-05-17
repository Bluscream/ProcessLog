using System.Diagnostics;
using System.Security.Principal;

namespace ProcessLog.Services;

/// <summary>
/// Windows-specific elevation and privilege helpers.
/// </summary>
internal static class NativeInterop
{
    /// <summary>
    /// Returns true if the current process is running with administrator privileges.
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relaunches the current executable with a UAC elevation prompt.
    /// Exits the current process on success.
    /// </summary>
    /// <returns>True if relaunch succeeded (caller should exit), false on failure.</returns>
    public static bool TryRestartAsElevated()
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrEmpty(exePath))
            {
                ConsoleHelper.WriteError("Cannot determine executable path for elevation.");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
                FileName = exePath,
                Verb = "runas",
                Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1))
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Failed to restart with elevated privileges: {ex.Message}");
            ConsoleHelper.WriteError("Please run this application as Administrator.");
            return false;
        }
    }
}
