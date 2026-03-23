using System.IO;
using System.Text.Json;

namespace RAMSpeed.Models;

public enum OptimizationLevel
{
    Conservative,
    Balanced,
    Aggressive
}

public class Settings
{
    public bool AutoOptimizeEnabled { get; set; } = false;
    public int CheckIntervalSeconds { get; set; } = 5;
    public int ThresholdPercent { get; set; } = 80;
    public int CooldownSeconds { get; set; } = 30;
    public OptimizationLevel Level { get; set; } = OptimizationLevel.Balanced;
    public List<string> ExcludedProcesses { get; set; } =
    [
        "System",
        "Idle",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "svchost",
        "dwm",
        "winlogon",
        "Memory Compression",
        "Registry",
        "fontdrvhost",
        "conhost"
    ];
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public int SelfWorkingSetCapMB { get; set; } = 25;
    public int CacheMaxPercent { get; set; } = 0; // 0 = disabled, else % of total RAM
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public int HistoryMaxItems { get; set; } = 50;
    public bool ScheduledOptimizeEnabled { get; set; } = false;
    public int ScheduledOptimizeIntervalMinutes { get; set; } = 30;
    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAMSpeed");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
            }
        }
        catch
        {
            // Fall through to default
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Settings save failure is non-critical
        }
    }
}
