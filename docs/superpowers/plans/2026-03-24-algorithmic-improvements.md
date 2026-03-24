# Algorithmic Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement 7 algorithmic improvements to RAMSpeed's memory optimization pipeline using an eARA-adapted development loop (implement, build-verify, keep/revert, log).

**Architecture:** All changes live in the Services layer (`MemoryOptimizer`, `MemoryMonitor`) and Models (`Settings`, `OptimizationResult`). One new file (`StepEffectivenessTracker`). Three implementation layers: Layer 1 restructures the optimizer pipeline, Layer 2 adds 3 independent monitor improvements, Layer 3 adds 3 pipeline refinements that depend on Layer 1.

**Tech Stack:** .NET 8, C#, WPF, xUnit, Windows P/Invoke APIs

**Spec:** `docs/superpowers/specs/2026-03-24-algorithmic-improvements-design.md`

**eARA adaptation:** After each layer, build the solution to verify. If build fails, fix or revert. Log results to `docs/superpowers/specs/results.tsv`.

---

## File Map

| File | Role | Tasks |
|---|---|---|
| `src/RAMSpeed/Models/Settings.cs` | Add 5 new settings | 1 |
| `src/RAMSpeed/Models/OptimizationResult.cs` | Add `ActualLevelUsed` field | 2 |
| `src/RAMSpeed/Services/MemoryOptimizer.cs` | Adaptive escalation, sorted trimming, two-pass, per-step measurement, compressed awareness | 2, 5, 6, 7, 8 |
| `src/RAMSpeed/Services/MemoryMonitor.cs` | Hysteresis, rate-of-change, pass threshold+isLowMemory to optimizer | 3, 4 |
| `src/RAMSpeed/Services/StepEffectivenessTracker.cs` | New: per-step effectiveness history | 7 |
| `tests/RAMSpeed.Tests/StepEffectivenessTrackerTests.cs` | New: unit tests for tracker | 7 |
| `tests/RAMSpeed.Tests/HysteresisTests.cs` | New: unit tests for hysteresis + rate-of-change | 3, 4 |
| `docs/superpowers/specs/results.tsv` | eARA experiment log | 2, 5, 8 |

---

## Layer 1: Adaptive Escalation

### Task 1: Add new settings

**Files:**
- Modify: `src/RAMSpeed/Models/Settings.cs`

- [ ] **Step 1: Add the 5 new properties to Settings**

In `Settings.cs`, after the `ThemeMode` property (line 48), add:

```csharp
public int HysteresisGap { get; set; } = 10;
public int TrendWindowSize { get; set; } = 10;
public int PredictiveLeadSeconds { get; set; } = 15;
public int AccessedBitsDelayMs { get; set; } = 2000;
public bool EffectivenessTrackingEnabled { get; set; } = true;
```

- [ ] **Step 2: Add validation for the new settings**

In `Settings.Validate()`, after `ScheduledOptimizeIntervalMinutes` clamping (line 102), add:

```csharp
HysteresisGap = Math.Clamp(HysteresisGap, 5, 30);
TrendWindowSize = Math.Clamp(TrendWindowSize, 5, 30);
PredictiveLeadSeconds = Math.Clamp(PredictiveLeadSeconds, 5, 60);
AccessedBitsDelayMs = Math.Clamp(AccessedBitsDelayMs, 500, 5000);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/RAMSpeed/Models/Settings.cs
git commit -m "feat: add settings for algorithmic improvements"
```

---

### Task 2: Restructure OptimizeAll for adaptive escalation

**Files:**
- Modify: `src/RAMSpeed/Models/OptimizationResult.cs`
- Modify: `src/RAMSpeed/Services/MemoryOptimizer.cs`
- Modify: `src/RAMSpeed/Services/MemoryMonitor.cs`

- [ ] **Step 1: Add ActualLevelUsed to OptimizationResult**

In `OptimizationResult.cs`, add after line 12 (`public string? ErrorMessage`):

```csharp
public OptimizationLevel ActualLevelUsed { get; set; }
```

Update `Summary` property to include the actual level:

```csharp
public string Summary => Success
    ? $"Freed {FreedMB:F1} MB in {Duration.TotalMilliseconds:F0}ms ({ProcessesTrimmed} processes trimmed, {ActualLevelUsed})"
    : $"Failed: {ErrorMessage}";
```

