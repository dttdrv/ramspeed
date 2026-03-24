# RAMSpeed Algorithmic Improvements — Design Spec

## Summary

Seven algorithmic improvements to RAMSpeed's memory optimization pipeline, implemented using an eARA-adapted development loop (implement → pre-check → build → verify → keep/revert → log).

All changes are in the Services layer. No UI changes. No new project files beyond this spec and the results log.

## Files Modified

| File | Changes |
|---|---|
| `src/RAMSpeed/Services/MemoryOptimizer.cs` | Adaptive escalation, sorted trimming + early exit, two-pass accessed bits, per-step measurement |
| `src/RAMSpeed/Services/MemoryMonitor.cs` | Hysteresis band, rate-of-change detection, compressed memory awareness |
| `src/RAMSpeed/Models/Settings.cs` | New settings: HysteresisGap, TrendWindowSize, PredictiveLeadSeconds, AccessedBitsDelayMs, EffectivenessTrackingEnabled |
| `src/RAMSpeed/Services/StepEffectivenessTracker.cs` | New file: per-step effectiveness history (improvement 6) |
| `src/RAMSpeed/Models/OptimizationResult.cs` | New field: ActualLevelUsed |

## Implementation Layers

### Layer 1: Adaptive Escalation (must land first)

**Improvement 1 — Adaptive Escalation**

Current: `OptimizeAll` runs all steps for a fixed level.
New: Runs Conservative first, measures, escalates to Balanced/Aggressive only if memory usage is still above threshold.

Signature change:
```csharp
public OptimizationResult OptimizeAll(
    OptimizationLevel level = OptimizationLevel.Balanced,
    int cacheMaxPercent = 0,
    int targetThresholdPercent = 0,
    bool isLowMemory = false)
```

**Fallback behavior when `targetThresholdPercent == 0`**: Adaptive escalation is disabled — the method runs the full requested level exactly as it does today. This preserves backward compatibility for any caller that does not supply the new parameters.

**When `targetThresholdPercent > 0`**: Adaptive escalation is active. The optimizer re-measures usage internally via `_memoryInfo.GetCurrentMemoryInfo()` after each tier and stops escalating once usage drops below the target.

**`isLowMemory` parameter**: Passed from `MemoryMonitor.IsLowMemory` by the caller. Used by compressed memory awareness (Improvement 7) to decide whether to escalate when compression is low.

Logic:
1. Run Conservative steps (working set trim).
2. If `targetThresholdPercent > 0`: re-measure via `_memoryInfo.GetCurrentMemoryInfo()`. If `UsagePercent < targetThresholdPercent`, stop.
3. Apply compressed memory awareness modifier (Improvement 7) to decide effective level cap.
4. If `level >= Balanced` (and not capped), run Balanced steps. Re-measure. If below threshold, stop.
5. If `level >= Aggressive` (and not capped), run remaining Aggressive steps.

`OptimizationResult.ActualLevelUsed` (`OptimizationLevel` enum) records the highest tier that actually ran.

Caller update — `MemoryMonitor.RunOptimization` passes state:
```csharp
_optimizer.OptimizeAll(levelOverride ?? Level, CacheMaxPercent, ThresholdPercent, IsLowMemory);
```

### Layer 2: Independent Improvements (parallel)

**Improvement 2 — Hysteresis Band**

Add to `MemoryMonitor`:
- `private bool _armed = true;`
- On tick: if `!_armed && usage < (ThresholdPercent - HysteresisGap)`, re-arm.
- Trigger condition becomes: `_armed && (IsLowMemory || usage >= ThresholdPercent)`.
- After optimization fires: `_armed = false`.
- OS low-memory signal (`IsLowMemory`) bypasses hysteresis — always fires regardless of arm state.
- New setting: `HysteresisGap` (default 10, clamped 5–30).

**Improvement 3 — Rate-of-Change Detection**

Add to `MemoryMonitor`:
- `private readonly Queue<(DateTime time, int usage)> _usageHistory;`
- Max size: `TrendWindowSize` (default 10).
- On each tick, enqueue current reading, dequeue if over size.
- Compute least-squares slope of usage vs. time.
- `predictedUsage = currentUsage + slope * PredictiveLeadSeconds`.
- If `_armed && predictedUsage >= ThresholdPercent`, trigger optimization early and disarm.
- New settings: `TrendWindowSize` (default 10, clamped 5–30), `PredictiveLeadSeconds` (default 15, clamped 5–60).

