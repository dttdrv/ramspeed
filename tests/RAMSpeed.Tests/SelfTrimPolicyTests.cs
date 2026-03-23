using RAMSpeed.Services;

namespace RAMSpeed.Tests;

public class SelfTrimPolicyTests
{
    [Fact]
    public void Startup_trim_runs_once_when_cap_enabled()
    {
        var now = new DateTime(2026, 3, 15, 1, 0, 0, DateTimeKind.Utc);

        var first = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.Startup,
            now,
            lastTrimAtUtc: null,
            cooldownSeconds: 30,
            selfWorkingSetCapMb: 25,
            isOptimizing: false);

        var second = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.Startup,
            now.AddSeconds(5),
            lastTrimAtUtc: now,
            cooldownSeconds: 30,
            selfWorkingSetCapMb: 25,
            isOptimizing: false);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void Periodic_trim_is_throttled_by_max_of_cooldown_and_30_seconds()
    {
        var now = new DateTime(2026, 3, 15, 1, 0, 0, DateTimeKind.Utc);
        var lastTrim = now.AddSeconds(-20);

        var shouldSkip = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.Periodic,
            now,
            lastTrim,
            cooldownSeconds: 10,
            selfWorkingSetCapMb: 25,
            isOptimizing: false);

        var shouldRun = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.Periodic,
            now.AddSeconds(15),
            lastTrim,
            cooldownSeconds: 10,
            selfWorkingSetCapMb: 25,
            isOptimizing: false);

        Assert.False(shouldSkip);
        Assert.True(shouldRun);
    }

    [Fact]
    public void Periodic_trim_skips_when_optimizing_or_cap_disabled()
    {
        var now = new DateTime(2026, 3, 15, 1, 0, 0, DateTimeKind.Utc);

        var optimizing = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.Periodic,
            now,
            lastTrimAtUtc: now.AddMinutes(-1),
            cooldownSeconds: 30,
            selfWorkingSetCapMb: 25,
            isOptimizing: true);

        var capDisabled = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.Periodic,
            now,
            lastTrimAtUtc: now.AddMinutes(-1),
            cooldownSeconds: 30,
            selfWorkingSetCapMb: 0,
            isOptimizing: false);

        Assert.False(optimizing);
        Assert.False(capDisabled);
    }

    [Fact]
    public void Post_optimization_trim_runs_immediately_when_cap_enabled()
    {
        var now = new DateTime(2026, 3, 15, 1, 0, 0, DateTimeKind.Utc);

        var result = SelfTrimPolicy.ShouldTrim(
            SelfTrimReason.PostOptimization,
            now,
            lastTrimAtUtc: now.AddSeconds(-1),
            cooldownSeconds: 300,
            selfWorkingSetCapMb: 25,
            isOptimizing: false);

        Assert.True(result);
    }
}
