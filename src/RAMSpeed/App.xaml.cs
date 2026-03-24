using System.Diagnostics;
using System.Runtime;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using RAMSpeed.Native;
using RAMSpeed.Services;
using Wpf.Ui.Appearance;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace RAMSpeed;

public partial class App : Application
{
    internal const string SingleInstanceMutexName = "RAMSpeed_SingleInstance_B7F3A2";
    internal const string ActivationSignalName = "RAMSpeed_Activate_B7F3A2";

    private Mutex? _singleInstanceMutex;
    private SingleInstanceActivationService? _activationService;
    private bool _pendingActivationRestore;

    /// <summary>True when running without admin — optimization disabled, monitoring only.</summary>
    internal bool IsReadOnlyMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Show any unhandled exceptions instead of silently dying
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"Unhandled error:\n\n{args.Exception}",
                "RAMSpeed Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"Fatal error:\n\n{args.ExceptionObject}",
                "RAMSpeed Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Handle uninstall flag (registered as UninstallString in registry)
        if (e.Args.Contains("--uninstall"))
        {
            InstallerService.RunUninstall();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--register-task"))
        {
            var startAtLogon = e.Args.Contains("--start-at-logon");
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                TaskSchedulerHelper.CreateTask(Environment.ProcessPath!, startAtLogon);
            Shutdown();
            return;
        }

        // Single-instance enforcement — use a security descriptor that allows
        // both elevated and non-elevated processes to see the same mutex/event,
        // preventing duplicate instances across integrity levels.
        _singleInstanceMutex = CreateCrossIntegrityMutex(SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            var activated = SingleInstanceActivationService.SignalExistingInstance(ActivationSignalName)
                || ActivateExistingInstance();
            if (!activated)
            {
                MessageBox.Show(
                    "RAMSpeed is already running, but the existing instance could not be activated.\n\nRestore it from the system tray.",
                    "RAMSpeed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _activationService = new SingleInstanceActivationService(ActivationSignalName, OnActivationSignal);

        // Admin check — try silent elevation via scheduled task, then UAC, then read-only
        if (!IsRunningAsAdmin())
        {
            // Try the scheduled task first (silent elevation, no UAC prompt)
            if (TaskSchedulerHelper.TaskExists() && TaskSchedulerHelper.RunTask())
            {
                // Give the elevated instance a moment to start before we release the mutex
                Thread.Sleep(500);
                Shutdown();
                return;
            }

            var result = MessageBox.Show(
                "RAMSpeed requires administrator privileges to manage system memory.\n\n" +
                "Yes = Restart as administrator\n" +
                "No = Continue in read-only mode (monitoring only)",
                "RAMSpeed", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Verb = "runas",
                        UseShellExecute = true
                    });
                }
                catch { /* User cancelled UAC or process start failed */ }
                Shutdown();
                return;
            }

            if (result == MessageBoxResult.Cancel)
            {
                Shutdown();
                return;
            }

            // User chose "No" — continue in read-only mode (keep existing mutex)
            IsReadOnlyMode = true;
        }

        // Apply Windows 11 theme and accent color
        ThemeService.Instance.ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Battery efficiency: lower animation frame rate from 60 to 30 FPS
        try
        {
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 30 });
        }
        catch { }

        // Self-optimization: low memory priority + EcoQoS efficiency mode + timer resolution + GC latency
        try { NativeMethods.SetSelfMemoryPriority(NativeMethods.MEMORY_PRIORITY_LOW); } catch { }
        try { NativeMethods.SetSelfEcoQoS(); } catch { }
        try { NativeMethods.SetSelfIgnoreTimerResolution(); } catch { }
        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ThemeService.Instance.Dispose();
        _activationService?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() => ThemeService.Instance.OnSystemThemeChanged());
    }

    private void OnActivationSignal()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (Current.MainWindow is MainWindow mainWindow)
            {
                _pendingActivationRestore = false;
                mainWindow.RestoreFromExternalActivation();
                return;
            }

            _pendingActivationRestore = true;
        });
    }

    internal void RestorePendingActivation()
    {
        if (!_pendingActivationRestore || Current.MainWindow is not MainWindow mainWindow)
            return;

        _pendingActivationRestore = false;
        mainWindow.RestoreFromExternalActivation();
    }

    private static bool ActivateExistingInstance()
    {
        var hwnd = NativeMethods.FindWindow(null, "RAMSpeed");
        if (hwnd == IntPtr.Zero)
            return false;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        return NativeMethods.SetForegroundWindow(hwnd);
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Create a named mutex accessible by both elevated and non-elevated processes.
    /// Without explicit security, an elevated mutex is invisible to non-elevated processes,
    /// causing duplicate instances when the app is launched via Task Scheduler (elevated)
    /// and the user double-clicks the EXE (non-elevated).
    /// </summary>
    private static Mutex CreateCrossIntegrityMutex(string name, out bool createdNew)
    {
        var security = new MutexSecurity();
        // Allow Everyone to synchronize (detect the mutex) and modify (release it)
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.Synchronize | MutexRights.Modify,
            AccessControlType.Allow));
        // Grant full control to the current user
        security.AddAccessRule(new MutexAccessRule(
            WindowsIdentity.GetCurrent().User!,
            MutexRights.FullControl,
            AccessControlType.Allow));
        return MutexAcl.Create(true, name, out createdNew, security);
    }
}

