using System.Diagnostics;
using System.Runtime.InteropServices;
using RAMSpeed.Models;
using RAMSpeed.Native;

namespace RAMSpeed.Services;

internal class MemoryOptimizer : IDisposable
{
    private static readonly HashSet<string> DefaultExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "services", "lsass",
        "svchost", "dwm", "winlogon", "Memory Compression", "Registry",
        "fontdrvhost", "conhost"
    };

    private readonly MemoryInfoService _memoryInfo = new();
    private bool _disposed;

    public HashSet<string> ExcludedProcesses { get; set; } = new(DefaultExclusions, StringComparer.OrdinalIgnoreCase);

    public (int trimmed, int failed, int skipped, bool earlyExit) TrimProcessWorkingSets(long targetAvailableBytes = 0)
    {
        ThrowIfDisposed();

        int foregroundPid = NativeMethods.GetForegroundProcessId();
        int selfPid = Environment.ProcessId;

        int skipped = 0;
        var processInfos = new List<(int pid, long workingSet)>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (ExcludedProcesses.Contains(proc.ProcessName)
                    || proc.Id == foregroundPid
                    || proc.Id == selfPid)
                {
                    skipped++;
                    continue;
                }

                processInfos.Add((proc.Id, proc.WorkingSet64));
            }
            catch
            {
                skipped++;
            }
            finally
            {
                proc.Dispose();
            }
        }

        // Sort by working set descending — trim biggest consumers first
        processInfos.Sort((a, b) => b.workingSet.CompareTo(a.workingSet));

        int trimmed = 0, failed = 0;
        bool earlyExit = false;

        foreach (var (pid, workingSet) in processInfos)
        {
            try
            {
                var handle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                    false, pid);

                if (handle == IntPtr.Zero)
                {
                    failed++;
                    continue;
                }

                try
                {
                    if (NativeMethods.EmptyWorkingSet(handle))
                        trimmed++;
                    else
                        failed++;
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }

                // Early exit check every 10 successful trims
                if (targetAvailableBytes > 0 && trimmed > 0 && trimmed % 10 == 0)
                {
                    if ((long)GetAvailablePhysicalBytesQuick() >= targetAvailableBytes)
                    {
                        earlyExit = true;
                        break;
                    }
                }
            }
            catch
            {
                failed++;
            }
        }

        return (trimmed, failed, skipped, earlyExit);
    }

    public bool PurgeStandbyList()
    {
        ThrowIfDisposed();

        int command = (int)NativeMethods.MemoryListCommand.MemoryPurgeStandbyList;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0; // NT_SUCCESS
    }

    public bool PurgeLowPriorityStandby()
    {
        ThrowIfDisposed();

        int command = (int)NativeMethods.MemoryListCommand.MemoryPurgeLowPriorityStandbyList;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool FlushModifiedList()
    {
        ThrowIfDisposed();

        int command = (int)NativeMethods.MemoryListCommand.MemoryFlushModifiedList;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool CaptureAndResetAccessedBits()
    {
        ThrowIfDisposed();

        int command = (int)NativeMethods.MemoryListCommand.MemoryCaptureAndResetAccessedBits;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool EmptySystemWorkingSets()
    {
        ThrowIfDisposed();

        int command = (int)NativeMethods.MemoryListCommand.MemoryEmptyWorkingSets;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool FlushSystemFileCache()
    {
        ThrowIfDisposed();
        return NativeMethods.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0);
    }

    /// <summary>Set a hard max file cache size. Pass 0 to clear limit.</summary>
    public bool SetFileCacheHardMax(long maxBytes)
    {
        ThrowIfDisposed();

        if (maxBytes <= 0)
        {
            // Remove hard limit
            return NativeMethods.SetSystemFileCacheSize(IntPtr.Zero, IntPtr.Zero,
                NativeMethods.FILE_CACHE_MAX_HARD_DISABLE);
        }
        return NativeMethods.SetSystemFileCacheSize(IntPtr.Zero, new IntPtr(maxBytes),
            NativeMethods.FILE_CACHE_MAX_HARD_ENABLE);
    }

    /// <summary>Set a hard working set cap on a target process.</summary>
    public static bool SetProcessWorkingSetCap(int pid, long maxBytes)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
            false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var minSize = new IntPtr(1024 * 1024); // 1 MB
            var maxSize = new IntPtr(maxBytes);
            return NativeMethods.SetProcessWorkingSetSizeEx(handle, minSize, maxSize,
                NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE);
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    /// <summary>Set memory priority on a target process. Optionally also lowers I/O priority.</summary>
    public static bool SetProcessMemoryPriority(int pid, uint priority, bool setIoPriority = false)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            bool result = NativeMethods.SetProcessMemoryPriority(handle, priority);
            if (setIoPriority && priority <= NativeMethods.MEMORY_PRIORITY_LOW)
                NativeMethods.SetProcessIoPriority(handle, NativeMethods.IO_PRIORITY_LOW);
            return result;
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    /// <summary>Flush dirty registry hives from memory to disk. Windows 8.1+.</summary>
    public bool FlushRegistryCache()
    {
        ThrowIfDisposed();
        int result = NativeMethods.NtSetSystemInformationNull(
            NativeMethods.SystemRegistryReconciliationInformation, IntPtr.Zero, 0);
        return result >= 0;
    }

    /// <summary>
    /// Combine identical physical pages (deduplication). Requires SeProfileSingleProcessPrivilege.
    /// May be disabled by policy (CVE-2016-3272). Returns pages combined, or -1 on failure.
    /// </summary>
    public long CombinePhysicalMemory()
    {
        ThrowIfDisposed();
        var info = new NativeMethods.MEMORY_COMBINE_INFORMATION_EX();
        int result = NativeMethods.NtSetSystemInformationCombine(
            NativeMethods.SystemCombinePhysicalMemoryInformation,
            ref info,
            Marshal.SizeOf<NativeMethods.MEMORY_COMBINE_INFORMATION_EX>());
        return result >= 0 ? (long)(ulong)info.PagesCombined : -1;
    }

    public static void TrimSelf(SelfTrimReason reason, int workingSetCapMB = 25)
    {
        if (reason is SelfTrimReason.Startup or SelfTrimReason.PostOptimization)
        {
            GC.Collect(2, GCCollectionMode.Optimized, true, true);
            GC.WaitForPendingFinalizers();
        }

        // Use the NativeMethods pseudo-handle (-1) instead of Process.GetCurrentProcess().Handle
        // to avoid leaking a Process object on every call (this runs on every timer tick).
        try
        {
            NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
        }
        catch { /* EmptyWorkingSet can throw under extreme memory pressure */ }

        // Apply hard working set cap
        if (workingSetCapMB > 0)
        {
            try { NativeMethods.SetSelfWorkingSetCap(workingSetCapMB * 1024L * 1024); } catch { }
        }
    }

    public OptimizationResult OptimizeAll(
        OptimizationLevel level = OptimizationLevel.Balanced,
        int cacheMaxPercent = 0,
        int targetThresholdPercent = 0,
        bool isLowMemory = false) // Used by Layer 3: compressed memory awareness
    {
        ThrowIfDisposed();

        var sw = Stopwatch.StartNew();
        var methodsUsed = new List<string>();
        var beforeInfo = _memoryInfo.GetCurrentMemoryInfo();
        var beforeAvailable = (double)beforeInfo.AvailablePhysicalBytes;
        var actualLevel = OptimizationLevel.Conservative;

        int processesTrimmed = 0;

        try
        {
            // Compute early-exit target for sorted trimming
            long trimTarget = 0;
            if (targetThresholdPercent > 0 && beforeInfo.TotalPhysicalBytes > 0)
            {
                trimTarget = (long)((double)beforeInfo.TotalPhysicalBytes * (1.0 - targetThresholdPercent / 100.0));
            }

            // Step 1: Trim process working sets (all levels)
            var (trimmed, _, _, earlyExit) = TrimProcessWorkingSets(trimTarget);
            processesTrimmed = trimmed;
            methodsUsed.Add(earlyExit ? "Working Set Trim (early exit)" : "Working Set Trim");

            // Adaptive escalation: check if Conservative was enough
            if (targetThresholdPercent > 0 && GetUsagePercentQuick() < targetThresholdPercent)
            {
                return BuildResult(sw, methodsUsed, beforeAvailable, processesTrimmed, actualLevel);
            }

            if (level >= OptimizationLevel.Balanced)
            {
                actualLevel = OptimizationLevel.Balanced;

                // Step 2: Flush modified page list
                if (FlushModifiedList())
                    methodsUsed.Add("Modified List Flush");

                // Step 3: Reset accessed bits
                if (CaptureAndResetAccessedBits())
                    methodsUsed.Add("Access Bits Reset");

                // Step 4: Purge standby (balanced = low-priority only)
                if (level == OptimizationLevel.Balanced)
                {
                    if (PurgeLowPriorityStandby())
                        methodsUsed.Add("Low-Priority Standby Purge");
                }

                // Step 5: Flush system file cache
                if (FlushSystemFileCache())
                    methodsUsed.Add("File Cache Flush");

                // Step 6: Flush registry cache
                if (FlushRegistryCache())
                    methodsUsed.Add("Registry Cache Flush");

                // Step 7: Page combining
                var pagesCombined = CombinePhysicalMemory();
                if (pagesCombined > 0)
                    methodsUsed.Add($"Page Combine ({pagesCombined} pages)");

                // Step 8: File cache cap
                if (cacheMaxPercent > 0)
                {
                    var totalRam = (double)beforeInfo.TotalPhysicalBytes;
                    var maxCacheBytes = (long)(totalRam * cacheMaxPercent / 100.0);
                    if (SetFileCacheHardMax(maxCacheBytes))
                        methodsUsed.Add($"Cache Cap {cacheMaxPercent}%");
                }

                // Adaptive escalation: check if Balanced was enough
                if (targetThresholdPercent > 0 && GetUsagePercentQuick() < targetThresholdPercent)
                {
                    return BuildResult(sw, methodsUsed, beforeAvailable, processesTrimmed, actualLevel);
                }

                // Aggressive-only steps
                if (level >= OptimizationLevel.Aggressive)
                {
                    actualLevel = OptimizationLevel.Aggressive;

                    if (EmptySystemWorkingSets())
                        methodsUsed.Add("System Working Set Empty");
                    if (PurgeStandbyList())
                        methodsUsed.Add("Standby List Purge");
                }
            }

            return BuildResult(sw, methodsUsed, beforeAvailable, processesTrimmed, actualLevel);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new OptimizationResult
            {
                Timestamp = DateTime.Now,
                Duration = sw.Elapsed,
                Success = false,
                ErrorMessage = ex.Message,
                MethodsUsed = methodsUsed.ToArray(),
                ActualLevelUsed = actualLevel
            };
        }
    }

    private OptimizationResult BuildResult(
        Stopwatch sw, List<string> methodsUsed,
        double beforeAvailable, int processesTrimmed,
        OptimizationLevel actualLevel)
    {
        sw.Stop();
        var afterInfo = _memoryInfo.GetCurrentMemoryInfo();
        var afterAvailable = (double)afterInfo.AvailablePhysicalBytes;
        var freed = (long)(afterAvailable - beforeAvailable);

        return new OptimizationResult
        {
            Timestamp = DateTime.Now,
            MemoryBeforeMB = beforeAvailable / (1024 * 1024),
            MemoryAfterMB = afterAvailable / (1024 * 1024),
            MemoryFreedBytes = Math.Max(0, freed),
            ProcessesTrimmed = processesTrimmed,
            Duration = sw.Elapsed,
            MethodsUsed = methodsUsed.ToArray(),
            Success = true,
            ActualLevelUsed = actualLevel
        };
    }

    // Used by TrimProcessWorkingSets (sorted trimming + early exit) and effectiveness tracking
    private static ulong GetAvailablePhysicalBytesQuick()
    {
        var ms = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref ms);
        return ms.ullAvailPhys;
    }

    private static int GetUsagePercentQuick()
    {
        var ms = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref ms);
        return (int)ms.dwMemoryLoad;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _memoryInfo.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