**Interaction with hysteresis and cooldown**: A rate-of-change trigger calls `RunOptimization()` which sets `_lastOptimization = DateTime.Now` (existing code, line 105 of `MemoryMonitor.cs`). This means the cooldown timer applies identically whether the trigger was threshold-based or predictive. The `_armed = false` disarm also applies, so re-arming requires usage to drop below `ThresholdPercent - HysteresisGap` before any subsequent trigger (threshold or predictive) can fire. The cooldown check in `OnTimerTick` runs before both trigger paths, so both are gated by cooldown.

Slope calculation — simple linear regression over the window:
```
slope = (N * sum(t*u) - sum(t) * sum(u)) / (N * sum(t^2) - sum(t)^2)
```
Where t is seconds since first sample, u is usage percent. The result is "usage percent change per second."

**Improvement 4 — Sorted Process Trimming + Early Exit**

Modify `TrimProcessWorkingSets`:
- Signature adds optional `targetAvailableBytes` parameter (0 = no early exit).
- Collect all processes into lightweight structs: `(int pid, string name, long workingSet)`. Read `Process.WorkingSet64` and `ProcessName` with try/catch (access denied → skip), then immediately `Dispose()` the `Process` object. This avoids holding all process handles open during sort.
- Sort the struct list descending by `workingSet`.
- Iterate sorted list. Open handles by PID via `NativeMethods.OpenProcess` (same as current code). After every 10 successful trims, do a lightweight available-memory check using `NativeMethods.GlobalMemoryStatusEx` directly (not `MemoryInfoService`, which also reads perf counters). If available bytes now exceed `targetAvailableBytes`, break.
- Return extended tuple: `(trimmed, failed, skipped, earlyExit)`.
- **Caller update in `OptimizeAll`**: Destructure as `var (trimmed, _, _, earlyExit) = TrimProcessWorkingSets(targetAvailableBytes)`. Derive `targetAvailableBytes` from threshold: `targetAvailableBytes = (long)(totalPhysicalBytes * (1.0 - targetThresholdPercent / 100.0))` when `targetThresholdPercent > 0`, else 0.

Add a private static helper for the lightweight check:
```csharp
private static ulong GetAvailablePhysicalBytesQuick()
{
    var ms = new NativeMethods.MEMORYSTATUSEX { dwLength = ... };
    NativeMethods.GlobalMemoryStatusEx(ref ms);
    return ms.ullAvailPhys;
}
```

### Layer 3: Dependent Improvements (after Layer 1 + 2)

Within Layer 3: Improvements 5 and 6 are independent of each other and can be implemented in parallel. Improvement 7 modifies the adaptive escalation logic from Improvement 1 and must be implemented after Improvement 1 is verified working (guaranteed since Layer 1 completes before Layer 3 begins).

**Improvement 5 — Two-Pass Accessed Bits**

In `OptimizeAll`, when running Balanced/Aggressive:
1. Run `CaptureAndResetAccessedBits` as the first Balanced step (before modified list flush).
2. Run `FlushModifiedList` (gives the delay useful work to overlap with).
3. `Thread.Sleep(AccessedBitsDelayMs)` — default 2000ms. Uses `Thread.Sleep` (not `Task.Delay`) to avoid async propagation through `OptimizeAll` and its callers.
4. Then proceed to standby purge.

Note: The sleep runs while `_optimizationInProgress > 0`, which means `MaybeTrimSelf(Periodic)` will correctly skip during this window. The cooldown timer effectively extends by the delay duration — this is acceptable since the delay is part of the optimization.

New setting: `AccessedBitsDelayMs` (default 2000, clamped 500–5000).

**Improvement 6 — Effectiveness Tracking**

