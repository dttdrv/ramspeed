using System.Diagnostics;
using System.Runtime;
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

        // Single-instance enforcement
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
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

        // Admin check — skip dialog during dev, go straight to read-only mode
        if (!IsRunningAsAdmin() && false) // TODO: restore admin check
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            if (TaskSchedulerHelper.TaskExists() && TaskSchedulerHelper.RunTask())
            {
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
                catch { /* User cancelled UAC */ }
                Shutdown();
                return;
            }

            if (result == MessageBoxResult.Cancel)
            {
                Shutdown();
                return;
            }

            // User chose "No" — continue in read-only mode
            IsReadOnlyMode = true;

            // Re-acquire mutex for single-instance
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool ownsMutex);
            if (!ownsMutex)
            {
                Shutdown();
                return;
            }
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
        _singleInstanceMutex?.ReleaseMutex();
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
}

