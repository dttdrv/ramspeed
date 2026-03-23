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

    public (int trimmed, int failed, int skipped) TrimProcessWorkingSets()
    {
        ThrowIfDisposed();

        // Protect foreground app and self from trimming
        int foregroundPid = NativeMethods.GetForegroundProcessId();
        int selfPid = Environment.ProcessId;

        int trimmed = 0, failed = 0, skipped = 0;
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

                var handle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                    false, proc.Id);

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
            }
            catch
            {
                failed++;
            }
            finally
            {
                proc.Dispose();
            }
        }

        return (trimmed, failed, skipped);
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

    public OptimizationResult OptimizeAll(OptimizationLevel level = OptimizationLevel.Balanced, int cacheMaxPercent = 0)
    {
        ThrowIfDisposed();

        var sw = Stopwatch.StartNew();
        var methodsUsed = new List<string>();
        var beforeInfo = _memoryInfo.GetCurrentMemoryInfo();
        var beforeAvailable = (double)beforeInfo.AvailablePhysicalBytes;

        int processesTrimmed = 0;

        try
        {
            // Step 1: Trim process working sets (all levels)
            var (trimmed, _, _) = TrimProcessWorkingSets();
            processesTrimmed = trimmed;
            methodsUsed.Add("Working Set Trim");

            if (level >= OptimizationLevel.Balanced)
            {
                // Step 2: Flush modified page list
                if (FlushModifiedList())
                    methodsUsed.Add("Modified List Flush");

                // Step 3: Two-phase — reset accessed bits so untouched pages become trim candidates
                if (CaptureAndResetAccessedBits())
                    methodsUsed.Add("Access Bits Reset");

                // Step 4: Purge standby list (balanced = low-priority only, aggressive = all)
                if (level == OptimizationLevel.Balanced)
                {
                    if (PurgeLowPriorityStandby())
                        methodsUsed.Add("Low-Priority Standby Purge");
                }
                else
                {
                    // Aggressive: system-wide working set empty + full standby purge
                    if (EmptySystemWorkingSets())
                        methodsUsed.Add("System Working Set Empty");
                    if (PurgeStandbyList())
                        methodsUsed.Add("Standby List Purge");
                }

                // Step 5: Flush system file cache
                if (FlushSystemFileCache())
                    methodsUsed.Add("File Cache Flush");

                // Step 6: Flush registry cache (dirty hives → disk)
                if (FlushRegistryCache())
                    methodsUsed.Add("Registry Cache Flush");

                // Step 7: Memory combining (merge identical pages)
                var pagesCombined = CombinePhysicalMemory();
                if (pagesCombined > 0)
                    methodsUsed.Add($"Page Combine ({pagesCombined} pages)");

                // Step 8: Apply hard file cache max if configured
                if (cacheMaxPercent > 0)
                {
                    var totalRam = (double)beforeInfo.TotalPhysicalBytes;
                    var maxCacheBytes = (long)(totalRam * cacheMaxPercent / 100.0);
                    if (SetFileCacheHardMax(maxCacheBytes))
                        methodsUsed.Add($"Cache Cap {cacheMaxPercent}%");
                }
            }

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
                Success = true
            };
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
                MethodsUsed = methodsUsed.ToArray()
            };
        }
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
