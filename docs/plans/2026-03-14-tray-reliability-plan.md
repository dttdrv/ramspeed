# RAMSpeed Tray Reliability Implementation Plan
> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
**Goal:** Fix tray restore and tooltip reliability regressions without broad architectural churn, while documenting the deeper risks discovered during review.
**Architecture:** Replace title-based second-instance activation with a named activation signal listened to by the primary instance, and drive tray tooltip updates from the raw `MemoryInfo` snapshot instead of rounded view-model values. Add focused unit tests around the new seams so the fix is regression-resistant.
**Tech Stack:** .NET 8 WPF, Windows Forms `NotifyIcon`, xUnit
---

### Task 1: Add Regression Test Coverage
**Files:**
- Modify: `tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj`
- Modify: `src/RAMSpeed/RAMSpeed.csproj`
- Create: `tests/RAMSpeed.Tests/TrayTooltipFormatterTests.cs`
- Create: `tests/RAMSpeed.Tests/SingleInstanceActivationServiceTests.cs`

**Step 1: Write the failing tests**
- Add a tooltip-format test that asserts the tray text is built from a raw `MemoryInfo` snapshot and respects the Win32 text limit.
- Add an activation-signal test that asserts a listening primary instance receives a restore signal from a second caller.

**Step 2: Run tests to verify they fail**
Run: `dotnet test tests\RAMSpeed.Tests\RAMSpeed.Tests.csproj`
Expected: FAIL because the formatter and activation service do not exist yet.

**Step 3: Write minimal implementation**
- Add only the helper/service types needed for the tests.

**Step 4: Run tests to verify they pass**
Run: `dotnet test tests\RAMSpeed.Tests\RAMSpeed.Tests.csproj`
Expected: PASS for the new regression tests.

### Task 2: Fix Tray Activation and Tooltip Wiring
**Files:**
- Create: `src/RAMSpeed/Services/SingleInstanceActivationService.cs`
- Modify: `src/RAMSpeed/App.xaml.cs`
- Modify: `src/RAMSpeed/MainWindow.xaml.cs`
- Modify: `src/RAMSpeed/ViewModels/MainViewModel.cs`
- Modify: `src/RAMSpeed/Services/TrayIconService.cs`

**Step 1: Route raw memory snapshots to the tray**
- Expose a `MemoryInfoUpdated` event and latest snapshot from `MainViewModel`.
- Update the tray tooltip directly from that snapshot instead of reconstructing from rounded UI values.

**Step 2: Replace brittle second-instance restore**
- Remove title-based `FindWindow` activation.
- Add a named activation signal that the primary instance listens to and maps to the existing `RestoreWindow` path.

**Step 3: Harden restore behavior**
- Make restore idempotent for visible, hidden, or minimized windows and force taskbar/activation state back into a normal interactive state.

**Step 4: Re-run focused tests**
Run: `dotnet test tests\RAMSpeed.Tests\RAMSpeed.Tests.csproj`
Expected: PASS.

### Task 3: Verify, Scan, and Record State
**Files:**
- Create: `PROJECT_BRIEF.md`
- Create: `STATE.yaml`
- Create: `LOG.md`

**Step 1: Verify application build**
Run: `dotnet build src\RAMSpeed\RAMSpeed.csproj`
Expected: PASS.

**Step 2: Run security/dependency scan**
Run: Trivy filesystem scan against the repo root.
Expected: results captured and summarized, with blockers called out if scanning fails.

**Step 3: Update project decision/state records**
- Record the tray fix scope, root causes, open risks, and next-priority remediation items.
