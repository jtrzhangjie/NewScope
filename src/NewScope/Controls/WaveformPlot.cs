using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using NewScope.Models;
using NewScope.Services;
using NewScope.ViewModels;

namespace NewScope.Controls;

public sealed class WaveformPlot : Control
{
    private static readonly PlotPalette DarkPalette = new(
        background: "#0A0F15",
        panel: "#0D141C",
        grid: "#1A2633",
        gridStrong: "#263849",
        axis: "#34485C",
        label: "#8FA2B5",
        muted: "#5E7288",
        crosshair: "#71869B",
        tooltipBackground: "#E6101822",
        legendBackground: "#C70D141C",
        empty: "#6C8094",
        tooltipBorder: "#32465A",
        valueText: "#E6EEF7");

    private static readonly PlotPalette LightPalette = new(
        background: "#F8FAFC",
        panel: "#FFFFFF",
        grid: "#E2E8F0",
        gridStrong: "#CBD5E1",
        axis: "#94A3B8",
        label: "#475569",
        muted: "#64748B",
        crosshair: "#475569",
        tooltipBackground: "#F2FFFFFF",
        legendBackground: "#E8FFFFFF",
        empty: "#64748B",
        tooltipBorder: "#CBD5E1",
        valueText: "#0F172A");

    private PlotPalette CurrentPalette =>
        ActualThemeVariant == ThemeVariant.Light ? LightPalette : DarkPalette;

    private static readonly Typeface UiTypeface = new(FontFamily.Parse("Segoe UI Variable Text, Segoe UI"));
    private static readonly Typeface MonoTypeface = new(FontFamily.Parse("Cascadia Mono, Consolas, Courier New"));

    private const double LeftMargin = 70;
    private const double TopMargin = 20;
    private const double RightMargin = 18;
    private const double BottomMargin = 36;

    private INotifyCollectionChanged? _collectionNotifications;
    private bool _observing;
    private SamplePoint[] _sampleBuffer = [];
    private Point[] _screenPoints = [];
    private readonly Dictionary<string, double> _labelWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _valueWidths = new(StringComparer.Ordinal);
    private double _viewStart;
    private double _viewEnd;
    private Rect _plotRect;
    private Point _pointer;
    private bool _pointerInside;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartViewStart;
    private double _dragStartViewEnd;
    private readonly AxisRangePolicy _axisPolicy = new();
    private double _followSpan = 5.0;
    private bool _manualY;
    private double _manualYMin;
    private double _manualYMax;

    public static readonly StyledProperty<IEnumerable<ChannelViewModel>?> ChannelsProperty =
        AvaloniaProperty.Register<WaveformPlot, IEnumerable<ChannelViewModel>?>(nameof(Channels));

    public static readonly StyledProperty<double> WindowSecondsProperty =
        AvaloniaProperty.Register<WaveformPlot, double>(nameof(WindowSeconds), 5.0);

