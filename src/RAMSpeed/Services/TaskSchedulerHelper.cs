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
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
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
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return false;
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Create the scheduled task. Must be called from an elevated process (installer).
    /// </summary>
    public static bool CreateTask(string exePath, bool startAtLogon = false)
    {
        string? tempXml = null;
        try
        {
            var xml = BuildTaskXml(exePath, startAtLogon);
            // Use a random filename to prevent symlink/replacement attacks (TOCTOU)
            tempXml = Path.Combine(Path.GetTempPath(), $"RAMSpeed_Task_{Guid.NewGuid():N}.xml");
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
            if (p == null) return false;

            // Use longer timeout — schtasks can be slow on cold-start or domain-joined machines
            if (!p.WaitForExit(15000))
            {
                // Process didn't exit in time — task may still have been created
                try { p.Kill(); } catch { }
                return false;
            }

            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
        finally
        {
            if (tempXml != null)
                try { File.Delete(tempXml); } catch { }
        }
    }

    internal static string BuildTaskXml(string exePath, bool startAtLogon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        // Use the user's SID for portability across domain renames and profile migrations
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw new InvalidOperationException("Unable to resolve the current Windows identity SID for task registration.");

        var workingDir = Path.GetDirectoryName(exePath) ?? "";

        var triggers = startAtLogon
            ? """
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
              </Triggers>
              """
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>RAMSpeed</Author>
                <Description>RAMSpeed — Memory Optimizer (elevated launch)</Description>
              </RegistrationInfo>
              {triggers}
              <Principals>
                <Principal id="Author">
                  <UserId>{System.Security.SecurityElement.Escape(userSid)}</UserId>
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
                  <WorkingDirectory>{System.Security.SecurityElement.Escape(workingDir)}</WorkingDirectory>
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
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