- [ ] **Step 2: Add GetAvailablePhysicalBytesQuick helper to MemoryOptimizer**

In `MemoryOptimizer.cs`, add as a private static method before `Dispose()`:

```csharp
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
```

- [ ] **Step 3: Change OptimizeAll signature and implement adaptive escalation**

Replace the entire `OptimizeAll` method in `MemoryOptimizer.cs` with:

```csharp
public OptimizationResult OptimizeAll(
    OptimizationLevel level = OptimizationLevel.Balanced,
    int cacheMaxPercent = 0,
    int targetThresholdPercent = 0,
    bool isLowMemory = false)
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
        // Step 1: Trim process working sets (all levels)
        var (trimmed, _, _, _) = TrimProcessWorkingSets();
        processesTrimmed = trimmed;
        methodsUsed.Add("Working Set Trim");

        // Adaptive escalation: check if we can stop early
        if (targetThresholdPercent > 0 && GetUsagePercentQuick() < targetThresholdPercent)
        {
            // Conservative was enough
            return BuildResult(sw, methodsUsed, beforeAvailable, processesTrimmed, actualLevel);
        }

        if (level >= OptimizationLevel.Balanced)
        {
            actualLevel = OptimizationLevel.Balanced;

            // Step 2: Flush modified page list
            if (FlushModifiedList())
                methodsUsed.Add("Modified List Flush");

            // Step 3: Two-phase — reset accessed bits so untouched pages become trim candidates
            if (CaptureAndResetAccessedBits())
                methodsUsed.Add("Access Bits Reset");

            // Step 4: Purge standby list (balanced = low-priority only)
            if (level == OptimizationLevel.Balanced || level == OptimizationLevel.Aggressive)
            {
                if (PurgeLowPriorityStandby())
                    methodsUsed.Add("Low-Priority Standby Purge");
            }

            // Step 5: Flush system file cache
            if (FlushSystemFileCache())
                methodsUsed.Add("File Cache Flush");

            // Step 6: Flush registry cache (dirty hives -> disk)
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
```

- [ ] **Step 4: Update MemoryMonitor.RunOptimization to pass new parameters**

In `MemoryMonitor.cs`, change line 104 from:

```csharp
var result = _optimizer.OptimizeAll(levelOverride ?? Level, CacheMaxPercent);
```

to:

```csharp
var result = _optimizer.OptimizeAll(levelOverride ?? Level, CacheMaxPercent, ThresholdPercent, IsLowMemory);
```

- [ ] **Step 5: Update TrimProcessWorkingSets return type for compatibility**

The new `OptimizeAll` destructures with 4 elements. Update `TrimProcessWorkingSets` to return 4 elements (the 4th — `earlyExit` — defaults to `false` for now; Task 5 will wire it up):

In `MemoryOptimizer.cs`, change the signature and return type:

```csharp
public (int trimmed, int failed, int skipped, bool earlyExit) TrimProcessWorkingSets(long targetAvailableBytes = 0)
```

Change the return statement at the end to:

```csharp
return (trimmed, failed, skipped, false);
```

- [ ] **Step 6: Build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: Build succeeded, all tests pass

- [ ] **Step 7: Commit**

```bash
git add src/RAMSpeed/Models/OptimizationResult.cs src/RAMSpeed/Services/MemoryOptimizer.cs src/RAMSpeed/Services/MemoryMonitor.cs
git commit -m "feat: implement adaptive escalation in optimization pipeline

Restructures OptimizeAll to start Conservative and escalate to
Balanced/Aggressive only if memory is still above threshold.
Adds ActualLevelUsed to OptimizationResult."
```

- [ ] **Step 8: eARA log — Layer 1**

Create `docs/superpowers/specs/results.tsv` with header and first entry:

```
commit	layer	status	description
<hash>	1	keep	adaptive escalation: OptimizeAll restructured with per-tier early exit
```

---

## Layer 2: Independent Improvements (parallel-safe)

### Task 3: Hysteresis band

**Files:**
- Modify: `src/RAMSpeed/Services/MemoryMonitor.cs`
- Create: `tests/RAMSpeed.Tests/HysteresisTests.cs`

- [ ] **Step 1: Write failing tests for hysteresis**

Create `tests/RAMSpeed.Tests/HysteresisTests.cs`.

