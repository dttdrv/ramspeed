namespace RAMSpeed.Services;

internal enum SelfTrimReason
{
    Startup,
    Periodic,
    PostOptimization
}

internal static class SelfTrimPolicy
{
    public static bool ShouldTrim(
        SelfTrimReason reason,
        DateTime nowUtc,
        DateTime? lastTrimAtUtc,
        int cooldownSeconds,
        int selfWorkingSetCapMb,
        bool isOptimizing)
    {
        if (selfWorkingSetCapMb <= 0)
            return false;

        return reason switch
        {
            SelfTrimReason.Startup => lastTrimAtUtc is null,
            SelfTrimReason.Periodic => !isOptimizing && IsPastThrottleWindow(nowUtc, lastTrimAtUtc, cooldownSeconds),
            SelfTrimReason.PostOptimization => true,
            _ => false
        };
    }

    private static bool IsPastThrottleWindow(DateTime nowUtc, DateTime? lastTrimAtUtc, int cooldownSeconds)
    {
        if (lastTrimAtUtc is null)
            return true;

        var minimumIntervalSeconds = Math.Max(cooldownSeconds, 30);
        return nowUtc - lastTrimAtUtc.Value >= TimeSpan.FromSeconds(minimumIntervalSeconds);
    }
}
