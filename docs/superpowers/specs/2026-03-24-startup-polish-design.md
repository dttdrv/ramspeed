# RAMSpeed Startup Polish & Simplification — Design Spec

## Problem

The app still freezes on startup. The process list is overcomplicated (Working Set + Private Bytes). The app feels bloated. Root causes:

1. `RefreshProcessList()` runs at BackgroundPriority on startup — process enumeration (100-500ms) plus ObservableCollection batch update (50-100ms) blocks the UI thread
2. `GetCompressedMemoryBytes()` first call isn't cached — 50-150ms process enumeration on first memory poll
3. `Initialize()` runs on dispatcher BackgroundPriority — still the UI thread, just delayed
4. No visual loading state — user sees a frozen window with no feedback
5. Process view shows both Working Set and Private Bytes — unnecessary complexity

## Fixes

### Fix 1: Don't enumerate processes on startup

**Root cause:** `MainWindow.OnLoaded` calls `_vm.RefreshProcessList()` at dispatcher BackgroundPriority. This enumerates all processes on the UI thread.

**Fix:** Remove the `RefreshProcessList()` call from `OnLoaded`. Only populate the process list when the user actually navigates to the process tab/section. Lazy load.

In `MainWindow.xaml.cs` `OnLoaded` dispatcher block: remove `_vm.RefreshProcessList()`.

**Lazy-load wiring:** The XAML has a `TabControl` with a "Processes" `TabItem` (MainWindow.xaml ~line 359). Add a `SelectionChanged` handler on the `TabControl`:

In `MainWindow.xaml`, on the TabControl element add: `SelectionChanged="TabControl_SelectionChanged"`

In `MainWindow.xaml.cs`, add:
```csharp
private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (sender is TabControl tc && tc.SelectedItem is TabItem tab && tab.Header?.ToString() == "Processes")
    {
        var vm = DataContext as MainViewModel;
        vm?.RefreshProcessList();
    }
}
```

This ensures process enumeration only runs when the user actually views the Processes tab.

### Fix 2: Move Initialize to Task.Run

**Root cause:** `Initialize()` runs at dispatcher BackgroundPriority — this is still the UI thread. PrivilegeManager, Monitor.Start(), and the first memory poll all block the dispatcher.

**Fix:** Move the heavy parts of Initialize() to `Task.Run()`, only updating UI-bound properties via `Dispatcher.Invoke`.

**Thread affinity:** `MemoryMonitor` creates a `DispatcherTimer` in its constructor, which captures `Dispatcher.CurrentDispatcher`. It MUST be constructed on the UI thread. Only the privilege enabling moves to background.

```csharp
private async void Initialize()
{
    if (_initialized) return;

    // Construct monitor on UI thread (DispatcherTimer requires it)
    _ = Monitor;
    ApplyMonitorSettings();
    Monitor.MemoryUpdated += OnMemoryUpdated;
    Monitor.OptimizationCompleted += OnOptimizationCompleted;

    // Move only the slow privilege calls off UI thread
    await Task.Run(() =>
    {
        if (!((App)Application.Current).IsReadOnlyMode)
            PrivilegeManager.EnableAllRequired();
    });

    // Back on UI thread — start the timer
    Monitor.Start(_settings.CheckIntervalSeconds);
    _initialized = true;
    IsLoading = false;
}
```

### Fix 3: Skip compressed memory on first poll

**Root cause:** `GetCompressedMemoryBytes()` cache starts empty, so the first call in `GetCurrentMemoryInfo()` enumerates processes.

**Fix:** Initialize `_compressedCacheExpiry` to `DateTime.UtcNow` (not `DateTime.MinValue`), and set `_cachedCompressedBytes = 0`. First call returns 0 instantly. The cache populates on the next call after 30s. Alternatively, simpler: just check `_compressedCacheExpiry == default` and return 0 on first call.

Actually even simpler — initialize the expiry to 30 seconds in the future:
```csharp
private DateTime _compressedCacheExpiry = DateTime.UtcNow.AddSeconds(30);
```
First calls return 0 (cached default). After 30s, the real lookup runs. Compressed memory is supplementary info — showing 0 for 30s on startup is fine.

### Fix 4: Simplify ProcessMemoryInfo

**Root cause:** ProcessMemoryInfo has both `WorkingSetBytes` and `PrivateBytes`. The user wants just Working Set.

**Fix:** Remove `PrivateBytes` from `ProcessMemoryInfo`. Remove the corresponding column from `MainWindow.xaml`. Remove `proc.PrivateMemorySize64` read from `RefreshProcessList()`.

In `ProcessMemoryInfo.cs`: remove `PrivateBytes` property.
In `MainWindow.xaml`: remove the Private Bytes DataGrid column.
In `MainViewModel.cs RefreshProcessList()`: remove `PrivateBytes = proc.PrivateMemorySize64` line.

### Fix 5: Add loading state

**Root cause:** No visual feedback while initialization happens. User sees a frozen window.

**Fix:** Add an `IsLoading` property to MainViewModel. Set it `true` before Initialize, `false` after. In MainWindow.xaml, show a simple "Loading..." overlay or loading indicator when `IsLoading` is true. Use WPF-UI's `ProgressRing` or a simple TextBlock.

```csharp
// MainViewModel
private bool _isLoading = true;
public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
```

In the async Initialize():
```csharp
IsLoading = true;
// ... do work ...
IsLoading = false;
```

In MainWindow.xaml, add a loading overlay bound to `IsLoading` with a `BooleanToVisibilityConverter`.

## Files Modified

| File | Changes |
|---|---|
| `src/RAMSpeed/ViewModels/MainViewModel.cs` | Async Initialize(), remove RefreshProcessList from startup, IsLoading property, remove PrivateBytes read |
| `src/RAMSpeed/MainWindow.xaml.cs` | Remove RefreshProcessList from OnLoaded |
| `src/RAMSpeed/MainWindow.xaml` | Remove Private Bytes column, add loading overlay |
| `src/RAMSpeed/Models/ProcessMemoryInfo.cs` | Remove PrivateBytes property |
| `src/RAMSpeed/Services/MemoryInfoService.cs` | Initialize compressed cache expiry to 30s future |

## eARA Gates

- Must compile with 0 errors
- All 28 existing tests must pass
- App window appears and is interactive within 500ms
- Process list loads only when user navigates to it
- No Private Bytes column visible
- Loading indicator shows during initialization

## Backward Compatibility

- Process list no longer auto-populates on startup (intentional: lazy load)
- Private Bytes removed from process view (intentional: simplification)
- Compressed memory shows 0 for first 30s (acceptable: supplementary data)
- Loading overlay is new visual element (additive, no regression)