Note: `MemoryMonitor` has a parameterless constructor that calls P/Invoke and creates a `DispatcherTimer` (requires WPF dispatcher). Tests MUST use the `internal` constructor to avoid these dependencies. The test project already has `InternalsVisibleTo` access via the project reference.

```csharp
using RAMSpeed.Services;

namespace RAMSpeed.Tests;

public class HysteresisTests
{
    [Fact]
    public void HysteresisGap_property_exists_with_default()
    {
        var monitor = CreateTestMonitor();
        monitor.HysteresisGap = 10;
        Assert.Equal(10, monitor.HysteresisGap);
    }

    /// <summary>
    /// Creates a MemoryMonitor using the internal constructor with injected
    /// dependencies to avoid P/Invoke and WPF dispatcher requirements.
    /// </summary>
    private static MemoryMonitor CreateTestMonitor()
    {
        // Use the internal constructor: MemoryMonitor(MemoryInfoService, MemoryOptimizer, DispatcherTimer?, Func<DateTime>?)
        // Pass null for timer (tests don't need ticking) and a fixed clock
        return new MemoryMonitor(
            new MemoryInfoService(),
            new MemoryOptimizer(),
            timer: null,
            utcNow: () => DateTime.UtcNow);
    }
}
```

Run: `dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj --filter HysteresisTests`
Expected: Fails because `HysteresisGap` property does not exist on `MemoryMonitor`

- [ ] **Step 2: Add hysteresis fields and property to MemoryMonitor**

In `MemoryMonitor.cs`, add fields after line 17 (`private bool _disposed;`):

```csharp
private bool _armed = true;
```

Add public property after `ScheduledOptimizeIntervalMinutes` (line 33):

```csharp
public int HysteresisGap { get; set; } = 10;
```

- [ ] **Step 3: Implement hysteresis logic in OnTimerTick**

In `MemoryMonitor.cs`, replace the auto-optimize section at the end of `OnTimerTick` (lines 152-163):

```csharp
if (!AutoOptimizeEnabled || LastMemoryInfo == null)
    return;

// Check cooldown
if ((DateTime.Now - _lastOptimization).TotalSeconds < CooldownSeconds)
    return;

// Hysteresis: re-arm when usage drops below threshold minus gap
if (!_armed && LastMemoryInfo.UsagePercent < (ThresholdPercent - HysteresisGap))
    _armed = true;

// Auto-optimize: OS low-memory always fires; threshold requires armed state
if (IsLowMemory || (_armed && LastMemoryInfo.UsagePercent >= ThresholdPercent))
{
    RunOptimization();
    _armed = false;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/RAMSpeed/Services/MemoryMonitor.cs tests/RAMSpeed.Tests/HysteresisTests.cs
git commit -m "feat: add hysteresis band to prevent optimization thrashing

Optimization trigger disarms after firing, re-arms only when usage
drops below ThresholdPercent - HysteresisGap (default 10%). OS
low-memory signal bypasses hysteresis."
```

---

### Task 4: Rate-of-change detection

**Files:**
- Modify: `src/RAMSpeed/Services/MemoryMonitor.cs`
- Modify: `tests/RAMSpeed.Tests/HysteresisTests.cs`

- [ ] **Step 0: Add unit tests for trend calculation to HysteresisTests.cs**

Add to `tests/RAMSpeed.Tests/HysteresisTests.cs`:

```csharp
[Fact]
public void TrendWindowSize_property_exists_with_default()
{
    var monitor = CreateTestMonitor();
    monitor.TrendWindowSize = 10;
    Assert.Equal(10, monitor.TrendWindowSize);
}

[Fact]
public void PredictiveLeadSeconds_property_exists_with_default()
{
    var monitor = CreateTestMonitor();
    monitor.PredictiveLeadSeconds = 15;
    Assert.Equal(15, monitor.PredictiveLeadSeconds);
}
```

Run: `dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj --filter HysteresisTests`
Expected: Fails because `TrendWindowSize` and `PredictiveLeadSeconds` properties do not exist

- [ ] **Step 1: Add rate-of-change fields and properties to MemoryMonitor**

In `MemoryMonitor.cs`, add after the `_armed` field:

```csharp
private readonly Queue<(DateTime time, double usage)> _usageHistory = new();
```

Add public properties after `HysteresisGap`:

```csharp
public int TrendWindowSize { get; set; } = 10;
public int PredictiveLeadSeconds { get; set; } = 15;
```

- [ ] **Step 2: Add trend calculation method**

Add a private method to `MemoryMonitor`:

```csharp
/// <summary>
/// Compute usage-percent-per-second slope via least-squares linear regression.
/// Returns 0 if insufficient data.
/// </summary>
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
```

- [ ] **Step 3: Wire rate-of-change into OnTimerTick**

In `OnTimerTick`, after `RefreshMemoryInfo()` and before `MaybeTrimSelf`, add the history tracking:

```csharp
// Track usage history for rate-of-change detection
if (LastMemoryInfo != null)
{
    _usageHistory.Enqueue((DateTime.UtcNow, LastMemoryInfo.UsagePercent));
    while (_usageHistory.Count > TrendWindowSize)
        _usageHistory.Dequeue();
}
```

In the auto-optimize section, after the hysteresis re-arm check and before the trigger condition, add predictive trigger:

```csharp
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
```

- [ ] **Step 4: Build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/RAMSpeed/Services/MemoryMonitor.cs tests/RAMSpeed.Tests/HysteresisTests.cs
git commit -m "feat: add rate-of-change detection for predictive optimization

Maintains a sliding window of memory usage readings and computes
linear regression slope. Triggers optimization early when the trend
predicts threshold breach within PredictiveLeadSeconds."
```

---

### Task 5: Sorted process trimming with early exit

**Files:**
- Modify: `src/RAMSpeed/Services/MemoryOptimizer.cs`

- [ ] **Step 1: Rewrite TrimProcessWorkingSets with sorting and early exit**

Replace the entire `TrimProcessWorkingSets` method in `MemoryOptimizer.cs`:

```csharp
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
```

- [ ] **Step 2: Wire targetAvailableBytes in OptimizeAll**

In the `OptimizeAll` method, replace the `TrimProcessWorkingSets` call. Before the call, compute the target:

```csharp
// Compute early-exit target for sorted trimming
long trimTarget = 0;
if (targetThresholdPercent > 0)
{
    trimTarget = (long)(beforeInfo.TotalPhysicalBytes * (1.0 - targetThresholdPercent / 100.0));
}

var (trimmed, _, _, earlyExit) = TrimProcessWorkingSets(trimTarget);
processesTrimmed = trimmed;
methodsUsed.Add(earlyExit ? "Working Set Trim (early exit)" : "Working Set Trim");
```

- [ ] **Step 3: Build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/RAMSpeed/Services/MemoryOptimizer.cs
git commit -m "feat: sort process trimming by working set size, add early exit

Trims biggest memory consumers first. Exits early when available
memory crosses the target threshold, avoiding unnecessary work on
small processes."
```

---

### Task 5b: Layer 2 build gate (eARA checkpoint)

- [ ] **Step 1: Full build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: Build succeeded, all tests pass

- [ ] **Step 2: Log Layer 2 to results.tsv**

Append to `docs/superpowers/specs/results.tsv`:

```
<hash>	2	keep	hysteresis band, rate-of-change detection, sorted trimming with early exit
```

---

## Layer 3: Dependent Improvements

### Task 6: Two-pass accessed bits

**Files:**
- Modify: `src/RAMSpeed/Services/MemoryOptimizer.cs`

- [ ] **Step 1: Reorder accessed bits and add delay in OptimizeAll**

First, add `using System.Threading;` to the top of `MemoryOptimizer.cs` (needed for `Thread.Sleep`).

In `OptimizeAll`, within the `if (level >= OptimizationLevel.Balanced)` block, reorder so accessed bits reset comes first, then modified list flush, then the delay:

```csharp
if (level >= OptimizationLevel.Balanced)
{
    actualLevel = OptimizationLevel.Balanced;

    // Step 2: Reset accessed bits FIRST (two-pass technique: mark pages as unvisited)
    if (CaptureAndResetAccessedBits())
        methodsUsed.Add("Access Bits Reset");

    // Step 3: Flush modified page list (useful work during the delay)
    if (FlushModifiedList())
        methodsUsed.Add("Modified List Flush");

    // Step 4: Delay to let active pages get re-accessed (two-pass window)
    Thread.Sleep(AccessedBitsDelayMs);

    // Step 5: Purge standby list — pages not re-accessed during delay are truly idle
    // ... rest of balanced steps
```

