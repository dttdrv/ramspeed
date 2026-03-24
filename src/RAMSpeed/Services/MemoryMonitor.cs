using System.Windows.Threading;
using RAMSpeed.Models;
using RAMSpeed.Native;

namespace RAMSpeed.Services;

internal class MemoryMonitor : IDisposable
{
    private readonly MemoryInfoService _memoryInfoService;
    private readonly MemoryOptimizer _optimizer;
    private readonly DispatcherTimer _timer;
    private readonly Func<DateTime> _utcNow;
    private DateTime _lastOptimization = DateTime.MinValue;
    private DateTime? _lastSelfTrimUtc;
    private bool _threadOptimized;
    private int _optimizationInProgress;
    private bool _disposed;
    private bool _armed = true;
    private readonly Queue<(DateTime time, double usage)> _usageHistory = new();

    // Memory resource notification handles
    private IntPtr _lowMemoryHandle;
    private IntPtr _highMemoryHandle;

    public event Action<MemoryInfo>? MemoryUpdated;
    public event Action<OptimizationResult>? OptimizationCompleted;

    public bool AutoOptimizeEnabled { get; set; }
    public int ThresholdPercent { get; set; } = 80;
    public int CooldownSeconds { get; set; } = 30;
    public OptimizationLevel Level { get; set; } = OptimizationLevel.Balanced;
    public int SelfWorkingSetCapMB { get; set; } = 25;
    public int CacheMaxPercent { get; set; } = 0;
    public bool ScheduledOptimizeEnabled { get; set; }
    public int ScheduledOptimizeIntervalMinutes { get; set; } = 30;
    public int HysteresisGap { get; set; } = 10;
    public int TrendWindowSize { get; set; } = 10;
    public int PredictiveLeadSeconds { get; set; } = 15;
    public int AccessedBitsDelayMs { get; set; } = 2000;

    public MemoryOptimizer Optimizer => _optimizer;
    public MemoryInfo? LastMemoryInfo { get; private set; }

    /// <summary>True when OS reports low memory condition.</summary>
    public bool IsLowMemory { get; private set; }

    public MemoryMonitor()
        : this(new MemoryInfoService(), new MemoryOptimizer(), null, null)
    {
    }

    internal MemoryMonitor(
        MemoryInfoService memoryInfoService,
        MemoryOptimizer optimizer,
        DispatcherTimer? timer = null,
        Func<DateTime>? utcNow = null)
    {
        _memoryInfoService = memoryInfoService;
        _optimizer = optimizer;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _timer = timer ?? new DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += OnTimerTick;

        // Create OS memory resource notification handles
        _lowMemoryHandle = NativeMethods.CreateMemoryResourceNotification(
            NativeMethods.LowMemoryResourceNotification);
        _highMemoryHandle = NativeMethods.CreateMemoryResourceNotification(
            NativeMethods.HighMemoryResourceNotification);
    }

    public void Start(int intervalSeconds = 2)
    {
        ThrowIfDisposed();
        _timer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        _timer.Start();
        // Immediately fire first update
        RefreshMemoryInfo();
        MaybeTrimSelf(SelfTrimReason.Startup);
    }

    public void Stop()
    {
        if (_disposed)
            return;

        _timer.Stop();
    }

