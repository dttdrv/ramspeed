using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using RAMSpeed.Models;
using RAMSpeed.Native;

namespace RAMSpeed.Services;

/// <summary>
/// System tray icon using Hardcodet.NotifyIcon.Wpf (pure WPF, no WinForms ContextMenuStrip).
/// Shows RAM usage percentage as minimalistic text, inverted based on taskbar theme.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private MenuItem? _autoOptimizeItem;
    private bool _disposed;
    private double _lastUsagePercent;

    public event Action? OptimizeRequested;
    public event Action? ToggleAutoOptimizeRequested;
    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "RAMSpeed",
            ContextMenu = BuildContextMenu(),
            Icon = RenderPercentageIcon(0)
        };

        _taskbarIcon.TrayMouseDoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        ThemeService.Instance.TaskbarThemeChanged += OnTaskbarThemeChanged;
    }

    public void UpdateTooltip(MemoryInfo info)
    {
        if (_taskbarIcon == null) return;

        _taskbarIcon.ToolTipText = TrayTooltipFormatter.Format(info);
        _lastUsagePercent = info.UsagePercent;
        UpdateIcon(info.UsagePercent);
    }

    public void UpdateAutoOptimizeState(bool enabled)
    {
        if (_autoOptimizeItem != null)
            _autoOptimizeItem.Header = enabled ? "Auto-Optimize: On" : "Auto-Optimize: Off";
    }

    public void ShowBalloon(string title, string text)
    {
        _taskbarIcon?.ShowBalloonTip(title, text, BalloonIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeService.Instance.TaskbarThemeChanged -= OnTaskbarThemeChanged;
        if (_taskbarIcon != null)
        {
            // Dispose the current icon (clone owns its data, so Dispose frees the GDI handle)
            var currentIcon = _taskbarIcon.Icon;
            _taskbarIcon.Icon = null;
            currentIcon?.Dispose();

            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        _autoOptimizeItem = new MenuItem { Header = "Auto-Optimize: Off" };
        _autoOptimizeItem.Click += (_, _) => ToggleAutoOptimizeRequested?.Invoke();

        var menu = new ContextMenu();

        var optimizeItem = new MenuItem { Header = "Optimize Now" };
        optimizeItem.Click += (_, _) => OptimizeRequested?.Invoke();
        menu.Items.Add(optimizeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_autoOptimizeItem);
        menu.Items.Add(new Separator());

        var showItem = new MenuItem { Header = "Show RAMSpeed" };
        showItem.Click += (_, _) => ShowWindowRequested?.Invoke();
        menu.Items.Add(showItem);

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void UpdateIcon(double usagePercent)
    {
        if (_taskbarIcon == null) return;

        try
        {
            var newIcon = RenderPercentageIcon(usagePercent);
            var oldIcon = _taskbarIcon.Icon;
            _taskbarIcon.Icon = newIcon;

            // The clone returned by RenderPercentageIcon owns its data,
            // so Dispose() correctly frees the underlying GDI handle.
            oldIcon?.Dispose();
        }
        catch
        {
            // Icon generation failure is non-critical
        }
    }

    private void OnTaskbarThemeChanged()
    {
        // Theme change events can fire on background threads — marshal to UI thread
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => UpdateIcon(_lastUsagePercent));
        else
            UpdateIcon(_lastUsagePercent);
    }

    /// <summary>
    /// Renders a 32x32 icon showing just the usage percentage number.
    /// 32px is the sweet spot: crisp at 200% DPI, Windows downscales cleanly for lower DPI.
    /// Text color inverts based on taskbar theme (white on dark, dark on light).
    /// Returns a managed Icon clone that owns its data (safe to Dispose).
    /// </summary>
    private static Icon RenderPercentageIcon(double usagePercent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // AntiAliasGridFit uses greyscale AA — ClearType on transparent bg causes color fringing
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var pctText = $"{usagePercent:F0}";

        // Invert text color based on taskbar theme
        var textColor = ThemeService.Instance.IsTaskbarLight
            ? Color.FromArgb(0x1A, 0x1A, 0x1A)  // Dark text on light taskbar
            : Color.White;                        // White text on dark taskbar

        // Font sizes scaled for 32px canvas — larger for readability
        var fontSize = pctText.Length > 2 ? 19.0f : 23.0f;
        using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var brush = new SolidBrush(textColor);

        var textSize = g.MeasureString(pctText, font);
        g.DrawString(pctText, font, brush,
            (size - textSize.Width) / 2,
            (size - textSize.Height) / 2);

        // Clone creates a managed Icon that owns its data. Then immediately
        // free the raw HICON from GetHicon() to prevent GDI handle leaks.
        IntPtr hIcon = bmp.GetHicon();
        Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
        NativeMethods.DestroyIcon(hIcon);
        return icon;
    }
}