    public static readonly StyledProperty<bool> AutoFollowProperty =
        AvaloniaProperty.Register<WaveformPlot, bool>(
            nameof(AutoFollow),
            true,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly DirectProperty<WaveformPlot, string> ViewSpanTextProperty =
        AvaloniaProperty.RegisterDirect<WaveformPlot, string>(
            nameof(ViewSpanText),
            plot => plot.ViewSpanText);

    public static readonly DirectProperty<WaveformPlot, string> YModeTextProperty =
        AvaloniaProperty.RegisterDirect<WaveformPlot, string>(
            nameof(YModeText),
            plot => plot.YModeText);

    private string _viewSpanText = "5 s";
    private string _yModeText = Localization.Get("YAuto");

    public WaveformPlot()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        DoubleTapped += OnDoubleTapped;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IEnumerable<ChannelViewModel>? Channels
    {
        get => GetValue(ChannelsProperty);
        set => SetValue(ChannelsProperty, value);
    }

    public double WindowSeconds
    {
        get => GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public bool AutoFollow
    {
        get => GetValue(AutoFollowProperty);
        set => SetValue(AutoFollowProperty, value);
    }

    public string ViewSpanText
    {
        get => _viewSpanText;
        private set => SetAndRaise(ViewSpanTextProperty, ref _viewSpanText, value);
    }

    public string YModeText
    {
        get => _yModeText;
        private set => SetAndRaise(YModeTextProperty, ref _yModeText, value);
    }

    public void FitAll()
    {
        ResetYAxis();
        var channels = VisibleChannels();
        var first = double.PositiveInfinity;
        var last = double.NegativeInfinity;
        foreach (var channel in channels)
        {
            var range = channel.Buffer.GetTimeRange();
            if (range.First < first && range.Last > range.First)
            {
                first = range.First;
            }

            if (range.Last > last)
            {
                last = range.Last;
            }
        }

        if (!double.IsFinite(first) || !double.IsFinite(last) || last <= first)
        {
            _viewStart = 0;
            _viewEnd = Math.Max(1, WindowSeconds);
        }
        else
        {
            var padding = Math.Max(0.02, (last - first) * 0.03);
            _viewStart = Math.Max(0, first - padding);
            _viewEnd = last + padding;
        }

        ViewSpanText = FormatVisibleSpan(_viewEnd - _viewStart);
        SetCurrentValue(AutoFollowProperty, false);
        InvalidateVisual();
    }

    public void FollowLatest()
    {
        SetCurrentValue(AutoFollowProperty, true);
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_observing)
        {
            _observing = true;
            Localization.LanguageChanged += OnLanguageChanged;
            if (_collectionNotifications is not null)
            {
                _collectionNotifications.CollectionChanged += OnChannelsCollectionChanged;
            }
        }

        OnLanguageChanged(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observing)
        {
            _observing = false;
            Localization.LanguageChanged -= OnLanguageChanged;
            if (_collectionNotifications is not null)
            {
                _collectionNotifications.CollectionChanged -= OnChannelsCollectionChanged;
            }
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChannelsProperty)
        {
            if (_observing && _collectionNotifications is not null)
            {
                _collectionNotifications.CollectionChanged -= OnChannelsCollectionChanged;
            }

            _collectionNotifications = change.GetNewValue<IEnumerable<ChannelViewModel>?>() as INotifyCollectionChanged;
            if (_observing && _collectionNotifications is not null)
            {
                _collectionNotifications.CollectionChanged += OnChannelsCollectionChanged;
            }

            _labelWidths.Clear();
            _valueWidths.Clear();
            InvalidateVisual();
        }
        else if (change.Property == WindowSecondsProperty)
        {
            var seconds = Math.Max(0.01, change.GetNewValue<double>());
            _followSpan = seconds;
            if (!AutoFollow)
            {
                _viewEnd = _viewEnd <= 0 ? seconds : _viewEnd;
                _viewStart = _viewEnd - seconds;
            }

            ViewSpanText = FormatVisibleSpan(seconds);
            InvalidateVisual();
        }
        else if (change.Property == AutoFollowProperty)
        {
            if (change.GetNewValue<bool>())
            {
                _followSpan = Math.Max(0.01, WindowSeconds);
                ViewSpanText = FormatVisibleSpan(_followSpan);
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !_plotRect.Contains(e.GetPosition(this)))
        {
            return;
        }

        _isDragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartViewStart = ViewStart;
        _dragStartViewEnd = ViewEnd;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        _pointerInside = _plotRect.Contains(_pointer);

        if (_isDragging)
        {
            var dx = _pointer.X - _dragStart.X;
            var span = Math.Max(0.001, _dragStartViewEnd - _dragStartViewStart);
            var shift = -dx / Math.Max(1, _plotRect.Width) * span;
            _viewStart = _dragStartViewStart + shift;
            _viewEnd = _dragStartViewEnd + shift;
            if (_viewStart < 0)
            {
                _viewEnd -= _viewStart;
                _viewStart = 0;
            }

            SetCurrentValue(AutoFollowProperty, false);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0)
        {
            return;
        }
        var position = e.GetPosition(this);
        if (_plotRect.Height > 0 && position.X < _plotRect.Left &&
            position.Y >= _plotRect.Top && position.Y <= _plotRect.Bottom)
        {
            ZoomY(position.Y, e.Delta.Y);
            e.Handled = true;
            return;
        }

        if (!_plotRect.Contains(position))
        {
            return;
        }

        var span = Math.Max(0.001, AutoFollow ? _followSpan : ViewEnd - ViewStart);
        var factor = e.Delta.Y > 0 ? 0.8 : 1.25;
        var newSpan = Math.Clamp(span * factor, 0.01, 3600);
        if (AutoFollow)
        {
            _followSpan = newSpan;
        }
        else
        {
            var anchor = ViewStart + (position.X - _plotRect.X) / Math.Max(1, _plotRect.Width) * span;
            var ratio = newSpan / span;
            _viewStart = anchor - (anchor - ViewStart) * ratio;
            _viewEnd = _viewStart + newSpan;
            if (_viewStart < 0)
            {
                _viewEnd -= _viewStart;
                _viewStart = 0;
            }
        }

        ViewSpanText = FormatVisibleSpan(newSpan);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        context.FillRectangle(CurrentPalette.PlotBackground, new Rect(0, 0, width, height));
        if (width < LeftMargin + RightMargin + 24 || height < 80)
        {
            _plotRect = default;
            return;
        }
        _plotRect = new Rect(
            LeftMargin,
            TopMargin,
            Math.Max(10, width - LeftMargin - RightMargin),
            Math.Max(10, height - TopMargin - BottomMargin));

        var channels = VisibleChannels();
        if (AutoFollow)
        {
            var latest = channels
                .Select(channel => channel.Buffer.GetTimeRange().Last)
                .DefaultIfEmpty(0)
                .Max();
            var window = Math.Max(0.01, _followSpan);
            _viewEnd = Math.Max(window, latest);
            _viewStart = _viewEnd - window;
        }
        else if (_viewEnd <= _viewStart)
        {
            _viewStart = 0;
            _viewEnd = Math.Max(0.1, WindowSeconds);
        }

        var (yMin, yMax) = CalculateYRange(channels);

        context.FillRectangle(CurrentPalette.PlotPanel, _plotRect, 5);
        DrawGrid(context, yMin, yMax);
        DrawSeries(context, channels, yMin, yMax);
        context.DrawRectangle(CurrentPalette.AxisPen, _plotRect, 5);
        DrawTicks(context, yMin, yMax);
        DrawLegend(context, channels);

        if (_pointerInside && !_isDragging)
        {
            DrawCrosshair(context, channels, yMin, yMax);
        }

        if (channels.Length == 0)
        {
            DrawEmptyState(context, Localization.Get("PlotEmpty"));
        }
        else if (channels.All(channel => channel.Buffer.Count == 0))
        {
            DrawEmptyState(context, Localization.Get("PlotWaiting"));
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        YModeText = Localization.Get(_manualY ? "YManual" : "YAuto");
        InvalidateVisual();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_plotRect.Height > 0 && position.X < _plotRect.Left &&
            position.Y >= _plotRect.Top && position.Y <= _plotRect.Bottom)
        {
            ResetYAxis();
            e.Handled = true;
            return;
        }

        FitAll();
    }

    private void ZoomY(double pointerY, double delta)
    {
        if (delta == 0 || _plotRect.Height <= 0)
        {
            return;
        }

        var (min, max) = CalculateYRange(VisibleChannels());
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            return;
        }

        var ratio = Math.Clamp((pointerY - _plotRect.Top) / _plotRect.Height, 0, 1);
        var anchor = max * (1 - ratio) + min * ratio;
        var factor = delta > 0 ? 0.8 : 1.25;
        var newMin = min * factor + anchor * (1 - factor);
        var newMax = max * factor + anchor * (1 - factor);
        if (!double.IsFinite(newMin) || !double.IsFinite(newMax) || newMax - newMin < 1e-12)
        {
            return;
        }

        _manualYMin = newMin;
        _manualYMax = newMax;
        _manualY = true;
        YModeText = Localization.Get("YManual");
        InvalidateVisual();
    }

    private void ResetYAxis()
    {
        _manualY = false;
        _manualYMin = 0;
        _manualYMax = 0;
        _axisPolicy.Reset();
        YModeText = Localization.Get("YAuto");
        InvalidateVisual();
    }

    private double ViewStart => Math.Max(0, _viewStart);
    private double ViewEnd => Math.Max(ViewStart + 0.001, _viewEnd);

    private static string FormatVisibleSpan(double seconds)
    {
        if (seconds < 1)
        {
            return $"{seconds * 1000:0} ms";
        }

        if (seconds < 10)
        {
            return $"{seconds:0.##} s";
        }

        if (seconds < 60)
        {
            return $"{seconds:0} s";
        }

        return $"{seconds / 60:0.#} min";
    }

    private ChannelViewModel[] VisibleChannels() =>
        Channels?.Where(channel => channel.IsVisible).ToArray() ?? [];

    private void OnChannelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _axisPolicy.Reset();
        InvalidateVisual();
    }