- [ ] **Step 2: Add AccessedBitsDelayMs property to MemoryOptimizer or pass it through**

The simplest approach: pass it as a parameter to `OptimizeAll`. Update signature:

```csharp
public OptimizationResult OptimizeAll(
    OptimizationLevel level = OptimizationLevel.Balanced,
    int cacheMaxPercent = 0,
    int targetThresholdPercent = 0,
    bool isLowMemory = false,
    int accessedBitsDelayMs = 2000)
```

Update the caller in `MemoryMonitor.RunOptimization`:

```csharp
var result = _optimizer.OptimizeAll(
    levelOverride ?? Level, CacheMaxPercent, ThresholdPercent, IsLowMemory, AccessedBitsDelayMs);
```

Add the property to `MemoryMonitor`:

```csharp
public int AccessedBitsDelayMs { get; set; } = 2000;
```

- [ ] **Step 3: Build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/RAMSpeed/Services/MemoryOptimizer.cs src/RAMSpeed/Services/MemoryMonitor.cs
git commit -m "feat: two-pass accessed bits with configurable delay

Resets accessed bits early, flushes modified list during a delay
window, then purges standby. Pages not re-accessed during the delay
are truly idle, making the purge more precise."
```

---

### Task 7: Effectiveness tracking

**Files:**
- Create: `src/RAMSpeed/Services/StepEffectivenessTracker.cs`
- Create: `tests/RAMSpeed.Tests/StepEffectivenessTrackerTests.cs`
- Modify: `src/RAMSpeed/Services/MemoryOptimizer.cs`

- [ ] **Step 1: Write failing tests for StepEffectivenessTracker**

Create `tests/RAMSpeed.Tests/StepEffectivenessTrackerTests.cs`:

```csharp
using RAMSpeed.Services;

namespace RAMSpeed.Tests;

public class StepEffectivenessTrackerTests
{
    [Fact]
    public void ShouldSkip_returns_false_with_no_history()
    {
        var tracker = new StepEffectivenessTracker();
        Assert.False(tracker.ShouldSkip("test-step"));
    }

    [Fact]
    public void ShouldSkip_returns_true_when_average_below_threshold()
    {
        var tracker = new StepEffectivenessTracker(windowSize: 3, minEffectiveBytes: 1_048_576);

        tracker.Record("test-step", 100);
        tracker.Record("test-step", 200);
        tracker.Record("test-step", 300);

        Assert.True(tracker.ShouldSkip("test-step"));
    }

    [Fact]
    public void ShouldSkip_returns_false_when_average_above_threshold()
    {
        var tracker = new StepEffectivenessTracker(windowSize: 3, minEffectiveBytes: 1_048_576);

        tracker.Record("test-step", 2_000_000);
        tracker.Record("test-step", 1_500_000);
        tracker.Record("test-step", 3_000_000);

        Assert.False(tracker.ShouldSkip("test-step"));
    }

    [Fact]
    public void Window_evicts_old_entries()
    {
        var tracker = new StepEffectivenessTracker(windowSize: 2, minEffectiveBytes: 1_048_576);

        tracker.Record("test-step", 100);       // will be evicted
        tracker.Record("test-step", 5_000_000);
        tracker.Record("test-step", 5_000_000);

        Assert.False(tracker.ShouldSkip("test-step"));
    }

    [Fact]
    public void Reset_clears_all_history()
    {
        var tracker = new StepEffectivenessTracker(windowSize: 3, minEffectiveBytes: 1_048_576);

        tracker.Record("test-step", 100);
        tracker.Record("test-step", 100);
        tracker.Record("test-step", 100);
        Assert.True(tracker.ShouldSkip("test-step"));

        tracker.Reset();
        Assert.False(tracker.ShouldSkip("test-step"));
    }

    [Fact]
    public void Negative_freed_bytes_treated_as_zero()
    {
        var tracker = new StepEffectivenessTracker(windowSize: 3, minEffectiveBytes: 1_048_576);

        tracker.Record("test-step", -500_000);
        tracker.Record("test-step", 2_000_000);
        tracker.Record("test-step", 2_000_000);

        // Average = (0 + 2M + 2M) / 3 = 1.33M > 1M threshold
        Assert.False(tracker.ShouldSkip("test-step"));
    }
}
```

Run: `dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj --filter StepEffectivenessTrackerTests`
Expected: Fails because `StepEffectivenessTracker` class does not exist

- [ ] **Step 2: Implement StepEffectivenessTracker**

Create `src/RAMSpeed/Services/StepEffectivenessTracker.cs`:

```csharp
namespace RAMSpeed.Services;

