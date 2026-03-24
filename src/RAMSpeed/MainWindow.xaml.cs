using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RAMSpeed.Services;
using RAMSpeed.ViewModels;
using Wpf.Ui.Controls;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using PointCollection = System.Windows.Media.PointCollection;

namespace RAMSpeed;

public partial class MainWindow : FluentWindow
{
    private MainViewModel? _vm;
    private TrayIconService? _tray;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.MemoryInfoUpdated += OnMemoryInfoUpdated;

            var settings = _vm.GetSettings();
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
            if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }

            InitializeTrayIcon();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Apply read-only mode from App if non-admin
                var app = System.Windows.Application.Current as App;
                if (app?.IsReadOnlyMode == true)
                    _vm.IsReadOnlyMode = true;

                _vm.Initialize();
                if (_vm.LastMemoryInfo != null)
                    _tray?.UpdateTooltip(_vm.LastMemoryInfo);
                app?.RestorePendingActivation();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void InitializeTrayIcon()
    {
        _tray = new TrayIconService();
        _tray.OptimizeRequested += () => Dispatcher.Invoke(() => _vm?.OptimizeNowCommand.Execute(null));
        _tray.ToggleAutoOptimizeRequested += () => Dispatcher.Invoke(() =>
        {
            if (_vm != null) _vm.AutoOptimizeEnabled = !_vm.AutoOptimizeEnabled;
        });
        _tray.ShowWindowRequested += () => Dispatcher.Invoke(RestoreWindow);
        _tray.ExitRequested += () => Dispatcher.Invoke(() => { _forceClose = true; Close(); });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.GraphPoints))
            UpdateGraphPolyline();
        else if (e.PropertyName == nameof(MainViewModel.AutoOptimizeEnabled))
            _tray?.UpdateAutoOptimizeState(_vm!.AutoOptimizeEnabled);
    }

    private void OnMemoryInfoUpdated(Models.MemoryInfo info)
    {
        _tray?.UpdateTooltip(info);
    }

    private void UpdateGraphPolyline()
    {
        if (_vm == null || string.IsNullOrWhiteSpace(_vm.GraphPoints))
            return;

        var canvas = GraphCanvas;
        var width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 600;
        var height = canvas.ActualHeight > 0 ? canvas.ActualHeight : 100;

        // Remove old dynamic elements (grid lines and labels)
        for (int i = canvas.Children.Count - 1; i >= 0; i--)
        {
            if (canvas.Children[i] is Line || canvas.Children[i] is System.Windows.Controls.TextBlock)
                canvas.Children.RemoveAt(i);
        }

        // Theme-aware grid line color
        var gridBrush = TryFindResource("ControlStrokeColorDefaultBrush") as Brush
            ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80));
        var labelBrush = TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
        var labels = new[] { ("100%", 0.0), ("75%", 0.25), ("50%", 0.5), ("25%", 0.75) };
        foreach (var (text, frac) in labels)
        {
            var y = frac * height;
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1, Opacity = 0.3
            });
            var label = new System.Windows.Controls.TextBlock { Text = text, FontSize = 8, Foreground = labelBrush };
            Canvas.SetLeft(label, 3);
            Canvas.SetTop(label, y - 1);
            canvas.Children.Add(label);
        }

        // Parse points and build line + fill
        var linePoints = new PointCollection();
        var parts = _vm.GraphPoints.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var coords = part.Split(',');
            if (coords.Length == 2 &&
                double.TryParse(coords[0], System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(coords[1], System.Globalization.CultureInfo.InvariantCulture, out var y))
            {
                linePoints.Add(new Point(x / 600 * width, y / 100 * height));
            }
        }

        GraphLine.Points = linePoints;

        // Build gradient fill polygon (line points + bottom corners to close the area)
        if (linePoints.Count >= 2)
        {
            var fillPoints = new PointCollection(linePoints);
            fillPoints.Add(new Point(linePoints[linePoints.Count - 1].X, height));
            fillPoints.Add(new Point(linePoints[0].X, height));
            GraphFill.Points = fillPoints;
        }
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGraphPolyline();
    }

    private void GraphCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_vm == null || string.IsNullOrWhiteSpace(_vm.GraphPoints))
            return;

        var canvas = GraphCanvas;
        var width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 600;
        var height = canvas.ActualHeight > 0 ? canvas.ActualHeight : 100;
        var pos = e.GetPosition(canvas);

        var linePoints = GraphLine.Points;
        if (linePoints == null || linePoints.Count < 2)
            return;

        // Find nearest point by X
        int nearestIdx = 0;
        double nearestDist = double.MaxValue;
        for (int i = 0; i < linePoints.Count; i++)
        {
            var dist = Math.Abs(linePoints[i].X - pos.X);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestIdx = i;
            }
        }

        var pt = linePoints[nearestIdx];
        double usagePercent = (1.0 - pt.Y / height) * 100;
        int pointsAgo = linePoints.Count - 1 - nearestIdx;
        int secondsAgo = pointsAgo * (_vm.CheckIntervalSeconds > 0 ? _vm.CheckIntervalSeconds : 2);
        string timeLabel = secondsAgo == 0 ? "now"
            : secondsAgo < 60 ? $"{secondsAgo}s ago"
            : $"{secondsAgo / 60}m {secondsAgo % 60}s ago";

        GraphTooltipText.Text = $"{usagePercent:F0}% — {timeLabel}";

        Canvas.SetLeft(GraphDot, pt.X - 4);
        Canvas.SetTop(GraphDot, pt.Y - 4);
        GraphDot.Visibility = Visibility.Visible;

        double tooltipLeft = pt.X + 12;
        double tooltipTop = pt.Y - 10;
        // Keep tooltip within canvas bounds
        if (tooltipLeft + 100 > width)
            tooltipLeft = pt.X - 110;
        if (tooltipTop < 0)
            tooltipTop = 2;

        Canvas.SetLeft(GraphTooltip, tooltipLeft);
        Canvas.SetTop(GraphTooltip, tooltipTop);
        GraphTooltip.Visibility = Visibility.Visible;
    }

    private void GraphCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        GraphDot.Visibility = Visibility.Collapsed;
        GraphTooltip.Visibility = Visibility.Collapsed;
    }

    private bool _forceClose;

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _vm?.SaveWindowState(ActualWidth, ActualHeight, Left, Top);

        // X button hides to tray; tray Exit actually quits
        if (!_forceClose)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            return;
        }

        _tray?.Dispose();
        if (_vm != null)
            _vm.MemoryInfoUpdated -= OnMemoryInfoUpdated;
        _vm?.Shutdown();
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
    }

    internal void RestoreFromExternalActivation()
    {
        RestoreWindow();
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TabControl tc && tc.SelectedItem is TabItem tab && tab.Header?.ToString() == "Processes")
        {
            var vm = DataContext as MainViewModel;
            vm?.RefreshProcessList();
        }
    }

    // Menu handlers removed — all actions are in the UI directly.
}