    private int GetMaxPoints(int channelCount)
    {
        var widthPoints = Math.Max(200, (int)_plotRect.Width * 2);
        const int framePointBudget = 64_000;
        return Math.Max(120, Math.Min(widthPoints, framePointBudget / Math.Max(1, channelCount)));
    }

    private (double Min, double Max) CalculateYRange(ChannelViewModel[] channels)
    {
        if (_manualY && double.IsFinite(_manualYMin) && double.IsFinite(_manualYMax) &&
            _manualYMax > _manualYMin)
        {
            return (_manualYMin, _manualYMax);
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var channel in channels)
        {
            if (channel.Buffer.TryGetVisibleRange(ViewStart, ViewEnd, out var channelMin, out var channelMax))
            {
                min = Math.Min(min, channelMin);
                max = Math.Max(max, channelMax);
            }
        }

        return _axisPolicy.Update(min, max, Environment.TickCount64);
    }

    private void DrawGrid(DrawingContext context, double yMin, double yMax)
    {
        const int xTicks = 6;
        for (var i = 0; i <= xTicks; i++)
        {
            var x = _plotRect.X + _plotRect.Width * i / xTicks;
            context.DrawLine(i == 0 || i == xTicks ? CurrentPalette.GridStrongPen : CurrentPalette.GridPen,
                new Point(x, _plotRect.Top),
                new Point(x, _plotRect.Bottom));
        }

        const int yTicks = 4;
        for (var i = 0; i <= yTicks; i++)
        {
            var y = _plotRect.Y + _plotRect.Height * i / yTicks;
            context.DrawLine(i == yTicks ? CurrentPalette.GridStrongPen : CurrentPalette.GridPen,
                new Point(_plotRect.Left, y),
                new Point(_plotRect.Right, y));
        }

        if (yMin < 0 && yMax > 0)
        {
            var zeroY = MapY(0, yMin, yMax);
            context.DrawLine(CurrentPalette.GridStrongPen,
                new Point(_plotRect.Left, zeroY),
                new Point(_plotRect.Right, zeroY));
        }
    }

