using System.Diagnostics;
using System.Runtime.InteropServices;
using optiRAM.Models;
using optiRAM.Native;

namespace optiRAM.Services;

internal class MemoryInfoService : IDisposable
{
    private PerformanceCounter? _standbyNormalCounter;
    private PerformanceCounter? _standbyReserveCounter;
    private PerformanceCounter? _standbyCoreCounter;
    private PerformanceCounter? _modifiedCounter;
    private PerformanceCounter? _freeCounter;
    private bool _perfCountersAvailable;
    private bool _disposed;
    private ulong _cachedCompressedBytes;
    private DateTime _compressedCacheExpiry = DateTime.UtcNow.AddSeconds(30);

    public MemoryInfoService()
    {
        // Counters are NOT created here. WarmUpAsync() handles creation + priming.
        _perfCountersAvailable = false;
    }

    public Task WarmUpAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                _standbyNormalCounter = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes");
                _standbyReserveCounter = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes");
                _standbyCoreCounter = new PerformanceCounter("Memory", "Standby Cache Core Bytes");
                _modifiedCounter = new PerformanceCounter("Memory", "Modified Page List Bytes");
                _freeCounter = new PerformanceCounter("Memory", "Free & Zero Page List Bytes");
                _standbyNormalCounter.NextValue();
                _standbyReserveCounter.NextValue();
                _standbyCoreCounter.NextValue();
                _modifiedCounter.NextValue();
                _freeCounter.NextValue();
                _perfCountersAvailable = true;
            }
            catch
            {
                _perfCountersAvailable = false;
                DisposeCounter(ref _standbyNormalCounter);
                DisposeCounter(ref _standbyReserveCounter);
                DisposeCounter(ref _standbyCoreCounter);
                DisposeCounter(ref _modifiedCounter);
                DisposeCounter(ref _freeCounter);
            }
        });
    }

    public MemoryInfo GetCurrentMemoryInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var memStatus = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref memStatus);

        var perfInfo = new NativeMethods.PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>() };
        NativeMethods.GetPerformanceInfo(ref perfInfo, perfInfo.cb);

        var pageSize = (ulong)perfInfo.PageSize;
        var physTotal = (ulong)perfInfo.PhysicalTotal * pageSize;
        var physAvail = (ulong)perfInfo.PhysicalAvailable * pageSize;
        var sysCache = (ulong)perfInfo.SystemCache * pageSize;
        var available = memStatus.ullAvailPhys;

        ulong standbyBytes = 0, modifiedBytes = 0, freeBytes = 0;

        if (_perfCountersAvailable)
        {
            try
            {
                standbyBytes = (ulong)_standbyNormalCounter!.NextValue()
                             + (ulong)_standbyReserveCounter!.NextValue()
                             + (ulong)_standbyCoreCounter!.NextValue();
                modifiedBytes = (ulong)_modifiedCounter!.NextValue();
                freeBytes = (ulong)_freeCounter!.NextValue();
            }
            catch
            {
                _perfCountersAvailable = false;
            }
        }

        if (!_perfCountersAvailable)
        {
            // Fallback estimation
            standbyBytes = available > 0 ? Math.Min(sysCache, available) : 0UL;
            freeBytes = available > standbyBytes ? available - standbyBytes : 0UL;
        }

        return new MemoryInfo
        {
            TotalPhysicalBytes = physTotal,
            AvailablePhysicalBytes = available,
            CachedBytes = sysCache,
            StandbyBytes = standbyBytes,
            FreeBytes = freeBytes,
            ModifiedBytes = modifiedBytes,
            TotalPageFileBytes = memStatus.ullTotalPageFile,
            AvailablePageFileBytes = memStatus.ullAvailPageFile,
            PageSize = pageSize,
            ProcessCount = perfInfo.ProcessCount,
            ThreadCount = perfInfo.ThreadCount,
            HandleCount = perfInfo.HandleCount,
            KernelTotalBytes = (ulong)perfInfo.KernelTotal * pageSize,
            KernelPagedBytes = (ulong)perfInfo.KernelPaged * pageSize,
            KernelNonpagedBytes = (ulong)perfInfo.KernelNonpaged * pageSize,
            CommitTotalBytes = (ulong)perfInfo.CommitTotal * pageSize,
            CommitLimitBytes = (ulong)perfInfo.CommitLimit * pageSize,
            CompressedBytes = GetCompressedMemoryBytes(),
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _perfCountersAvailable = false;

        DisposeCounter(ref _standbyNormalCounter);
        DisposeCounter(ref _standbyReserveCounter);
        DisposeCounter(ref _standbyCoreCounter);
        DisposeCounter(ref _modifiedCounter);
        DisposeCounter(ref _freeCounter);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Read compressed memory size from the "Memory Compression" process working set.
    /// Cached with a 30-second TTL to avoid expensive process enumeration on every poll.
    /// </summary>
    private ulong GetCompressedMemoryBytes()
    {
        if (DateTime.UtcNow < _compressedCacheExpiry)
            return _cachedCompressedBytes;

        try
        {
            var procs = Process.GetProcessesByName("Memory Compression");
            if (procs.Length > 0)
            {
                _cachedCompressedBytes = (ulong)procs[0].WorkingSet64;
                foreach (var p in procs) p.Dispose();
            }
            else
            {
                _cachedCompressedBytes = 0;
            }
        }
        catch { _cachedCompressedBytes = 0; }

        _compressedCacheExpiry = DateTime.UtcNow.AddSeconds(30);
        return _cachedCompressedBytes;
    }

    private static void DisposeCounter(ref PerformanceCounter? counter)
    {
        counter?.Dispose();
        counter = null;
    }
}