internal class StepEffectivenessTracker
{
    private readonly Dictionary<string, Queue<long>> _history = new();
    private readonly int _windowSize;
    private readonly long _minEffectiveBytes;
    private DateTime _lastResetUtc = DateTime.UtcNow;
    private static readonly TimeSpan ResetInterval = TimeSpan.FromMinutes(30);

    public StepEffectivenessTracker(int windowSize = 10, long minEffectiveBytes = 1_048_576)
    {
        _windowSize = windowSize;
        _minEffectiveBytes = minEffectiveBytes;
    }

    public void Record(string stepName, long freedBytes)
    {
        MaybeAutoReset();

        if (!_history.TryGetValue(stepName, out var queue))
        {
            queue = new Queue<long>();
            _history[stepName] = queue;
        }

        queue.Enqueue(Math.Max(0, freedBytes));

        while (queue.Count > _windowSize)
            queue.Dequeue();
    }

    public bool ShouldSkip(string stepName)
    {
        MaybeAutoReset();

        if (!_history.TryGetValue(stepName, out var queue) || queue.Count < _windowSize)
            return false;

        var average = queue.Average();
        return average < _minEffectiveBytes;
    }

    public void Reset()
    {
        _history.Clear();
        _lastResetUtc = DateTime.UtcNow;
    }

    private void MaybeAutoReset()
    {
        if (DateTime.UtcNow - _lastResetUtc >= ResetInterval)
            Reset();
    }
}
```

- [ ] **Step 3: Run tests to verify all pass**

Run: `dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj --filter StepEffectivenessTrackerTests`
Expected: All 6 tests pass

- [ ] **Step 4: Wire tracker into MemoryOptimizer**

In `MemoryOptimizer.cs`, add the tracker field:

```csharp
private readonly StepEffectivenessTracker _tracker = new();
```

Add `effectivenessTrackingEnabled` parameter to `OptimizeAll`:

```csharp
public OptimizationResult OptimizeAll(
    OptimizationLevel level = OptimizationLevel.Balanced,
    int cacheMaxPercent = 0,
    int targetThresholdPercent = 0,
    bool isLowMemory = false,
    int accessedBitsDelayMs = 2000,
    bool effectivenessTrackingEnabled = true)
```

Add helper method for running a tracked step:

```csharp
private bool RunTrackedStep(string stepName, Func<bool> step, List<string> methodsUsed, bool trackingEnabled)
{
    if (trackingEnabled && _tracker.ShouldSkip(stepName))
    {
        methodsUsed.Add($"Skipped: {stepName} (ineffective)");
        return false;
    }

    ulong before = trackingEnabled ? GetAvailablePhysicalBytesQuick() : 0;
    bool success = step();
    if (trackingEnabled)
    {
        ulong after = GetAvailablePhysicalBytesQuick();
        _tracker.Record(stepName, (long)(after - before));
    }

    if (success)
        methodsUsed.Add(stepName);

    return success;
}
```

Then in `OptimizeAll`, replace direct step calls with tracked calls, e.g.:

```csharp
// Instead of:
// if (FlushModifiedList()) methodsUsed.Add("Modified List Flush");
// Use:
RunTrackedStep("Modified List Flush", FlushModifiedList, methodsUsed, effectivenessTrackingEnabled);
```

Apply this pattern to all steps in Balanced/Aggressive (except Working Set Trim which has its own tracking).

Update the caller in `MemoryMonitor.RunOptimization`:

```csharp
var result = _optimizer.OptimizeAll(
    levelOverride ?? Level, CacheMaxPercent, ThresholdPercent, IsLowMemory,
    AccessedBitsDelayMs, EffectivenessTrackingEnabled);
