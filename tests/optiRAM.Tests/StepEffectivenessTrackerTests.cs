using optiRAM.Services;

namespace optiRAM.Tests;

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