    private void DrawSeries(DrawingContext context, ChannelViewModel[] channels, double yMin, double yMax)
    {
        if (channels.Length == 0)
        {
            return;
        }

        var maxPoints = GetMaxPoints(channels.Length);
        if (_sampleBuffer.Length < maxPoints + 1)
        {
            _sampleBuffer = new SamplePoint[maxPoints + 1];
            _screenPoints = new Point[maxPoints + 1];
        }
        var buffer = _sampleBuffer;
        var groups = new Dictionary<string, List<ChannelViewModel>>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            if (!groups.TryGetValue(channel.ColorHex, out var group))
            {
                group = [];
                groups[channel.ColorHex] = group;
            }

            group.Add(channel);
        }

        using (context.PushClip(_plotRect))
        {
            foreach (var group in groups.Values)
            {
                foreach (var channel in group)
                {
                    var count = channel.Buffer.CopyVisible(ViewStart, ViewEnd, buffer, maxPoints);
                    if (count == 0)
                    {
                        continue;
                    }

                    if (count == 1)
                    {
                        var point = buffer[0];
                        if (point.IsValid)
                        {
                            context.DrawEllipse(channel.ColorBrush, null,
                                new Point(MapX(point.Time), MapY(point.Value, yMin, yMax)), 2.2, 2.2);
                        }
                        continue;
                    }

                    // Keep paths independent. Combining many crossing traces
                    // of the same color makes native stroke tessellation much
                    // more expensive than the number of sampled points suggests.
                    // Published geometry is immutable: the render thread can
                    // still be consuming a previous frame while this one builds.
                    var geometry = new StreamGeometry();
                    using (var geometryContext = geometry.Open())
                    {
                        var offset = 0;
                        while (offset < count)
                        {
                            while (offset < count && !buffer[offset].IsValid) offset++;
                            var length = 0;
                            while (offset < count && buffer[offset].IsValid)
                            {
                                var point = buffer[offset++];
                                _screenPoints[length++] = new Point(MapX(point.Time), MapY(point.Value, yMin, yMax));
                            }
                            if (length == 0)
                            {
                                continue;
                            }

                            length = PlotGeometryPolicy.Simplify(_screenPoints.AsSpan(0, length));
                            geometryContext.BeginFigure(_screenPoints[0], false);
                            for (var i = 1; i < length; i++)
                            {
                                geometryContext.LineTo(_screenPoints[i], true);
                            }
                            geometryContext.EndFigure(false);
                        }
                    }

                    context.DrawGeometry(null, channel.Pen, geometry);
                }
            }
        }
    }

    private void DrawTicks(DrawingContext context, double yMin, double yMax)
    {
        const int xTicks = 6;
        var span = ViewEnd - ViewStart;
        for (var i = 0; i <= xTicks; i++)
        {
            var time = ViewStart + span * i / xTicks;
            var x = _plotRect.X + _plotRect.Width * i / xTicks;
            var text = Text(FormatAxisTime(time), 10.5, CurrentPalette.LabelBrush);
            var textX = ClampSafe(x - text.Width / 2, _plotRect.Left, _plotRect.Right - text.Width);
            context.DrawText(text, new Point(textX, _plotRect.Bottom + 8));
        }

        const int yTicks = 4;
        for (var i = 0; i <= yTicks; i++)
        {
            var fraction = (double)i / yTicks;
            var value = yMax * (1 - fraction) + yMin * fraction;
            var y = _plotRect.Y + _plotRect.Height * i / yTicks;
            var text = Text(FormatAxisValue(value, yMax - yMin), 10.5, CurrentPalette.LabelBrush, mono: true);
            context.DrawText(text, new Point(_plotRect.Left - text.Width - 10, y - text.Height / 2));
        }

        var timeLabel = Text(Localization.Get("TimeAxis"), 9.5, CurrentPalette.MutedBrush);
        context.DrawText(timeLabel, new Point(_plotRect.Right - timeLabel.Width, Bounds.Height - 16));

        var valueLabel = Text(Localization.Get("ValueAxis"), 9.5, CurrentPalette.MutedBrush);
        context.DrawText(valueLabel, new Point(10, 4));
    }

    private void DrawLegend(DrawingContext context, ChannelViewModel[] channels)
    {
        if (channels.Length == 0)
        {
            return;
        }

        var entries = channels.Take(12).Select(channel =>
        {
            var text = Text(channel.Name, 10.5, CurrentPalette.LabelBrush);
            return (Channel: channel, Text: text);
        }).ToArray();

        var x = _plotRect.Left + 12;
        var y = _plotRect.Top + 12;
        var lineHeight = 18.0;
        var maxWidth = _plotRect.Width - 24;
        var rows = new List<List<(ChannelViewModel Channel, FormattedText Text)>>();
        var row = new List<(ChannelViewModel Channel, FormattedText Text)>();
        var rowWidth = 0.0;
        foreach (var entry in entries)
        {
            var itemWidth = entry.Text.Width + 30;
            if (row.Count > 0 && rowWidth + itemWidth > maxWidth)
            {
                rows.Add(row);
                row = [];
                rowWidth = 0;
            }

            row.Add(entry);
            rowWidth += itemWidth;
        }

        if (row.Count > 0)
        {
            rows.Add(row);
        }

        var panelHeight = rows.Count * lineHeight + 12;
        var panelWidth = Math.Min(maxWidth, rows.Max(r => r.Sum(item => item.Text.Width + 30)) + 12);
        context.FillRectangle(CurrentPalette.LegendBackground, new Rect(x, y, panelWidth, panelHeight), 5);

        var drawY = y + 6;
        foreach (var legendRow in rows)
        {
            var drawX = x + 8;
            foreach (var entry in legendRow)
            {
                context.FillRectangle(entry.Channel.ColorBrush, new Rect(drawX, drawY + 4, 8, 8), 2);
                context.DrawText(entry.Text, new Point(drawX + 13, drawY));
                drawX += entry.Text.Width + 30;
            }

            drawY += lineHeight;
        }
    }

    private void DrawCrosshair(DrawingContext context, ChannelViewModel[] channels, double yMin, double yMax)
    {
        if (channels.Length == 0 || !_plotRect.Contains(_pointer))
        {
            return;
        }

        var span = ViewEnd - ViewStart;
        var time = ViewStart + (_pointer.X - _plotRect.X) / Math.Max(1, _plotRect.Width) * span;
        var x = Math.Clamp(_pointer.X, _plotRect.Left, _plotRect.Right);

        using (context.PushClip(_plotRect))
        {
            context.DrawLine(CurrentPalette.CrosshairPen, new Point(x, _plotRect.Top), new Point(x, _plotRect.Bottom));
        }

        var rows = new List<(ChannelViewModel Channel, double Value, double Time)>();
        var maxLabelWidth = 120.0;
        var maxValueWidth = 60.0;

        foreach (var channel in channels)
        {
            if (!channel.Buffer.TryGetNearest(time, ViewStart, ViewEnd, out var point))
            {
                continue;
            }

            if (point.IsValid)
            {
                var y = MapY(point.Value, yMin, yMax);
                context.DrawEllipse(channel.ColorBrush, null, new Point(x, y), 2.6, 2.6);
            }
            rows.Add((channel, point.Value, point.Time));
            maxLabelWidth = Math.Max(maxLabelWidth, MeasureWidth(channel.Name, mono: false));
            maxValueWidth = Math.Max(maxValueWidth, MeasureWidth(channel.FormatValue(point.Value), mono: true));
        }

        var maxRows = Math.Max(2, (int)((_plotRect.Height - 40) / 18));
        var shownRows = rows.Count <= maxRows
            ? rows
            : rows.GetRange(0, maxRows - 1);
        var hiddenRows = rows.Count - shownRows.Count;
        var rowLines = Math.Max(1, shownRows.Count + (hiddenRows > 0 ? 1 : 0));
        var boxWidth = Math.Min(_plotRect.Width - 20, Math.Max(180, maxLabelWidth + maxValueWidth + 48));
        var boxHeight = 26 + rowLines * 18;
        var boxX = x + 16;
        if (boxX + boxWidth > _plotRect.Right - 8)
        {
            boxX = x - boxWidth - 16;
        }

        boxX = ClampSafe(boxX, _plotRect.Left + 8, _plotRect.Right - boxWidth - 8);
        var boxY = ClampSafe(_pointer.Y + 12, _plotRect.Top + 8, _plotRect.Bottom - boxHeight - 8);
        var boxRect = new Rect(boxX, boxY, boxWidth, boxHeight);
        context.FillRectangle(CurrentPalette.TooltipBackground, boxRect, 6);
        context.DrawRectangle(CurrentPalette.TooltipBorderPen, boxRect, 6);

        var timeText = Text($"t = {FormatTooltipTime(time)}", 10.5, CurrentPalette.LabelBrush);
        context.DrawText(timeText, new Point(boxX + 12, boxY + 7));

        var rowY = boxY + 26;
        if (rows.Count == 0)
        {
            context.DrawText(Text(Localization.Get("PlotNoSamples"), 10.5, CurrentPalette.MutedBrush),
                new Point(boxX + 12, rowY));
            return;
        }

        foreach (var row in shownRows)
        {
            context.FillRectangle(row.Channel.ColorBrush, new Rect(boxX + 12, rowY + 4, 7, 7), 2);
            context.DrawText(Text(row.Channel.Name, 10.5, CurrentPalette.LabelBrush), new Point(boxX + 25, rowY));
            var valueText = Text(row.Channel.FormatValue(row.Value), 10.5, CurrentPalette.ValueTextBrush, mono: true);
            context.DrawText(valueText, new Point(boxRect.Right - valueText.Width - 12, rowY));
            rowY += 18;
        }

        if (hiddenRows > 0)
        {
            context.DrawText(Text($"+{hiddenRows} more", 10.5, CurrentPalette.MutedBrush), new Point(boxX + 12, rowY));
        }
    }

    private void DrawEmptyState(DrawingContext context, string message)
    {
        var text = Text(message, 13, CurrentPalette.EmptyBrush);
        context.DrawText(text, new Point(
            _plotRect.X + (_plotRect.Width - text.Width) / 2,
            _plotRect.Y + (_plotRect.Height - text.Height) / 2));
    }

    private double MeasureWidth(string text, bool mono)
    {
        var cache = mono ? _valueWidths : _labelWidths;
        if (!cache.TryGetValue(text, out var width))
        {
            if (cache.Count >= 4096)
            {
                cache.Clear();
            }
            width = Text(text, 10.5, mono ? CurrentPalette.ValueTextBrush : CurrentPalette.LabelBrush, mono).Width;
            cache[text] = width;
        }

        return width;
    }

    private double MapX(double time) =>
        _plotRect.X + (time - ViewStart) / Math.Max(0.000001, ViewEnd - ViewStart) * _plotRect.Width;

    private double MapY(double value, double yMin, double yMax)
    {
        // Normalize before subtracting so opposite, very large finite values
        // never overflow into an infinite coordinate at the native boundary.
        var scale = Math.Max(1, Math.Max(Math.Abs(yMin), Math.Abs(yMax)));
        var range = yMax / scale - yMin / scale;
        var ratio = (value / scale - yMin / scale) / Math.Max(double.Epsilon, range);
        return _plotRect.Bottom - Math.Clamp(ratio, -1e6, 1e6) * _plotRect.Height;
    }

    private static double ClampSafe(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);

    private static FormattedText Text(string text, double size, IBrush brush, bool mono = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mono ? MonoTypeface : UiTypeface, size, brush);

    private static string FormatAxisTime(double seconds)
    {
        if (seconds >= 3600)
        {
            return TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        }

        if (seconds >= 60)
        {
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }

        if (seconds >= 10)
        {
            return $"{seconds:0}s";
        }

        if (seconds >= 1)
        {
            return $"{seconds:0.0}s";
        }

        return $"{seconds * 1000:0}ms";
    }

    private static string FormatTooltipTime(double seconds) =>
        seconds >= 60
            ? TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture)
            : $"{seconds:0.000}s";

    private static string FormatAxisValue(double value, double range)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000)
        {
            return $"{value / 1_000_000:0.##}M";
        }

        if (abs >= 10_000)
        {
            return $"{value / 1000:0.#}k";
        }

        if (range < 1)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (range < 10)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return value.ToString("0", CultureInfo.InvariantCulture);
    }

    private sealed class PlotPalette
    {
        public PlotPalette(
            string background,
            string panel,
            string grid,
            string gridStrong,
            string axis,
            string label,
            string muted,
            string crosshair,
            string tooltipBackground,
            string legendBackground,
            string empty,
            string tooltipBorder,
            string valueText)
        {
            PlotBackground = Brush(background);
            PlotPanel = Brush(panel);
            GridBrush = Brush(grid);
            GridStrongBrush = Brush(gridStrong);
            AxisBrush = Brush(axis);
            LabelBrush = Brush(label);
            MutedBrush = Brush(muted);
            CrosshairBrush = Brush(crosshair);
            TooltipBackground = Brush(tooltipBackground);
            LegendBackground = Brush(legendBackground);
            EmptyBrush = Brush(empty);
            ValueTextBrush = Brush(valueText);
            GridPen = new Pen(GridBrush, 1);
            GridStrongPen = new Pen(GridStrongBrush, 1);
            AxisPen = new Pen(AxisBrush, 1);
            CrosshairPen = new Pen(CrosshairBrush, 1, new DashStyle([4, 4], 0));
            TooltipBorderPen = new Pen(Brush(tooltipBorder), 1);
        }

        public IBrush PlotBackground { get; }
        public IBrush PlotPanel { get; }
        public IBrush GridBrush { get; }
        public IBrush GridStrongBrush { get; }
        public IBrush AxisBrush { get; }
        public IBrush LabelBrush { get; }
        public IBrush MutedBrush { get; }
        public IBrush CrosshairBrush { get; }
        public IBrush TooltipBackground { get; }
        public IBrush LegendBackground { get; }
        public IBrush EmptyBrush { get; }
        public IBrush ValueTextBrush { get; }
        public Pen GridPen { get; }
        public Pen GridStrongPen { get; }
        public Pen AxisPen { get; }
        public Pen CrosshairPen { get; }
        public Pen TooltipBorderPen { get; }

        private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    }
}
