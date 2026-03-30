namespace optiRAM.Models;

public class MemoryInfo
{
    public ulong TotalPhysicalBytes { get; set; }
    public ulong AvailablePhysicalBytes { get; set; }
    public ulong UsedPhysicalBytes => TotalPhysicalBytes - AvailablePhysicalBytes;
    public double UsagePercent => TotalPhysicalBytes > 0 ? (double)UsedPhysicalBytes / TotalPhysicalBytes * 100 : 0;
    public double AvailablePercent => 100 - UsagePercent;
    public ulong CachedBytes { get; set; }
    public ulong ModifiedBytes { get; set; }
    public ulong StandbyBytes { get; set; }
    public ulong FreeBytes { get; set; }
    public ulong TotalPageFileBytes { get; set; }
    public ulong AvailablePageFileBytes { get; set; }
    public ulong PageSize { get; set; }
    public uint ProcessCount { get; set; }
    public uint ThreadCount { get; set; }
    public uint HandleCount { get; set; }
    public ulong KernelTotalBytes { get; set; }
    public ulong KernelPagedBytes { get; set; }
    public ulong KernelNonpagedBytes { get; set; }
    public ulong CommitTotalBytes { get; set; }
    public ulong CommitLimitBytes { get; set; }
    public ulong CompressedBytes { get; set; }

    public double TotalGB => TotalPhysicalBytes / (1024.0 * 1024 * 1024);
    public double UsedGB => UsedPhysicalBytes / (1024.0 * 1024 * 1024);
    public double AvailableGB => AvailablePhysicalBytes / (1024.0 * 1024 * 1024);
    public double CachedGB => CachedBytes / (1024.0 * 1024 * 1024);
    public double ModifiedGB => ModifiedBytes / (1024.0 * 1024 * 1024);
    public double StandbyGB => StandbyBytes / (1024.0 * 1024 * 1024);
    public double FreeGB => FreeBytes / (1024.0 * 1024 * 1024);
    public double CompressedMB => CompressedBytes / (1024.0 * 1024);
    public double KernelPagedMB => KernelPagedBytes / (1024.0 * 1024);
    public double KernelNonpagedMB => KernelNonpagedBytes / (1024.0 * 1024);
    public double CommitGB => CommitTotalBytes / (1024.0 * 1024 * 1024);
    public double CommitLimitGB => CommitLimitBytes / (1024.0 * 1024 * 1024);
    public double CommitPercent => CommitLimitBytes > 0 ? (double)CommitTotalBytes / CommitLimitBytes * 100 : 0;
}
