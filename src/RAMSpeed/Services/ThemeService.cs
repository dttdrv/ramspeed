using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using SystemTheme = Wpf.Ui.Appearance.SystemTheme;

namespace RAMSpeed.Services;

/// <summary>
/// Detects system theme (dark/light) and taskbar theme, applies WPF-UI theme,
/// and raises events when the user changes their Windows theme preferences.
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private bool _disposed;

    /// <summary>Raised when the taskbar theme changes (light ↔ dark), so the tray icon can re-render.</summary>
    public event Action? TaskbarThemeChanged;

    /// <summary>Raised when the app theme changes (light ↔ dark).</summary>
    public event Action? AppThemeChanged;

    public static ThemeService Instance { get; } = new();

    /// <summary>
    /// True when the Windows taskbar uses a light theme.
    /// Read from HKCU\...\Personalize\SystemUsesLightTheme (distinct from AppsUseLightTheme).
    /// </summary>
    public bool IsTaskbarLight { get; private set; }

    private ThemeService()
    {
        RefreshTaskbarTheme();
    }

    /// <summary>Apply the current system theme and accent color to the application.</summary>
    public void ApplySystemTheme()
    {
        var systemTheme = ApplicationThemeManager.GetSystemTheme();
        // Convert SystemTheme to ApplicationTheme
        var appTheme = systemTheme switch
        {
            SystemTheme.Dark => ApplicationTheme.Dark,
            SystemTheme.Light => ApplicationTheme.Light,
            _ => ApplicationTheme.Dark
        };
        ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, true);
        ApplicationAccentColorManager.ApplySystemAccent();
        RefreshTaskbarTheme();
    }

    /// <summary>Handle Windows theme change notifications.</summary>
    public void OnSystemThemeChanged()
    {
        var oldTaskbarLight = IsTaskbarLight;

        ApplySystemTheme();

        AppThemeChanged?.Invoke();

        if (oldTaskbarLight != IsTaskbarLight)
            TaskbarThemeChanged?.Invoke();
    }

    private void RefreshTaskbarTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            IsTaskbarLight = value is int i && i == 1;
        }
        catch
        {
            IsTaskbarLight = false; // Default to dark taskbar
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
