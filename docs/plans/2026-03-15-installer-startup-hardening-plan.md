# RAMSpeed Installer And Startup Hardening Implementation Plan
> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
**Goal:** Fix the remaining install/startup defects so both self-install and setup-based install deploy a runnable app payload and use one elevation-safe startup path.
**Architecture:** Add a small installer payload copier that mirrors the current application directory into the install directory, update the Inno Setup script to install the full `publish` payload, and make `TaskSchedulerHelper` the single source of truth for startup registration. Preserve one scheduled task for silent elevated launch and toggle only the optional logon trigger so disabling autostart does not throw the app back onto the UAC-only path.
**Tech Stack:** .NET 8 WPF, xUnit, Windows Task Scheduler, Inno Setup
---

### Task 1: Add failing regression tests
**Files:**
- Create: `tests/RAMSpeed.Tests/InstallerPayloadCopierTests.cs`
- Create: `tests/RAMSpeed.Tests/TaskSchedulerHelperTests.cs`

**Step 1: Write the failing test**
- Add a file-system test that proves installer payload copying must copy the full app directory, not just `RAMSpeed.exe`.
- Add a task-definition test that proves the scheduled task XML includes an `ONLOGON` trigger and highest-available run level.

**Step 2: Run test to verify it fails**
Run: `dotnet test tests\RAMSpeed.Tests\RAMSpeed.Tests.csproj`
Expected: FAIL because the copier and XML builder do not exist yet.

### Task 2: Implement minimal install/startup fixes
**Files:**
- Create: `src/RAMSpeed/Services/InstallerPayloadCopier.cs`
- Modify: `src/RAMSpeed/Services/InstallerService.cs`
- Modify: `src/RAMSpeed/Services/TaskSchedulerHelper.cs`
- Modify: `src/RAMSpeed/ViewModels/MainViewModel.cs`
- Modify: `installer/RAMSpeed.iss`

**Step 1: Copy the full payload**
- Replace the self-install single-file copy with a recursive copy of the current app payload.

**Step 2: Unify startup registration**
- Make `TaskSchedulerHelper.CreateTask` define one on-demand elevation task and add the logon trigger only when startup is enabled.
- Route the `StartWithWindows` toggle through `TaskSchedulerHelper` instead of the Run key.
- Stop the setup script from creating a separate Run-key startup path and register the same scheduled-task model through the app.

**Step 3: Run tests to verify the fix**
Run: `dotnet test tests\RAMSpeed.Tests\RAMSpeed.Tests.csproj`
Expected: PASS.

### Task 3: Verify and record state
**Files:**
- Modify: `PROJECT_BRIEF.md`
- Modify: `STATE.yaml`
- Modify: `LOG.md`

**Step 1: Build**
Run: `dotnet build src\RAMSpeed\RAMSpeed.csproj --no-restore /nr:false -v minimal`
Expected: PASS.

**Step 2: Security scan**
Run: Trivy filesystem scan against the repo root.
Expected: results captured and summarized.

**Step 3: Update state docs**
- Record the installer/startup fixes and the remaining follow-up risks.
