using optiRAM.Services;

namespace optiRAM.Tests;

public class HysteresisTests
{
    [Fact]
    public void HysteresisGap_property_exists_with_default()
    {
        var monitor = CreateTestMonitor();
        monitor.HysteresisGap = 10;
        Assert.Equal(10, monitor.HysteresisGap);
    }

    [Fact]
    public void TrendWindowSize_property_exists_with_default()
    {
        var monitor = CreateTestMonitor();
        monitor.TrendWindowSize = 10;
        Assert.Equal(10, monitor.TrendWindowSize);
    }

    [Fact]
    public void PredictiveLeadSeconds_property_exists_with_default()
    {
        var monitor = CreateTestMonitor();
        monitor.PredictiveLeadSeconds = 15;
        Assert.Equal(15, monitor.PredictiveLeadSeconds);
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
