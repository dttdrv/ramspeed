using optiRAM.Models;
using optiRAM.Services;

namespace optiRAM.Tests;

public class TrayTooltipFormatterTests
{
    [Fact]
    public void Format_uses_raw_memory_snapshot_values()
    {
        var info = new MemoryInfo
        {
            TotalPhysicalBytes = 16UL * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 6UL * 1024 * 1024 * 1024
        };

        var text = TrayTooltipFormatter.Format(info);

        Assert.Equal("optiRAM - 62% used (10.0 / 16.0 GB)", text);
    }

    [Fact]
    public void Format_truncates_to_notify_icon_limit()
    {
        var info = new MemoryInfo
        {
            TotalPhysicalBytes = ulong.MaxValue,
            AvailablePhysicalBytes = 0
        };

        var text = TrayTooltipFormatter.Format(info);

        Assert.True(text.Length <= 127);
    }
}
