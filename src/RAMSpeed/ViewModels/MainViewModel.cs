using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using RAMSpeed.Models;
using RAMSpeed.Services;

using Application = System.Windows.Application;

namespace RAMSpeed.ViewModels;

public class MainViewModel : ViewModelBase
{
    private MemoryMonitor? _monitor;
    private readonly Settings _settings;
    private bool _initialized;
    private MemoryInfo? _lastMemoryInfo;

    private MemoryMonitor Monitor => _monitor ??= new MemoryMonitor();

    // ── Memory stats ──
    private double _totalGB;
    private double _usedGB;
    private double _availableGB;
    private double _cachedGB;
    private double _standbyGB;
    private double _freeGB;
    private double _modifiedGB;
    private double _usagePercent;
    private uint _processCount;
    private uint _threadCount;
    private uint _handleCount;
    private double _kernelPagedMB;
    private double _kernelNonpagedMB;
    private double _commitPercent;
    private double _compressedMB;

    // ── Cumulative stats ──
    private double _totalFreedMB;
    private int _optimizationCount;

    // ── State ──
    private bool _isLoading = true;
    private bool _isOptimizing;
    private string _statusText = "Ready";
    private bool _autoOptimizeEnabled;
    private int _thresholdPercent = 80;
    private int _checkIntervalSeconds = 5;
    private int _cooldownSeconds = 30;
    private string _selectedLevel = "Balanced";
    private bool _startWithWindows;
    private bool _minimizeToTray = true;
    private int _cacheMaxPercent;
    private int _selfWorkingSetCapMB = 25;
    private bool _scheduledOptimizeEnabled;
    private int _scheduledOptimizeIntervalMinutes = 30;

    // ── Process list ──
    private bool _refreshingProcesses;

    // ── Process filter ──
    private string _processFilterText = "";
    private ICollectionView? _filteredProcesses;

    // ── Optimization feedback ──
    private OptimizationResult? _lastOptimizationResult;
    private bool _showOptimizationResult;
    private System.Timers.Timer? _resultDismissTimer;

    // ── Read-only mode ──
    private bool _isReadOnlyMode;

    // ── Graph ──
    private readonly List<double> _memoryHistory = new();
    private string _graphPoints = "";

    public MainViewModel()
    {
        _settings = Settings.Load();
        ApplySettings();

        OptimizeNowCommand = new RelayCommand(OptimizeNow, () => !IsOptimizing && !IsReadOnlyMode);
        RefreshProcessListCommand = new RelayCommand(RefreshProcessList);
        ToggleAutoOptimizeCommand = new RelayCommand(ToggleAutoOptimize);
        ToggleExclusionCommand = new RelayCommand<ProcessMemoryInfo>(ToggleProcessExclusion);
        SetWorkingSetCapCommand = new RelayCommand<ProcessMemoryInfo>(SetProcessWorkingSetCap);
        SetMemoryPriorityCommand = new RelayCommand<ProcessMemoryInfo>(SetProcessMemoryPriorityAction);
        DismissResultCommand = new RelayCommand(() => ShowOptimizationResult = false);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
    }

    public event Action<MemoryInfo>? MemoryInfoUpdated;

    public async void Initialize()
    {
        if (_initialized) return;

        // Construct monitor on UI thread (DispatcherTimer requires it)
        _ = Monitor;
        ApplyMonitorSettings();
        Monitor.MemoryUpdated += OnMemoryUpdated;
        Monitor.OptimizationCompleted += OnOptimizationCompleted;

        // Move only the slow privilege calls off UI thread
        await Task.Run(() =>
        {
            if (!((App)Application.Current).IsReadOnlyMode)
                PrivilegeManager.EnableAllRequired();
        });

        // Back on UI thread — start the timer
        Monitor.Start(_settings.CheckIntervalSeconds);
        _initialized = true;
        IsLoading = false;
    }

    // ── Bound Properties ──

