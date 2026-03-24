using RAMSpeed.ViewModels;

namespace RAMSpeed.Models;

public class ProcessMemoryInfo : ViewModelBase
{
    private bool _isExcluded;
    private int _workingSetCapMB;
    private string _memoryPriority = "Normal";

    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public long WorkingSetBytes { get; set; }

    public bool IsExcluded
    {
        get => _isExcluded;
        set => SetProperty(ref _isExcluded, value);
    }

    /// <summary>Hard working set cap in MB. 0 = no cap.</summary>
    public int WorkingSetCapMB
    {
        get => _workingSetCapMB;
        set => SetProperty(ref _workingSetCapMB, value);
    }

    /// <summary>Memory priority label for display.</summary>
    public string MemoryPriority
    {
        get => _memoryPriority;
        set => SetProperty(ref _memoryPriority, value);
    }

    public double WorkingSetMB => WorkingSetBytes / (1024.0 * 1024);
}