    public void SetInterval(int seconds)
    {
        ThrowIfDisposed();
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    public void RefreshMemoryInfo()
    {
        ThrowIfDisposed();
        LastMemoryInfo = _memoryInfoService.GetCurrentMemoryInfo();
        MemoryUpdated?.Invoke(LastMemoryInfo);
    }

    public OptimizationResult RunOptimization(OptimizationLevel? levelOverride = null)
    {
        ThrowIfDisposed();

        Interlocked.Increment(ref _optimizationInProgress);
        try
        {
            var result = _optimizer.OptimizeAll(levelOverride ?? Level, CacheMaxPercent, ThresholdPercent, IsLowMemory, AccessedBitsDelayMs);
            _lastOptimization = DateTime.Now;
            OptimizationCompleted?.Invoke(result);

            // Refresh memory info after optimization
            RefreshMemoryInfo();

            if (result.Success)
                MaybeTrimSelf(SelfTrimReason.PostOptimization);

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _optimizationInProgress);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        // Apply per-thread optimizations once (on the dispatcher thread — the timer thread)
        if (!_threadOptimized)
        {
            try { NativeMethods.SetCurrentThreadMemoryPriority(NativeMethods.MEMORY_PRIORITY_LOW); } catch { }
            try { NativeMethods.SetCurrentThreadEcoQoS(); } catch { }
            _threadOptimized = true;
        }

        RefreshMemoryInfo();

        if (LastMemoryInfo != null)
        {
            _usageHistory.Enqueue((_utcNow(), LastMemoryInfo.UsagePercent));
            while (_usageHistory.Count > TrendWindowSize)
                _usageHistory.Dequeue();
        }

        MaybeTrimSelf(SelfTrimReason.Periodic);

        // Query OS memory resource notifications
        CheckMemoryResourceNotifications();

        // Scheduled optimization (time-based, independent of threshold)
        if (ScheduledOptimizeEnabled && ScheduledOptimizeIntervalMinutes > 0 && LastMemoryInfo != null)
        {
            if ((DateTime.Now - _lastOptimization).TotalMinutes >= ScheduledOptimizeIntervalMinutes)
            {
                RunOptimization();
                return;
            }
        }

        if (!AutoOptimizeEnabled || LastMemoryInfo == null)
            return;

        // Check cooldown
        if ((DateTime.Now - _lastOptimization).TotalSeconds < CooldownSeconds)
            return;

        // Hysteresis: re-arm when usage drops below threshold minus gap
        if (!_armed && LastMemoryInfo.UsagePercent < (ThresholdPercent - HysteresisGap))
            _armed = true;

        // Predictive trigger: if slope predicts threshold breach within lead time
        if (_armed && !IsLowMemory && LastMemoryInfo.UsagePercent < ThresholdPercent)
        {
            double slope = ComputeUsageTrend();
            if (slope > 0)
            {
                double predicted = LastMemoryInfo.UsagePercent + slope * PredictiveLeadSeconds;
                if (predicted >= ThresholdPercent)
                {
                    RunOptimization();
                    _armed = false;
                    return;
                }
            }
        }

        // Auto-optimize: OS low-memory always fires; threshold requires armed state
        if (IsLowMemory || (_armed && LastMemoryInfo.UsagePercent >= ThresholdPercent))
        {
            RunOptimization();
            _armed = false;
        }
    }

    private double ComputeUsageTrend()
    {
        if (_usageHistory.Count < 3)
            return 0;

        var samples = _usageHistory.ToArray();
        var t0 = samples[0].time;
        int n = samples.Length;
        double sumT = 0, sumU = 0, sumTU = 0, sumT2 = 0;

        for (int i = 0; i < n; i++)
        {
            double t = (samples[i].time - t0).TotalSeconds;
            double u = samples[i].usage;
            sumT += t;
            sumU += u;
            sumTU += t * u;
            sumT2 += t * t;
        }

        double denominator = n * sumT2 - sumT * sumT;
        if (Math.Abs(denominator) < 0.0001)
            return 0;

        return (n * sumTU - sumT * sumU) / denominator;
    }

    private void CheckMemoryResourceNotifications()
    {
        if (_lowMemoryHandle != IntPtr.Zero &&
            NativeMethods.QueryMemoryResourceNotification(_lowMemoryHandle, out bool isLow))
        {
            IsLowMemory = isLow;
        }
    }

    internal bool MaybeTrimSelf(SelfTrimReason reason)
    {
        var nowUtc = _utcNow();
        if (!SelfTrimPolicy.ShouldTrim(reason, nowUtc, _lastSelfTrimUtc, CooldownSeconds, SelfWorkingSetCapMB, IsOptimizing))
            return false;

        MemoryOptimizer.TrimSelf(reason, SelfWorkingSetCapMB);
        _lastSelfTrimUtc = nowUtc;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;

        CloseHandle(ref _lowMemoryHandle);
        CloseHandle(ref _highMemoryHandle);

        _memoryInfoService.Dispose();
        _optimizer.Dispose();

        GC.SuppressFinalize(this);
    }

    private bool IsOptimizing => Volatile.Read(ref _optimizationInProgress) > 0;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void CloseHandle(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        NativeMethods.CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}
