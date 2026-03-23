# LOG

## 2026-03-14
- Audited the repo and confirmed the tray issues live in `src/RAMSpeed`.
- Identified two primary tray root causes: title-based hidden-window reactivation and tooltip updates wired to rounded UI state instead of raw memory snapshots.
- Collected a broader defect inventory covering install packaging, startup elevation, thread affinity, self-trimming overhead, and incomplete resource cleanup.
- Began a narrow fix plan centered on a primary-instance activation signal plus regression tests.
- Added xUnit coverage for tray tooltip formatting and single-instance activation signaling.
- Replaced the title-only activation path with a named activation signal, while keeping a Win32 fallback plus user-visible warning if activation still fails.
- Routed tray tooltip updates from the raw `MemoryInfo` snapshot and marshaled memory updates back onto the WPF dispatcher to avoid background-thread UI mutation.
- Verified `dotnet test`, `dotnet build --no-restore /nr:false`, and a Trivy filesystem scan; no HIGH/CRITICAL scan findings were reported.

## 2026-03-15
- Audited the remaining install/startup defects and confirmed two root causes: incomplete app payload deployment and split startup registration between Run-key and scheduled-task mechanisms.
- Added xUnit coverage for full payload copying and scheduled-task XML generation.
- Introduced `InstallerPayloadCopier` and changed self-install to copy the full application directory instead of only `RAMSpeed.exe`.
- Updated `TaskSchedulerHelper.CreateTask` to define one task that always supports silent elevated launch and optionally adds the logon trigger when startup is enabled.
- Changed the runtime `StartWithWindows` toggle to update that scheduled task instead of writing the HKCU Run key.
- Added `--register-task` / `--start-at-logon` startup flags so the Inno Setup path can register the same scheduled-task model through the app itself.
- Updated `installer/RAMSpeed.iss` to install the full `publish` payload and removed the separate Run-key startup entry so setup uses the same scheduled-task model.
- Updated app startup so a non-admin launch tries to hand off to the scheduled task before falling back to the UAC prompt.
- Verified `dotnet test`, `dotnet build --no-restore /nr:false -v minimal`, and a Trivy filesystem scan; no HIGH/CRITICAL scan findings were reported.
- Added `MemoryInfoServiceTests` and `SelfTrimPolicyTests` to lock in disposal behavior and the new startup/periodic/post-optimization trim rules.
- Implemented `SelfTrimPolicy` plus `SelfTrimReason` so self-trimming is policy-driven instead of an unconditional per-tick side effect.
- Changed `MemoryOptimizer.TrimSelf(...)` to be reason-aware, removed the old double-aggressive GC path, and removed the unconditional optimizer-level `finally` trim.
- Made `MemoryInfoService`, `MemoryOptimizer`, and `MemoryMonitor` disposable; the monitor now stops/unhooks the dispatcher timer, closes memory notification handles, and cascades disposal to owned services.
- Moved startup trim ownership into `MemoryMonitor.Start()` and updated `MainViewModel.Shutdown()` to dispose the monitor instead of only stopping it.
- Broadened the UI setting range so `SelfWorkingSetCapMB = 0` can truly disable self-trimming, which the new policy treats as an off state.
- Verified the cleanup wave with `dotnet test tests\\RAMSpeed.Tests\\RAMSpeed.Tests.csproj` -> 15 passed and `dotnet build src\\RAMSpeed\\RAMSpeed.csproj --no-restore /nr:false -v minimal` -> passed.
- `trivy` MCP filesystem scans failed twice with a wrapper-level project-scan error, so the scan was rerun via the shell: `trivy fs --quiet --skip-version-check --severity HIGH,CRITICAL --scanners vuln,misconfig,secret,license --format json .`; parsed result counts were `vuln=0 misconfig=0 secret=0 license=0`.
- Captured portable-smoke preconditions from the current shell:
  - admin check -> `False`
  - `schtasks /Query /TN "RAMSpeed"` -> task not found
  - `Test-Path "C:\\Program Files\\RAMSpeed"` -> `True`
  - `Test-Path "$env:ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\RAMSpeed"` -> `True`
- Inspected the pre-existing install footprint and found it is stale from an earlier broken install: `C:\\Program Files\\RAMSpeed` currently contains only `app.ico`, `RAMSpeed.exe`, `unins000.dat`, and `unins000.exe`.
- Published a fresh portable payload with `dotnet publish src\\RAMSpeed\\RAMSpeed.csproj -c Release -r win-x64 --self-contained false -o publish` and verified the expected minimum files exist in `publish`: `RAMSpeed.exe`, `RAMSpeed.dll`, `RAMSpeed.deps.json`, and `RAMSpeed.runtimeconfig.json`.
- Attempted a live portable launch from `publish\\RAMSpeed.exe`, but a clean smoke pass could not continue because an elevated `RAMSpeed.exe` instance remained running and `Stop-Process` from this shell failed with `Access is denied`.
- Queried the live process after invoking `CloseMainWindow()` and observed that the process stayed resident instead of exiting, which is consistent with the minimize-to-tray close path.
- Relaunched `publish\\RAMSpeed.exe` while one `RAMSpeed` process was already running and observed `Before:1` / `After:1`, which is a useful live signal that the single-instance activation path is preventing a second process.
