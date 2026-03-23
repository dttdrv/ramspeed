using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace RAMSpeed.Services;

/// <summary>
/// Manages a Windows Task Scheduler task that runs RAMSpeed with highest privileges,
/// enabling silent admin elevation without a UAC prompt after installation.
/// </summary>
internal static class TaskSchedulerHelper
{
    private const string TaskName = "RAMSpeed";
    private const string TaskXmlFileName = "RAMSpeed_Task.xml";

    /// <summary>Check whether the scheduled task exists.</summary>
    public static bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /FO LIST",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Run the app via the scheduled task (silent admin elevation).</summary>
    public static bool RunTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Create the scheduled task. Must be called from an elevated process (installer).
    /// </summary>
    public static bool CreateTask(string exePath, bool startAtLogon = false)
    {
        try
        {
            var xml = BuildTaskXml(exePath, startAtLogon);
            var tempXml = Path.Combine(Path.GetTempPath(), TaskXmlFileName);
            File.WriteAllText(tempXml, xml, System.Text.Encoding.Unicode);

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            try { File.Delete(tempXml); } catch { }
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static string BuildTaskXml(string exePath, bool startAtLogon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var userId = WindowsIdentity.GetCurrent().Name;
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Unable to resolve the current Windows identity for task registration.");

        var triggers = startAtLogon
            ? """
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              """
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>RAMSpeed — Memory Optimizer (elevated launch)</Description>
              </RegistrationInfo>
              {triggers}
              <Principals>
                <Principal id="Author">
                  <UserId>{System.Security.SecurityElement.Escape(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Delete the scheduled task (for uninstall).</summary>
    public static bool DeleteTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
