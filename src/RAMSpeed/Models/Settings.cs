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
                var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
                settings.Validate();
                return settings;
            }
        }
        catch
        {
            // Corrupted or inaccessible settings — fall through to defaults
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

    /// <summary>Clamp all values to valid ranges to prevent crashes from hand-edited or corrupted settings.</summary>
    private void Validate()
    {
        CheckIntervalSeconds = Math.Clamp(CheckIntervalSeconds, 1, 60);
        ThresholdPercent = Math.Clamp(ThresholdPercent, 10, 95);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 5, 300);
        CacheMaxPercent = Math.Clamp(CacheMaxPercent, 0, 75);
        SelfWorkingSetCapMB = Math.Clamp(SelfWorkingSetCapMB, 0, 100);
        HistoryMaxItems = Math.Clamp(HistoryMaxItems, 1, 500);
        ScheduledOptimizeIntervalMinutes = Math.Clamp(ScheduledOptimizeIntervalMinutes, 1, 240);
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 400, 4000) : 1000;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 300, 3000) : 700;
        ThemeMode ??= "System";
        ExcludedProcesses ??= [];
    }
}