New class `StepEffectivenessTracker`:
```csharp
internal class StepEffectivenessTracker
{
    private readonly Dictionary<string, Queue<long>> _history = new();
    private readonly int _windowSize;
    private readonly long _minEffectiveBytes;

    public StepEffectivenessTracker(int windowSize = 10, long minEffectiveBytes = 1_048_576) // 1 MB

    public void Record(string stepName, long freedBytes)
    public bool ShouldSkip(string stepName)  // returns true if average freed < minEffectiveBytes
}
```

In `OptimizeAll`, wrap each step using the lightweight helper from Improvement 4:
```csharp
var beforeAvail = GetAvailablePhysicalBytesQuick();
RunStep();
var afterAvail = GetAvailablePhysicalBytesQuick();
tracker.Record(stepName, (long)(afterAvail - beforeAvail));
```

Before running a step, check `tracker.ShouldSkip(stepName)`. If true, skip and log "Skipped: {name} (ineffective)".

The tracker is owned by `MemoryOptimizer`, non-persistent (resets on restart). Controlled by `EffectivenessTrackingEnabled` setting (default true).

**Staleness prevention**: The tracker resets its history for all steps every 30 minutes (`_lastResetUtc` field). This prevents steps from being permanently skipped when workload patterns change during a long session.

**Improvement 7 — Compressed Memory Awareness**

In `OptimizeAll`, after Conservative steps and before escalating to Balanced:
- Read `CompressedBytes` from the memory info snapshot (already available from the re-measurement call).
- Compute `compressedRatio = (double)compressedBytes / totalPhysicalBytes`.
- If `compressedRatio > 0.15` and effective level would be Aggressive → cap at Balanced.
- If `compressedRatio < 0.05` and `isLowMemory` parameter is true → ensure at least Balanced.

`isLowMemory` is the new parameter added to `OptimizeAll` in Improvement 1, passed from `MemoryMonitor.IsLowMemory`.

This modifies the adaptive escalation decision, not a separate pipeline step. It only applies when adaptive escalation is active (`targetThresholdPercent > 0`).

## Settings Additions

```csharp
// In Settings.cs
public int HysteresisGap { get; set; } = 10;                    // 5-30
public int TrendWindowSize { get; set; } = 10;                   // 5-30
public int PredictiveLeadSeconds { get; set; } = 15;             // 5-60
public int AccessedBitsDelayMs { get; set; } = 2000;             // 500-5000
public bool EffectivenessTrackingEnabled { get; set; } = true;
```

Validation in `Validate()`:
```csharp
HysteresisGap = Math.Clamp(HysteresisGap, 5, 30);
TrendWindowSize = Math.Clamp(TrendWindowSize, 5, 30);
PredictiveLeadSeconds = Math.Clamp(PredictiveLeadSeconds, 5, 60);
AccessedBitsDelayMs = Math.Clamp(AccessedBitsDelayMs, 500, 5000);
```

## Backward Compatibility

All new settings have defaults that preserve current behavior:
- Hysteresis: `_armed` starts true, so first trigger works identically.
- Rate-of-change: only triggers early, never suppresses a threshold trigger.
- Sorted trimming: same processes trimmed, just in better order.
- Two-pass delay: adds 2s to optimization time, but optimization is already async from UI.
- Effectiveness tracking: starts with empty history, so all steps run initially.
- Compressed memory: only modifies escalation in adaptive mode, not the static level path.

## eARA Development Loop

### Experiment config (adapted)

- **Modified files**: `MemoryOptimizer.cs`, `MemoryMonitor.cs`, `Settings.cs`, plus new `StepEffectivenessTracker.cs`
- **Metric**: builds cleanly (pass/fail) + qualitative impact logged
- **Gates**: must compile, no new warnings, default settings preserve current behavior
- **Results log**: `docs/superpowers/specs/results.tsv`

### Per-layer loop

1. Implement the layer's improvements.
2. Pre-checks: describe change, review diff, build, subagent verification.
3. If build passes and gates pass → keep (commit).
4. If build fails → fix or revert.
5. Log to results.tsv.
6. Post-run: verify no regressions, check compressed output size.

### Results.tsv format

```
commit	layer	status	description
```

## Non-Goals

- No UI changes.
- No runtime benchmarking harness.
- No persistent effectiveness data across restarts.
- No changes to the native P/Invoke layer.
- No new NuGet dependencies.