```

Add property to `MemoryMonitor`:

```csharp
public bool EffectivenessTrackingEnabled { get; set; } = true;
```

- [ ] **Step 5: Build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/RAMSpeed/Services/StepEffectivenessTracker.cs tests/RAMSpeed.Tests/StepEffectivenessTrackerTests.cs src/RAMSpeed/Services/MemoryOptimizer.cs src/RAMSpeed/Services/MemoryMonitor.cs
git commit -m "feat: add per-step effectiveness tracking

Tracks how much memory each optimization step frees. Skips steps
that consistently free less than 1 MB. Resets every 30 minutes to
adapt to changing workloads."
```

---

### Task 8: Compressed memory awareness

**Files:**
- Modify: `src/RAMSpeed/Services/MemoryOptimizer.cs`

- [ ] **Step 1: Add compressed memory awareness to adaptive escalation**

In `OptimizeAll`, after the Conservative steps and the first adaptive escalation check, before `if (level >= OptimizationLevel.Balanced)`, add:

```csharp
// Compressed memory awareness: adjust effective level based on OS compression activity
var effectiveLevel = level;
if (targetThresholdPercent > 0)
{
    var currentInfo = _memoryInfo.GetCurrentMemoryInfo();
    double compressedRatio = currentInfo.TotalPhysicalBytes > 0
        ? (double)currentInfo.CompressedBytes / currentInfo.TotalPhysicalBytes
        : 0;

    if (compressedRatio > 0.15 && effectiveLevel == OptimizationLevel.Aggressive)
    {
        effectiveLevel = OptimizationLevel.Balanced;
        methodsUsed.Add("Level capped (high compression)");
    }
    else if (compressedRatio < 0.05 && isLowMemory && effectiveLevel < OptimizationLevel.Balanced)
    {
        effectiveLevel = OptimizationLevel.Balanced;
        methodsUsed.Add("Level raised (low compression + low memory)");
    }
}
```

Then use `effectiveLevel` instead of `level` for the Balanced/Aggressive gates that follow.

- [ ] **Step 2: Build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: All pass

- [ ] **Step 3: Commit**

```bash
git add src/RAMSpeed/Services/MemoryOptimizer.cs
git commit -m "feat: add compressed memory awareness to escalation decisions

When OS compression is active (>15% of RAM), caps level at Balanced
to avoid fighting the OS. When compression is low (<5%) and memory
is critically low, ensures at least Balanced optimization."
```

---

### Task 9: Layer 3 build gate + settings wiring (eARA checkpoint)

- [ ] **Step 1: Wire all new settings from Settings.cs to MemoryMonitor at startup**

**Scope note:** Per the spec's "No UI changes" constraint, these settings are **load-only from JSON** — they do not get ViewModel properties with two-way binding. Users change them by editing `%APPDATA%\RAMSpeed\settings.json` directly. This is intentional: the existing UI-bound settings (ThresholdPercent, Level, etc.) have ViewModel properties; these new algorithmic tuning settings do not.

In `MainViewModel.cs`, find where existing settings are applied to the monitor (search for `Monitor.ThresholdPercent = _settings.ThresholdPercent`). Add alongside:

```csharp
Monitor.HysteresisGap = _settings.HysteresisGap;
Monitor.TrendWindowSize = _settings.TrendWindowSize;
Monitor.PredictiveLeadSeconds = _settings.PredictiveLeadSeconds;
Monitor.AccessedBitsDelayMs = _settings.AccessedBitsDelayMs;
Monitor.EffectivenessTrackingEnabled = _settings.EffectivenessTrackingEnabled;
```

- [ ] **Step 2: Full build and test**

Run: `dotnet build src/RAMSpeed/RAMSpeed.csproj && dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
Expected: Build succeeded, all tests pass

- [ ] **Step 3: Log Layer 3 to results.tsv**

Append to `docs/superpowers/specs/results.tsv`:

```
<hash>	3	keep	two-pass accessed bits, effectiveness tracking, compressed memory awareness
```

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "feat: wire new algorithmic settings to monitor

Connects HysteresisGap, TrendWindowSize, PredictiveLeadSeconds,
AccessedBitsDelayMs, and EffectivenessTrackingEnabled from Settings
to MemoryMonitor."
```

---

## Verification Gate

After all tasks complete:

- [ ] `dotnet build src/RAMSpeed/RAMSpeed.csproj` — clean build, no warnings
- [ ] `dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj` — all tests pass
- [ ] `results.tsv` has entries for all 3 layers
- [ ] All new settings default to values that preserve current behavior
- [ ] Git log shows clean, atomic commits per improvement