    public double TotalGB { get => _totalGB; set => SetProperty(ref _totalGB, value); }
    public double UsedGB { get => _usedGB; set => SetProperty(ref _usedGB, value); }
    public double AvailableGB { get => _availableGB; set => SetProperty(ref _availableGB, value); }
    public double CachedGB { get => _cachedGB; set => SetProperty(ref _cachedGB, value); }
    public double StandbyGB { get => _standbyGB; set => SetProperty(ref _standbyGB, value); }
    public double FreeGB { get => _freeGB; set => SetProperty(ref _freeGB, value); }
    public double ModifiedGB { get => _modifiedGB; set => SetProperty(ref _modifiedGB, value); }
    public double UsagePercent { get => _usagePercent; set => SetProperty(ref _usagePercent, value); }
    public uint ProcessCount { get => _processCount; set => SetProperty(ref _processCount, value); }
    public uint ThreadCount { get => _threadCount; set => SetProperty(ref _threadCount, value); }
    public uint HandleCount { get => _handleCount; set => SetProperty(ref _handleCount, value); }
    public double KernelPagedMB { get => _kernelPagedMB; set => SetProperty(ref _kernelPagedMB, value); }
    public double KernelNonpagedMB { get => _kernelNonpagedMB; set => SetProperty(ref _kernelNonpagedMB, value); }
    public double CommitPercent { get => _commitPercent; set => SetProperty(ref _commitPercent, value); }
    public double CompressedMB { get => _compressedMB; set => SetProperty(ref _compressedMB, value); }
    public double TotalFreedMB { get => _totalFreedMB; set => SetProperty(ref _totalFreedMB, value); }
    public int OptimizationCount { get => _optimizationCount; set => SetProperty(ref _optimizationCount, value); }

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    public bool IsOptimizing
    {
        get => _isOptimizing;
        set { SetProperty(ref _isOptimizing, value); CommandManager.InvalidateRequerySuggested(); }
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public bool AutoOptimizeEnabled
    {
        get => _autoOptimizeEnabled;
        set
        {
            if (SetProperty(ref _autoOptimizeEnabled, value))
            {
                if (_initialized) Monitor.AutoOptimizeEnabled = value;
                _settings.AutoOptimizeEnabled = value;
                _settings.SaveDebounced();
                StatusText = value ? $"Auto-optimizing (threshold: {ThresholdPercent}%)" : "Auto-optimize off";
            }
        }
    }

    public int ThresholdPercent
    {
        get => _thresholdPercent;
        set
        {
            if (SetProperty(ref _thresholdPercent, Math.Clamp(value, 10, 95)))
            {
                if (_initialized) Monitor.ThresholdPercent = _thresholdPercent;
                _settings.ThresholdPercent = _thresholdPercent;
                _settings.SaveDebounced();
            }
        }
    }

    public int CheckIntervalSeconds
    {
        get => _checkIntervalSeconds;
        set
        {
            if (SetProperty(ref _checkIntervalSeconds, Math.Clamp(value, 1, 60)))
            {
                if (_initialized) Monitor.SetInterval(_checkIntervalSeconds);
                _settings.CheckIntervalSeconds = _checkIntervalSeconds;
                _settings.SaveDebounced();
            }
        }
    }

    public int CooldownSeconds
    {
        get => _cooldownSeconds;
        set
        {
            if (SetProperty(ref _cooldownSeconds, Math.Clamp(value, 5, 300)))
            {
                if (_initialized) Monitor.CooldownSeconds = _cooldownSeconds;
                _settings.CooldownSeconds = _cooldownSeconds;
                _settings.SaveDebounced();
            }
        }
    }

    public string SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (SetProperty(ref _selectedLevel, value))
            {
                var level = value switch
                {
                    "Conservative" => OptimizationLevel.Conservative,
                    "Aggressive" => OptimizationLevel.Aggressive,
                    _ => OptimizationLevel.Balanced
                };
                if (_initialized) Monitor.Level = level;
                _settings.Level = level;
                _settings.SaveDebounced();
            }
        }
    }

    public string GraphPoints { get => _graphPoints; set => SetProperty(ref _graphPoints, value); }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                if (!SetStartWithWindows(value))
                {
                    _startWithWindows = !value;
                    OnPropertyChanged(nameof(StartWithWindows));
                    StatusText = value
                        ? "Failed to enable start with Windows"
                        : "Failed to disable start with Windows";
                    return;
                }

                _settings.StartWithWindows = value;
                _settings.SaveDebounced();
                StatusText = value
                    ? "Start with Windows enabled"
                    : "Start with Windows disabled";
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (SetProperty(ref _minimizeToTray, value))
            {
                _settings.MinimizeToTray = value;
                _settings.SaveDebounced();
            }
        }
    }

    public int CacheMaxPercent
    {
        get => _cacheMaxPercent;
        set
        {
            if (SetProperty(ref _cacheMaxPercent, Math.Clamp(value, 0, 75)))
            {
                if (_initialized) Monitor.CacheMaxPercent = _cacheMaxPercent;
                _settings.CacheMaxPercent = _cacheMaxPercent;
                _settings.SaveDebounced();
            }
        }
    }

    public int SelfWorkingSetCapMB
    {
        get => _selfWorkingSetCapMB;
        set
        {
            if (SetProperty(ref _selfWorkingSetCapMB, Math.Clamp(value, 0, 100)))
            {
                if (_initialized) Monitor.SelfWorkingSetCapMB = _selfWorkingSetCapMB;
                _settings.SelfWorkingSetCapMB = _selfWorkingSetCapMB;
                _settings.SaveDebounced();
            }
        }
    }

    public bool ScheduledOptimizeEnabled
    {
        get => _scheduledOptimizeEnabled;
        set
        {
            if (SetProperty(ref _scheduledOptimizeEnabled, value))
            {
                if (_initialized) Monitor.ScheduledOptimizeEnabled = value;
                _settings.ScheduledOptimizeEnabled = value;
                _settings.SaveDebounced();
            }
        }
    }

    public int ScheduledOptimizeIntervalMinutes
    {
        get => _scheduledOptimizeIntervalMinutes;
        set
        {
            if (SetProperty(ref _scheduledOptimizeIntervalMinutes, Math.Clamp(value, 1, 240)))
            {
                if (_initialized) Monitor.ScheduledOptimizeIntervalMinutes = _scheduledOptimizeIntervalMinutes;
                _settings.ScheduledOptimizeIntervalMinutes = _scheduledOptimizeIntervalMinutes;
                _settings.SaveDebounced();
            }
        }
    }

    public ObservableCollection<ProcessMemoryInfo> Processes { get; } = new();
    public ObservableCollection<OptimizationResult> OptimizationHistory { get; } = new();
    public MemoryInfo? LastMemoryInfo => _lastMemoryInfo;

    public ICollectionView FilteredProcesses
    {
        get
        {
            if (_filteredProcesses == null)
            {
                _filteredProcesses = CollectionViewSource.GetDefaultView(Processes);
                _filteredProcesses.Filter = FilterProcess;
            }
            return _filteredProcesses;
        }
    }

    public string ProcessFilterText
    {
        get => _processFilterText;
        set
        {
            if (SetProperty(ref _processFilterText, value))
                _filteredProcesses?.Refresh();
        }
    }

    public OptimizationResult? LastOptimizationResult
    {
        get => _lastOptimizationResult;
        set => SetProperty(ref _lastOptimizationResult, value);
    }

    public bool ShowOptimizationResult
    {
        get => _showOptimizationResult;
        set => SetProperty(ref _showOptimizationResult, value);
    }

    private bool FilterProcess(object obj)
    {
        if (string.IsNullOrWhiteSpace(_processFilterText))
            return true;
        return obj is ProcessMemoryInfo p &&
               p.Name.Contains(_processFilterText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Commands ──
    public ICommand OptimizeNowCommand { get; }
    public ICommand RefreshProcessListCommand { get; }
    public ICommand ToggleAutoOptimizeCommand { get; }
    public ICommand ToggleExclusionCommand { get; }
    public ICommand SetWorkingSetCapCommand { get; }
    public ICommand SetMemoryPriorityCommand { get; }
    public ICommand DismissResultCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    public bool IsReadOnlyMode
    {
        get => _isReadOnlyMode;
        set
        {
            if (SetProperty(ref _isReadOnlyMode, value))
            {
                CommandManager.InvalidateRequerySuggested();
                if (value)
                    StatusText = "Read-only mode — optimization features disabled";
            }
        }
    }

    // ── Actions ──

    private void OptimizeNow()
    {
        if (IsOptimizing) return;
        IsOptimizing = true;
        StatusText = "Optimizing...";

        // Run on background thread to keep UI responsive.
        // Use ContinueWith to ensure IsOptimizing is always reset, even on failure.
        Task.Run(() => Monitor.RunOptimization()).ContinueWith(task =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsOptimizing = false;

                if (task.IsFaulted)
                {
                    StatusText = $"Optimization failed: {task.Exception?.InnerException?.Message ?? "unknown error"}";
                    return;
                }

                var result = task.Result;
                StatusText = result.Success
                    ? $"Freed {result.FreedMB:F1} MB in {result.Duration.TotalMilliseconds:F0}ms"
                    : $"Optimization failed: {result.ErrorMessage}";

                // Show result notification
                LastOptimizationResult = result;
                ShowOptimizationResult = true;
                _resultDismissTimer?.Stop();
                _resultDismissTimer?.Dispose();
                _resultDismissTimer = new System.Timers.Timer(5000) { AutoReset = false };
                _resultDismissTimer.Elapsed += (_, _) =>
                    Application.Current?.Dispatcher.Invoke(() => ShowOptimizationResult = false);
                _resultDismissTimer.Start();
            });
        }, TaskScheduler.Default);
    }

    private void ToggleAutoOptimize()
    {
        AutoOptimizeEnabled = !AutoOptimizeEnabled;
    }

    public async void RefreshProcessList()
    {
        if (_refreshingProcesses) return;
        _refreshingProcesses = true;

        try
        {
            var excludedSet = _monitor?.Optimizer.ExcludedProcesses ?? [];

            var processes = await Task.Run(() =>
            {
                var list = new List<ProcessMemoryInfo>();
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        list.Add(new ProcessMemoryInfo
                        {
                            Pid = proc.Id,
                            Name = proc.ProcessName,
                            WorkingSetBytes = proc.WorkingSet64,
                            IsExcluded = excludedSet.Contains(proc.ProcessName)
                        });
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                return list.OrderByDescending(p => p.WorkingSetBytes).Take(100).ToList();
            });

            // Update ObservableCollection on UI thread
            Processes.Clear();
            foreach (var p in processes)
                Processes.Add(p);
        }
        catch
        {
            // Process enumeration can throw
        }
        finally
        {
            _refreshingProcesses = false;
        }
    }

    // ── Event Handlers ──

    private void OnMemoryUpdated(MemoryInfo info)
    {
        void ApplySnapshot()
        {
            _lastMemoryInfo = info;
            TotalGB = info.TotalGB;
            UsedGB = info.UsedGB;
            AvailableGB = info.AvailableGB;
            CachedGB = info.CachedGB;
            StandbyGB = info.StandbyGB;
            FreeGB = info.FreeGB;
            ModifiedGB = info.ModifiedGB;
            UsagePercent = info.UsagePercent;
            ProcessCount = info.ProcessCount;
            ThreadCount = info.ThreadCount;
            HandleCount = info.HandleCount;
            KernelPagedMB = info.KernelPagedMB;
            KernelNonpagedMB = info.KernelNonpagedMB;
            CommitPercent = info.CommitPercent;
            CompressedMB = info.CompressedMB;
            UpdateGraph(info.UsagePercent);
            MemoryInfoUpdated?.Invoke(info);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            ApplySnapshot();
            return;
        }

        dispatcher.Invoke(ApplySnapshot);
    }

    private void OnOptimizationCompleted(OptimizationResult result)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            OptimizationHistory.Insert(0, result);
            while (OptimizationHistory.Count > _settings.HistoryMaxItems)
                OptimizationHistory.RemoveAt(OptimizationHistory.Count - 1);

            if (result.Success)
            {
                TotalFreedMB += result.FreedMB;
                OptimizationCount++;
            }

            // Show feedback for auto-optimizations (manual already handles its own via OptimizeNow)
            if (!IsOptimizing)
            {
                StatusText = result.Success
                    ? $"Auto-optimized: freed {result.FreedMB:F1} MB"
                    : $"Auto-optimize failed: {result.ErrorMessage}";

                LastOptimizationResult = result;
                ShowOptimizationResult = true;
                _resultDismissTimer?.Stop();
                _resultDismissTimer?.Dispose();
                _resultDismissTimer = new System.Timers.Timer(5000) { AutoReset = false };
                _resultDismissTimer.Elapsed += (_, _) =>
                    Application.Current?.Dispatcher.Invoke(() => ShowOptimizationResult = false);
                _resultDismissTimer.Start();
            }
        });
    }

    private void UpdateGraph(double usagePercent)
    {
        _memoryHistory.Add(usagePercent);
        // Keep 150 points (~5 min at 2s interval)
        while (_memoryHistory.Count > 150)
            _memoryHistory.RemoveAt(0);

        // Build SVG-style polyline points for the graph
        // Canvas: 600w x 100h, points scaled accordingly
        const double graphWidth = 600;
        const double graphHeight = 100;
        var count = _memoryHistory.Count;
        if (count < 2) return;

        var points = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            double x = (double)i / (count - 1) * graphWidth;
            double y = graphHeight - (_memoryHistory[i] / 100.0 * graphHeight);
            points.Append($"{x:F1},{y:F1} ");
        }
        GraphPoints = points.ToString().TrimEnd();
    }

    private void ApplySettings()
    {
        _autoOptimizeEnabled = _settings.AutoOptimizeEnabled;
        _thresholdPercent = _settings.ThresholdPercent;
        _checkIntervalSeconds = _settings.CheckIntervalSeconds;
        _cooldownSeconds = _settings.CooldownSeconds;
        _selectedLevel = _settings.Level.ToString();
        _startWithWindows = _settings.StartWithWindows;
        _minimizeToTray = _settings.MinimizeToTray;
        _cacheMaxPercent = _settings.CacheMaxPercent;
        _selfWorkingSetCapMB = _settings.SelfWorkingSetCapMB;
        _scheduledOptimizeEnabled = _settings.ScheduledOptimizeEnabled;
        _scheduledOptimizeIntervalMinutes = _settings.ScheduledOptimizeIntervalMinutes;
    }

    private void ApplyMonitorSettings()
    {
        Monitor.AutoOptimizeEnabled = _settings.AutoOptimizeEnabled;
        Monitor.ThresholdPercent = _settings.ThresholdPercent;
        Monitor.CooldownSeconds = _settings.CooldownSeconds;
        Monitor.Level = _settings.Level;
        Monitor.CacheMaxPercent = _settings.CacheMaxPercent;
        Monitor.SelfWorkingSetCapMB = _settings.SelfWorkingSetCapMB;
        Monitor.ScheduledOptimizeEnabled = _settings.ScheduledOptimizeEnabled;
        Monitor.ScheduledOptimizeIntervalMinutes = _settings.ScheduledOptimizeIntervalMinutes;
        Monitor.Optimizer.ExcludedProcesses = new HashSet<string>(_settings.ExcludedProcesses, StringComparer.OrdinalIgnoreCase);

        // Algorithmic improvement settings (JSON-only, no UI binding)
        Monitor.HysteresisGap = _settings.HysteresisGap;
        Monitor.TrendWindowSize = _settings.TrendWindowSize;
        Monitor.PredictiveLeadSeconds = _settings.PredictiveLeadSeconds;
        Monitor.AccessedBitsDelayMs = _settings.AccessedBitsDelayMs;
        Monitor.EffectivenessTrackingEnabled = _settings.EffectivenessTrackingEnabled;
    }

    public void SaveWindowState(double width, double height, double left, double top)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.Save();
    }

    public Settings GetSettings() => _settings;

    public void Shutdown()
    {
        if (_monitor != null)
        {
            _monitor.MemoryUpdated -= OnMemoryUpdated;
            _monitor.OptimizationCompleted -= OnOptimizationCompleted;
            _monitor.Dispose();
            _monitor = null;
        }

        _initialized = false;
        _settings.Save();
    }

    private void ToggleProcessExclusion(ProcessMemoryInfo? proc)
    {
        if (proc == null) return;
        // Don't flip IsExcluded here — the CheckBox two-way binding already toggled it
        if (proc.IsExcluded)
        {
            Monitor.Optimizer.ExcludedProcesses.Add(proc.Name);
            if (!_settings.ExcludedProcesses.Contains(proc.Name))
                _settings.ExcludedProcesses.Add(proc.Name);
        }
        else
        {
            Monitor.Optimizer.ExcludedProcesses.Remove(proc.Name);
            _settings.ExcludedProcesses.Remove(proc.Name);
        }
        _settings.SaveDebounced();
    }

    private void SetProcessWorkingSetCap(ProcessMemoryInfo? proc)
    {
        if (proc == null || proc.WorkingSetCapMB <= 0) return;
        var success = MemoryOptimizer.SetProcessWorkingSetCap(proc.Pid, proc.WorkingSetCapMB * 1024L * 1024);
        StatusText = success
            ? $"Set {proc.Name} cap to {proc.WorkingSetCapMB} MB"
            : $"Failed to cap {proc.Name}";
    }

    private void SetProcessMemoryPriorityAction(ProcessMemoryInfo? proc)
    {
        if (proc == null) return;
        uint priority = proc.MemoryPriority switch
        {
            "Very Low" => Native.NativeMethods.MEMORY_PRIORITY_VERY_LOW,
            "Low" => Native.NativeMethods.MEMORY_PRIORITY_LOW,
            "Medium" => Native.NativeMethods.MEMORY_PRIORITY_MEDIUM,
            "Below Normal" => Native.NativeMethods.MEMORY_PRIORITY_BELOW_NORMAL,
            _ => Native.NativeMethods.MEMORY_PRIORITY_NORMAL,
        };
        bool lowPriority = priority <= Native.NativeMethods.MEMORY_PRIORITY_LOW;
        var success = MemoryOptimizer.SetProcessMemoryPriority(proc.Pid, priority, setIoPriority: lowPriority);
        StatusText = success
            ? $"Set {proc.Name} memory priority: {proc.MemoryPriority}" + (lowPriority ? " + low I/O" : "")
            : $"Failed to set priority on {proc.Name}";
    }

    private static void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Verb = "runas",
                UseShellExecute = true
            });
            Application.Current?.Shutdown();
        }
        catch { /* User cancelled UAC */ }
    }

    private static bool SetStartWithWindows(bool enable)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        return TaskSchedulerHelper.CreateTask(exePath, startAtLogon: enable);
    }
}
