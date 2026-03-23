namespace RAMSpeed.Models;

public class OptimizationResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public long MemoryFreedBytes { get; set; }
    public double MemoryBeforeMB { get; set; }
    public double MemoryAfterMB { get; set; }
    public int ProcessesTrimmed { get; set; }
    public TimeSpan Duration { get; set; }
    public string[] MethodsUsed { get; set; } = [];
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public double FreedMB => MemoryFreedBytes / (1024.0 * 1024);
    public string Summary => Success
        ? $"Freed {FreedMB:F1} MB in {Duration.TotalMilliseconds:F0}ms ({ProcessesTrimmed} processes trimmed)"
        : $"Failed: {ErrorMessage}";
    public string MethodsSummary => MethodsUsed.Length > 0 ? string.Join(", ", MethodsUsed) : "";
}
