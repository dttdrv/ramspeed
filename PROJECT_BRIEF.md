# PROJECT_BRIEF

## Current Objective
Stabilize RAMSpeed's desktop lifecycle end-to-end: tray restore and reporting, install payload integrity, elevation-safe startup behavior, and runtime cleanup around self-trimming and owned resources.

## Scope For 2026-03-15
- Fix stale tray tooltip updates.
- Replace brittle restore behavior with a primary-instance activation signal.
- Add regression tests around the new activation and tooltip seams.
- Fix self-install and setup-based install so they deploy the full runnable payload.
- Unify "Start with Windows" around one scheduled-task path that preserves elevated launch behavior even when autostart is disabled.
- Reduce self-trim overhead by moving trim decisions to a throttled policy seam.
- Make the monitor/optimizer/info-service lifecycle deterministic so timers, handles, and performance counters are released on shutdown.
- Capture the broader defect inventory and improvement opportunities discovered during review.

## Key Findings
- The current second-instance restore path depends on `FindWindow` plus `ShowWindow(SW_RESTORE)`, which is a poor fit for a WPF window hidden with `Hide()`.
- Tray tooltip updates are coupled to `UsagePercent` property changes and are rebuilt from rounded UI values instead of the original memory snapshot.
- Manual optimization previously risked cross-thread UI updates; that path is now marshaled back to the dispatcher.
- Both install paths were shipping incomplete payloads.
- Startup registration was split between a Run-key path and a scheduled-task path, which broke elevation-safe autostart.
- The scheduled task now serves two roles: silent elevated launch on demand, and optional logon startup when enabled.
- Aggressive self-trimming has been reduced to startup/post-optimization collections plus a throttled periodic working-set trim.
- `MemoryInfoService`, `MemoryOptimizer`, and `MemoryMonitor` now dispose owned counters, timers, and native notification handles deterministically.
- Live portable/install smoke is still only partially verified on this machine because the current shell is non-admin and the workstation already contains stale installed RAMSpeed artifacts plus an elevated running instance.
