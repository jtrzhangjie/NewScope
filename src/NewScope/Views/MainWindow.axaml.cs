using System;
using System.Diagnostics;
using Avalonia;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NewScope.Services;
using NewScope.ViewModels;

namespace NewScope.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _renderClock = Stopwatch.StartNew();
    private readonly UiWatchdog _watchdog;
    private long _lastRenderMilliseconds;
    private long _lastPositionLogMilliseconds;
    private long _lastResizeLogMilliseconds;
    private long _lastContextUpdateMilliseconds;
    private string _uiContext = "MainWindow constructed; waiting for Opened.";
    private bool? _responsiveLabelsVisible;
    private bool _closing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _uiTimer.Tick += OnUiTick;
        _watchdog = new UiWatchdog(() => _uiContext);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _uiTimer.Stop();
            _watchdog.Stop();
            AppLog.Event("Window", "Closed");
        };
        PositionChanged += (_, _) => OnPositionChanged();
        Resized += (_, _) => OnResized();
        ScalingChanged += (_, _) => OnScalingChanged();
        Screens.Changed += (_, _) => OnScreensChanged();
        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(WindowState))
            {
                OnWindowStateChanged(e.OldValue, e.NewValue);
            }
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        UpdateUiContext();
        _watchdog.Start();
        AppLog.Event(
            "Window",
            $"Opened: {DescribeWindow()}{Environment.NewLine}{BuildDiagnosticsDetails()}");
        var settings = AppSettingsStore.Current;
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        AppLog.Event(
            "Window",
            $"Applying saved settings: max={settings.WindowMaximized}, " +
            $"size={settings.WindowWidth:0}x{settings.WindowHeight:0}, " +
            $"position={settings.WindowX},{settings.WindowY}");
        RestoreWindowPlacement(settings);

        if (settings.WindowMaximized)
        {
            AppLog.Event("Window", $"Before maximize: {DescribeWindow()}");
            WindowState = WindowState.Maximized;
            AppLog.Event("Window", $"After maximize: {DescribeWindow()}");
        }

        UpdateUiContext();
        AppLog.Event("Window", $"Loaded: {DescribeWindow()}");
        _uiTimer.Start();
        ApplyPaneLayout();
        UpdateResponsiveLayout();
        Dispatcher.UIThread.Post(
            () =>
            {
                UpdateUiContext();
                UpdateResponsiveLayout();
                AppLog.Event("Window", $"Layout ready: {DescribeWindow()}");
            },
            DispatcherPriority.Background);
        if (DataContext is MainViewModel { IsDemo: true } viewModel)
        {
            await viewModel.ConnectCommand.ExecuteAsync(null);
        }
    }

    private void RestoreWindowPlacement(AppSettings settings)
    {
        if (settings.WindowX == int.MinValue || settings.WindowY == int.MinValue)
        {
            return;
        }

        var point = new PixelPoint(settings.WindowX, settings.WindowY);
        var screen = Screens.All.FirstOrDefault(candidate =>
            point.X >= candidate.Bounds.X &&
            point.X < candidate.Bounds.X + candidate.Bounds.Width &&
            point.Y >= candidate.Bounds.Y &&
            point.Y < candidate.Bounds.Y + candidate.Bounds.Height);
        screen ??= Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            AppLog.Warn("Window placement restore skipped: no screens reported");
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)Math.Round(Width * screen.Scaling);
        var height = (int)Math.Round(Height * screen.Scaling);
        var (x, y) = WindowPlacementPolicy.Clamp(
            settings.WindowX,
            settings.WindowY,
            width,
            height,
            new WindowWorkArea(area.X, area.Y, area.Width, area.Height));

        AppLog.Event(
            "Window",
            $"Restore placement: requested={point}, resolved={x},{y}, " +
            $"workArea={area}, screen={DescribeScreen(screen)}");
        Position = new PixelPoint(x, y);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        _watchdog.Beat();

        var now = _renderClock.ElapsedMilliseconds;
        if (now - _lastContextUpdateMilliseconds >= 1000)
        {
            _lastContextUpdateMilliseconds = now;
            UpdateUiContext();
            UpdateResponsiveLayout();
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateLiveStats();

        var channelCount = viewModel.Channels.Count;
        var minimumInterval = channelCount > 128 ? 200 : channelCount > 32 ? 66 : 16;
        if (now - _lastRenderMilliseconds < minimumInterval)
        {
            return;
        }

        _lastRenderMilliseconds = now;
        Plot.InvalidateVisual();
        if (viewModel.ShowTwoPanes)
        {
            PlotB.InvalidateVisual();
        }

        if (viewModel.ShowPaneC)
        {
            PlotC.InvalidateVisual();
        }

        if (viewModel.ShowPaneD)
        {
            PlotD.InvalidateVisual();
        }
    }

    private void Fit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Plot.FitAll();
        if (PlotB.IsVisible)
        {
            PlotB.FitAll();
        }

        if (PlotC.IsVisible)
        {
            PlotC.FitAll();
        }

        if (PlotD.IsVisible)
        {
            PlotD.FitAll();
        }
    }

    private void PaneCount_Changed(object? sender, SelectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(ApplyPaneLayout);
    }

    private void ApplyPaneLayout()
    {
        var count = DataContext is MainViewModel viewModel ? Math.Clamp(viewModel.PaneCount, 1, 4) : 1;
        var rows = PlotArea.RowDefinitions;
        rows[0].Height = new GridLength(1, GridUnitType.Star);
        rows[1].Height = count >= 2 ? new GridLength(6) : new GridLength(0);
        rows[2].Height = count >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        rows[3].Height = count >= 3 ? new GridLength(6) : new GridLength(0);
        rows[4].Height = count >= 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        rows[5].Height = count >= 4 ? new GridLength(6) : new GridLength(0);
        rows[6].Height = count >= 4 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        PlotSplitter.IsVisible = count >= 2;
        PlotB.IsVisible = count >= 2;
        PlotSplitterC.IsVisible = count >= 3;
        PlotC.IsVisible = count >= 3;
        PlotSplitterD.IsVisible = count >= 4;
        PlotD.IsVisible = count >= 4;
    }

    private async void ImportSymbols_Click(object? sender, RoutedEventArgs e) =>
        await ImportSymbolsAsync();

    private async Task ImportSymbolsAsync()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Get("ImportDialogTitle"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("AXF / ELF")
                    {
                        Patterns = ["*.axf", "*.elf", "*.out", "*.axf.*"],
                        MimeTypes = ["application/octet-stream"]
                    }
                ]
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            await viewModel.ImportSymbolsAsync(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Symbol picker/import failed", ex);
            viewModel.ErrorMessage = Localization.Format("ErrorImportFailed", ex.Message);
            viewModel.HasError = true;
        }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e) =>
        await ExportAsync();

    private async Task ExportAsync()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            if (!viewModel.HasSamples)
            {
                viewModel.StatusText = Localization.Get("ErrorNoSamplesToExport");
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Localization.Get("ExportDialogTitle"),
                SuggestedFileName = $"newscope_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                DefaultExtension = "csv",
                FileTypeChoices =
                [
                    new FilePickerFileType(Localization.Get("CsvFileType"))
                    {
                        Patterns = ["*.csv"],
                        MimeTypes = ["text/csv"]
                    }
                ]
            });

            if (file?.TryGetLocalPath() is not { } path)
            {
                return;
            }

            await viewModel.ExportCsvAsync(path, file.Name);
        }
        catch (Exception ex)
        {
            AppLog.Error("CSV export failed", ex);
            viewModel.ErrorMessage = Localization.Format("ErrorExportFailed", ex.Message);
            viewModel.HasError = true;
        }
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (viewModel.ToggleRunCommand.CanExecute(null))
            {
                await viewModel.ToggleRunCommand.ExecuteAsync(null);
            }
        }
        else if (e.Key == Key.Escape && viewModel.HasError)
        {
            e.Handled = true;
            viewModel.DismissErrorCommand.Execute(null);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.O:
                    e.Handled = true;
                    await ImportSymbolsAsync();
                    break;
                case Key.E:
                    e.Handled = true;
                    await ExportAsync();
                    break;
                case Key.D:
                    e.Handled = true;
                    await ExportDiagnosticsAsync();
                    break;
                case Key.L:
                    e.Handled = true;
                    viewModel.ToggleLanguageCommand.Execute(null);
                    break;
            }
        }
    }

    private async void Diagnostics_Click(object? sender, RoutedEventArgs e) =>
        await ExportDiagnosticsAsync();

    private async Task ExportDiagnosticsAsync()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Localization.Get("DiagnosticsDialogTitle"),
                SuggestedFileName = $"newscope_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                DefaultExtension = "zip",
                FileTypeChoices =
                [
                    new FilePickerFileType(Localization.Get("DiagnosticsFileType"))
                    {
                        Patterns = ["*.zip"],
                        MimeTypes = ["application/zip"]
                    }
                ]
            });

            if (file?.TryGetLocalPath() is not { } path)
            {
                return;
            }

            await Task.Run(() => DiagnosticsService.CreateBundle(path, BuildDiagnosticsDetails()));
            viewModel.StatusText = Localization.Format("DiagnosticsSaved", file.Name);
            viewModel.HasError = false;
            AppLog.Info($"Diagnostics exported: {path}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Diagnostics export failed", ex);
            viewModel.ErrorMessage = Localization.Format("DiagnosticsFailed", ex.Message);
            viewModel.HasError = true;
        }
    }

    private string BuildDiagnosticsDetails()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Timestamp: {DateTime.Now:O}");
        builder.AppendLine($"Session: {AppLog.SessionId}; PID: {Environment.ProcessId}");
        builder.AppendLine($"WindowState: {WindowState}");
        builder.AppendLine($"Position (physical): {Position}");
        builder.AppendLine($"Bounds (logical): {Bounds}");
        builder.AppendLine($"ClientSize (logical): {ClientSize}");
        builder.AppendLine($"RenderScaling: {RenderScaling:0.###}");
        builder.AppendLine($"Active screen (window): {DescribeScreen(SafeScreenFromWindow())}");
        builder.AppendLine($"Active screen (position): {DescribeScreen(SafeScreenFromPoint(Position))}");
        var handle = TryGetPlatformHandle();
        builder.AppendLine(handle is null
            ? "HWND: unavailable"
            : $"HWND: 0x{handle.Handle.ToInt64():X}");
        builder.AppendLine($"Primary screen: {DescribeScreen(SafePrimaryScreen())}");
        builder.AppendLine("Screens:");
        foreach (var screen in Screens.All)
        {
            builder.AppendLine(
                $"  {DescribeScreen(screen)} Bounds={screen.Bounds} WorkArea={screen.WorkingArea}");
        }

        return builder.ToString();
    }

    private Avalonia.Platform.Screen? SafeScreenFromPoint(PixelPoint point)
    {
        try
        {
            return Screens.ScreenFromPoint(point);
        }
        catch
        {
            return null;
        }
    }

    private Avalonia.Platform.Screen? SafePrimaryScreen()
    {
        try
        {
            return Screens.Primary;
        }
        catch
        {
            return null;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        UpdateUiContext();
        AppLog.Event(
            "Window",
            $"Close requested: {DescribeWindow()}{Environment.NewLine}{BuildDiagnosticsDetails()}");
        _watchdog.Stop();
        _uiTimer.Stop();
        var settings = AppSettingsStore.Current;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Bounds.Width;
            settings.WindowHeight = Bounds.Height;
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
        }

        try
        {
            await viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Shutdown failed", ex);
        }
        finally
        {
            AppLog.Event("Window", "Shutdown complete");
            _allowClose = true;
            Close();
        }
    }

    private void OnWindowStateChanged(object? oldValue, object? newValue)
    {
        RunOnUiThread(() =>
        {
            UpdateUiContext();
            AppLog.Event(
                "Window",
                $"State changed {oldValue ?? "?"} -> {newValue ?? "?"}: {DescribeWindow()}");
            AppLog.Event("Window", BuildDiagnosticsDetails());
        });
    }

    private void OnPositionChanged()
    {
        RunOnUiThread(() =>
        {
            var now = Environment.TickCount64;
            if (WindowState == WindowState.Normal && now - _lastPositionLogMilliseconds < 1000)
            {
                return;
            }

            _lastPositionLogMilliseconds = now;
            UpdateUiContext();
            AppLog.Event("Window", $"Position changed: {DescribeWindow()}");
        });
    }

    private void OnResized()
    {
        RunOnUiThread(() =>
        {
            UpdateResponsiveLayout();
            var now = Environment.TickCount64;
            if (now - _lastResizeLogMilliseconds < 1000)
            {
                UpdateUiContext();
                return;
            }

            _lastResizeLogMilliseconds = now;
            UpdateUiContext();
            AppLog.Event("Window", $"Resized: {DescribeWindow()}");
        });
    }

    private void OnScalingChanged()
    {
        RunOnUiThread(() =>
        {
            UpdateUiContext();
            AppLog.Event("Display", $"Scaling changed: {DescribeWindow()}");
            AppLog.Event("Display", BuildDiagnosticsDetails());
        });
    }

    private void OnScreensChanged()
    {
        RunOnUiThread(() =>
        {
            UpdateUiContext();
            AppLog.Event("Display", $"Screens changed: {DescribeWindow()}");
            AppLog.Event("Display", BuildDiagnosticsDetails());
        });
    }

    private void UpdateResponsiveLayout()
    {
        var width = Bounds.Width;
        var showLabels = width >= 1320;
        RateLabel.IsVisible = showLabels;
        WindowLabel.IsVisible = showLabels;
        PanesLabel.IsVisible = showLabels;
        TargetPaneLabel.IsVisible = showLabels;
        SwdLabel.IsVisible = showLabels;
        SelectedCountChip.IsVisible = width >= 1160;
        if (_responsiveLabelsVisible != showLabels)
        {
            _responsiveLabelsVisible = showLabels;
            AppLog.Event(
                "Window",
                $"Responsive labels={showLabels}: bounds={width:0.#}, client={ClientSize.Width:0.#}");
        }
    }

    private void UpdateUiContext()
    {
        try
        {
            _uiContext = BuildDiagnosticsDetails();
        }
        catch (Exception ex)
        {
            _uiContext = $"Failed to collect UI context: {ex}";
        }
    }

    private string DescribeWindow()
    {
        return $"state={WindowState}, position={Position}, bounds={Bounds}, " +
               $"client={ClientSize}, scaling={RenderScaling:0.##}, screen={DescribeScreen(SafeScreenFromWindow())}";
    }

    private Avalonia.Platform.Screen? SafeScreenFromWindow()
    {
        try
        {
            return Screens.ScreenFromWindow(this);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeScreen(Avalonia.Platform.Screen? screen) =>
        screen is null
            ? "unknown"
            : $"{screen.DisplayName} {screen.Bounds}/{screen.WorkingArea}" +
              $"@{screen.Scaling:0.##}{(screen.IsPrimary ? " primary" : string.Empty)}";

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }
}
