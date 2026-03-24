# RAMSpeed Performance Optimization — Design Spec

## Problem

The app window appears but memory stats take 5-15 seconds to populate (PerformanceCounter first-read latency). Settings changes and the process list freeze the UI (synchronous file I/O and process enumeration on the dispatcher thread). Tray icon re-renders on every 2-second tick even when the percentage hasn't changed.

## Approach: Targeted Async Offload

Move blocking operations off the UI thread with minimal architectural changes. Five fixes, each independent.

## Files Modified

| File | Changes |
|---|---|
| `src/RAMSpeed/Services/MemoryInfoService.cs` | Pre-warm perf counters on background thread; cache compressed memory lookup |
| `src/RAMSpeed/Services/MemoryMonitor.cs` | Start monitor with pre-warmed counters; async-aware first refresh |
| `src/RAMSpeed/Models/Settings.cs` | Debounced async Save() |
| `src/RAMSpeed/ViewModels/MainViewModel.cs` | Move process list enumeration to Task.Run; debounce settings saves |
| `src/RAMSpeed/Services/TrayIconService.cs` | Cache icon renders; skip re-render when percentage unchanged |

## Fix 1: Pre-warm PerformanceCounters on Background Thread

**Root cause:** `PerformanceCounter.NextValue()` takes 1-3s per counter on first call. 5 counters = 5-15s delay before first memory reading.

**Fix:** Defer both PerformanceCounter creation AND priming to a background thread. The constructor becomes a no-op for counters — they are created and primed entirely in `WarmUpAsync()`.

**Thread safety:** Use a single `_perfCountersAvailable` flag (volatile bool). It starts `false`. `GetCurrentMemoryInfo()` only reads counters when the flag is `true`. `WarmUpAsync()` creates counters, primes them, and sets the flag to `true` as the last step. No concurrent access occurs because the flag gates all reads, and counter creation + priming happens entirely within the Task.Run closure before the flag flips.

```csharp
// MemoryInfoService — constructor change
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
            _perfCountersAvailable = true;  // atomic flag flip — gates all reads
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
```

`GetCurrentMemoryInfo()` is unchanged — it already checks `_perfCountersAvailable` before reading counters (line 64) and falls back to estimation (lines 80-85) when false. No new flag needed.

In `MemoryMonitor.Start()`:
```csharp
public void Start(int intervalSeconds = 2)
{
    _timer.Interval = TimeSpan.FromSeconds(intervalSeconds);
    _timer.Start();
    RefreshMemoryInfo();  // fast: P/Invoke only, no perf counters yet
    MaybeTrimSelf(SelfTrimReason.Startup);
    _memoryInfoService.WarmUpAsync();  // fire-and-forget background warm-up
}
```

**Result:** First memory reading appears instantly (P/Invoke data). Perf counter detail fills in within 5-15s in background. No UI freeze. Initial standby/modified/free values use the estimation path — this is a known behavioral change (estimated values instead of blank) and is acceptable.

## Fix 2: Cache Compressed Memory Lookup

**Root cause:** `Process.GetProcessesByName("Memory Compression")` is called every 2-5 seconds inside `GetCurrentMemoryInfo()`. Each call enumerates all processes (~50-100ms).

**Fix:** Cache the result with a 30-second TTL.

```csharp
private ulong _cachedCompressedBytes;
private DateTime _compressedCacheExpiry;

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
```

Make the method non-static (it now has instance state). Update the call in `GetCurrentMemoryInfo()`.

**Result:** 50-100ms saved on every poll except every 30th second.

## Fix 3: Debounced Async Settings Save

**Root cause:** `Settings.Save()` does synchronous `File.WriteAllText()`. Every slider move, toggle, or dropdown change triggers an immediate disk write on the UI thread.

**Fix:** Replace synchronous `Save()` with a debounced async version. The debounce waits 500ms after the last change before writing — so dragging a slider generates one write, not 50.

**Thread safety:** Snapshot the settings to a JSON string on the UI thread (fast, ~0.1ms), then write the string to disk on the background thread. This avoids torn reads on `List<string>` properties.

