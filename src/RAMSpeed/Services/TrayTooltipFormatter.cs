using RAMSpeed.Models;

namespace RAMSpeed.Services;

internal static class TrayTooltipFormatter
{
    private const int NotifyIconTextLimit = 127;

    public static string Format(MemoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var text = $"RAMSpeed - {info.UsagePercent:F0}% used ({info.UsedGB:F1} / {info.TotalGB:F1} GB)";
        return text.Length <= NotifyIconTextLimit ? text : text[..NotifyIconTextLimit];
    }
}
