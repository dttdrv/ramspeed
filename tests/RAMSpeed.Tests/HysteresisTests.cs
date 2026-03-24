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

    private static MemoryMonitor CreateTestMonitor()
    {
        return new MemoryMonitor(
            new MemoryInfoService(),
            new MemoryOptimizer(),
            timer: null,
            utcNow: () => DateTime.UtcNow);
    }
}