```csharp
private CancellationTokenSource? _saveCts;

public void SaveDebounced()
{
    var oldCts = _saveCts;
    oldCts?.Cancel();
    _saveCts = new CancellationTokenSource();
    var token = _saveCts.Token;
    oldCts?.Dispose();

    // Snapshot on UI thread to avoid torn reads
    var json = JsonSerializer.Serialize(this, JsonOptions);

    Task.Run(async () =>
    {
        try
        {
            await Task.Delay(500, token);
            Directory.CreateDirectory(SettingsDir);
            await File.WriteAllTextAsync(SettingsFile, json, token);
        }
        catch (OperationCanceledException) { }
        catch { }
    });
}
```

**Which callers to change:**
- All property setters in MainViewModel that call `_settings.Save()` for UI-driven changes (sliders, toggles, dropdowns) → `_settings.SaveDebounced()`
- `SaveWindowState()` → keep synchronous `_settings.Save()` (runs during window close, must persist before exit)
- Shutdown path → keep synchronous `_settings.Save()`
- `ToggleProcessExclusion()` → use `_settings.SaveDebounced()` (discrete action, not slider drag, but no reason to block UI)

**Result:** Settings changes are instant. No UI freeze on slider drags.

## Fix 4: Process List Enumeration Off UI Thread

**Root cause:** `RefreshProcessList()` in MainViewModel calls `Process.GetProcesses()` and reads properties for each process synchronously on the dispatcher thread. With 200+ processes, this takes 200-500ms and freezes the UI.

**Fix:** Move the enumeration to `Task.Run()` and update the UI via dispatcher.

Find the `RefreshProcessList()` method in MainViewModel. Wrap the process enumeration in `Task.Run()`:

Add a `_refreshingProcesses` guard to prevent concurrent enumerations:

```csharp
private bool _refreshingProcesses;

private async void RefreshProcessList()
{
    if (_refreshingProcesses) return;
    _refreshingProcesses = true;

    try
    {
        var processes = await Task.Run(() =>
        {
            var list = new List<ProcessMemoryInfo>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    list.Add(new ProcessMemoryInfo
                    {
                        // ... existing property reads
                    });
                }
                catch { }
                finally { proc.Dispose(); }
            }
            return list;
        });

        // Update ObservableCollection on UI thread
        Processes.Clear();
        foreach (var p in processes)
            Processes.Add(p);
    }
    finally
    {
        _refreshingProcesses = false;
    }
}
```

Note: The public property is `Processes` (not `_processes`).

**Result:** Process list populates without freezing the UI. Double-click guard prevents duplicate entries.

## Fix 5: Cache Tray Icon Renders

**Root cause:** `TrayIconService.UpdateIcon()` calls `RenderPercentageIcon()` on every memory update (every 2-5s). Each render creates a Bitmap, Graphics, Font, measures text, and converts to Icon. Most of the time the percentage hasn't changed.

**Fix:** Track the last rendered percentage in the existing `UpdateIcon` method (which is `private` and takes `double`). Truncate to int for comparison. Invalidate cache on theme change.

```csharp
private int _lastRenderedPercent = -1;

private void UpdateIcon(double usagePercent)
{
    int truncated = (int)usagePercent;
    if (truncated == _lastRenderedPercent)
        return;

    _lastRenderedPercent = truncated;
    // ... existing RenderPercentageIcon + icon assignment logic
}
```

**Theme change handling:** In `OnTaskbarThemeChanged`, reset the cache before calling `UpdateIcon`:
```csharp
private void OnTaskbarThemeChanged(...)
{
    _lastRenderedPercent = -1;  // force re-render with new theme colors
    UpdateIcon(_lastUsagePercent);
}
```

Method stays `private`. Signature stays `double`. No callers change.

**Result:** Icon rendering drops from every-2-seconds to only-when-percentage-changes. Theme changes correctly force a re-render.

## Backward Compatibility

All fixes are internal. No settings changes, no API changes, no UI changes. Default behavior is identical — things just happen faster.

## eARA Gates

- Must compile with 0 errors
- All 28 existing tests must pass
- App launches and shows memory stats within 2 seconds (P/Invoke path)
- Settings slider drag does not freeze UI
- Process list click does not freeze UI
- No new warnings

## Non-Goals

- No architectural rework (keep synchronous MemoryMonitor pattern)
- No PerformanceCounter replacement with NtQuerySystemInformation (future work)
- No installer (separate work stream)
- No UI changes
