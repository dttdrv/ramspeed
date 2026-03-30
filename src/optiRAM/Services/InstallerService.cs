using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using optiRAM.Models;

namespace optiRAM.Services;

internal static class InstallerService
{
    private const string AppName = "optiRAM";
    private const string AppVersion = "1.0";
    private const string UninstallRegPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\optiRAM";

    public static bool HasSetupUninstaller =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static bool IsInProgramFiles
    {
        get
        {
            var dir = AppContext.BaseDirectory.ToLowerInvariant();
            var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant();
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLowerInvariant();
            return dir.StartsWith(pf) || dir.StartsWith(pfx86);
        }
    }

    public static void RunUninstall()
    {
        // Prefer InnoSetup uninstaller if present
        var innoUninstaller = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (File.Exists(innoUninstaller))
        {
            if (!ConfirmUninstall()) return;
            Process.Start(new ProcessStartInfo { FileName = innoUninstaller, UseShellExecute = true });
            System.Windows.Application.Current.Shutdown();
            return;
        }

        // Self-installed to Program Files
        if (IsInProgramFiles)
        {
            if (!ConfirmUninstall()) return;
            RunSelfUninstall(AppContext.BaseDirectory);
            return;
        }

        System.Windows.MessageBox.Show(
            "optiRAM is running as a portable application.\n" +
            "To remove it, simply delete the folder containing optiRAM.exe.",
            "Uninstall optiRAM",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    public static void SelfInstall()
    {
        if (IsInProgramFiles || HasSetupUninstaller)
        {
            System.Windows.MessageBox.Show(
                "optiRAM is already installed to Program Files.",
                "Already Installed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        // Installing to Program Files and HKLM registry requires elevation
        if (!IsRunningAsAdmin())
        {
            System.Windows.MessageBox.Show(
                "Installation to Program Files requires administrator privileges.\n\n" +
                "Please restart optiRAM as administrator to install.",
                "Administrator Required",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Install optiRAM to Program Files?\n\n" +
            "  \u2022 Copies to C:\\Program Files\\optiRAM\\\n" +
            "  \u2022 Adds a Start Menu shortcut\n" +
            "  \u2022 Registers in Add or Remove Programs\n\n" +
            "You can continue using optiRAM from its current location instead.",
            "Install optiRAM",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        var targetExe = Path.Combine(targetDir, "optiRAM.exe");
        try
        {
            InstallerPayloadCopier.CopyPayload(AppContext.BaseDirectory, targetDir);

            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs", AppName);
            Directory.CreateDirectory(startMenuDir);
            CreateShortcut(targetExe, Path.Combine(startMenuDir, $"{AppName}.lnk"));

            RegisterUninstallEntry(targetExe, targetDir);
            TaskSchedulerHelper.CreateTask(targetExe, startAtLogon: Settings.Load().StartWithWindows);

            System.Windows.MessageBox.Show(
                $"optiRAM was installed to:\n{targetDir}\n\n" +
                "Relaunching from the installed location.",
                "Installation Complete",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            Process.Start(new ProcessStartInfo { FileName = targetExe, UseShellExecute = true });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Installation failed:\n{ex.Message}",
                "Install Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void RunSelfUninstall(string installDir)
    {
        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", AppName);
        var tempScript = Path.Combine(Path.GetTempPath(), $"optiRAM_Uninstall_{Guid.NewGuid():N}.cmd");

        var script =
            "@echo off\r\n" +
            "taskkill /f /im optiRAM.exe >nul 2>&1\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "schtasks /Delete /TN \"optiRAM\" /F >nul 2>&1\r\n" +
            $"reg delete \"HKLM\\{UninstallRegPath}\" /f >nul 2>&1\r\n" +
            $"rmdir /s /q \"{startMenuDir.TrimEnd('\\')}\" >nul 2>&1\r\n" +
            $"rmdir /s /q \"{installDir.TrimEnd('\\')}\" >nul 2>&1\r\n" +
            "del /f /q \"%~f0\"\r\n";

        File.WriteAllText(tempScript, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/MIN /c \"{tempScript}\"",
            UseShellExecute = true
        });
        System.Windows.Application.Current.Shutdown();
    }

    private static void CreateShortcut(string targetPath, string shortcutPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        try
        {
            dynamic wsh = Activator.CreateInstance(type)!;
            dynamic lnk = wsh.CreateShortcut(shortcutPath);
            lnk.TargetPath = targetPath;
            lnk.Description = "optiRAM \u2014 Memory Optimizer";
            lnk.Save();
        }
        catch { /* shortcut creation is optional */ }
    }

    private static void RegisterUninstallEntry(string exePath, string installDir)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(UninstallRegPath);
            key.SetValue("DisplayName",            $"{AppName} \u2014 Memory Optimizer");
            key.SetValue("DisplayVersion",         AppVersion);
            key.SetValue("Publisher",              AppName);
            key.SetValue("DisplayIcon",            $"{exePath},0");
            key.SetValue("InstallLocation",        installDir);
            key.SetValue("UninstallString",        $"\"{exePath}\" --uninstall");
            key.SetValue("QuietUninstallString",   $"\"{exePath}\" --uninstall");
            key.SetValue("NoModify",               1, RegistryValueKind.DWord);
            key.SetValue("NoRepair",               1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize",          300, RegistryValueKind.DWord);
        }
        catch { /* registry write is best-effort */ }
    }

    private static bool ConfirmUninstall() =>
        System.Windows.MessageBox.Show(
            "Are you sure you want to uninstall optiRAM?\n" +
            "This will remove all program files and shortcuts.",
            "Uninstall optiRAM",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question)
        == System.Windows.MessageBoxResult.Yes;
}
