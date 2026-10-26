using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewScope.Models;

namespace NewScope.ViewModels;

public sealed partial class ChannelViewModel : ObservableObject, ISampleChannel
{
    private static readonly string[] Palette =
    [
        "#4DD0E1", "#FFCA28", "#7ED957", "#FF6B9A", "#B388FF",
        "#5B9DFF", "#FF9F43", "#FF5C5C", "#2DD4BF", "#A3E635",
        "#F472B6", "#38BDF8"
    ];

    private readonly Action<ChannelViewModel>? _movePane;
    private int _paneIndex;

    public ChannelViewModel(
        SymbolVariable symbol,
        int paletteIndex,
        Action<ChannelViewModel>? movePane = null,
        int capacity = 200_000)
    {
        Data = new SampleChannel(symbol, capacity);
        ColorHex = Palette[paletteIndex % Palette.Length];
        ColorBrush = new SolidColorBrush(Color.Parse(ColorHex));
        Pen = new Pen(ColorBrush, 1.7);
        _movePane = movePane;
    }

    public SampleChannel Data { get; }
    public SymbolVariable Symbol => Data.Symbol;
    public ChannelBuffer Buffer => Data.Buffer;
    public string ColorHex { get; }
    public IBrush ColorBrush { get; }
    public Pen Pen { get; }
    public string Name => Symbol.Name;
    public string TypeText => Symbol.Type;
    public string AddressText => Symbol.AddressText;
    public string Tooltip => Symbol.Tooltip;

    public int PaneIndex
    {
        get => _paneIndex;
        set
        {
            if (SetProperty(ref _paneIndex, value))
            {
                OnPropertyChanged(nameof(PaneLabel));
            }
        }
    }

    public string PaneLabel => PaneIndex switch
    {
        1 => "B",
        2 => "C",
        3 => "D",
        _ => "A"
    };

    [RelayCommand]
    private void MovePane() => _movePane?.Invoke(this);

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string _valueText = "--";

    [ObservableProperty]
    private string _minText = "--";

    [ObservableProperty]
    private string _maxText = "--";

    [ObservableProperty]
    private string _avgText = "--";

    [ObservableProperty]
    private string _countText = "0";

    public void UpdateStats()
    {
        var stats = Buffer.GetStats();
        if (stats.Count == 0)
        {
            ValueText = "--";
            MinText = "--";
            MaxText = "--";
            AvgText = "--";
            CountText = "0";
            return;
        }

        ValueText = Format(stats.Last);
        MinText = Format(stats.Min);
        MaxText = Format(stats.Max);
        AvgText = Format(stats.Average);
        CountText = stats.Count.ToString("N0", CultureInfo.CurrentCulture);
    }

    public void Clear()
    {
        Buffer.Clear();
        UpdateStats();
    }

    public string FormatValue(double value) => Format(value);

    private string Format(double value)
    {
        if (!double.IsFinite(value))
        {
            return "--";
        }

        return Symbol.Kind switch
        {
            "bool" => value == 0 ? "0" : "1",
            "int" => Math.Round(value).ToString("0", CultureInfo.CurrentCulture),
            _ => value.ToString("0.###", CultureInfo.CurrentCulture)
        };
    }
}

public sealed partial class SymbolItemViewModel : ObservableObject
{
    private readonly Action<SymbolItemViewModel, bool> _selectionChanged;
    private bool _suppress;

    public SymbolItemViewModel(SymbolVariable symbol, Action<SymbolItemViewModel, bool> selectionChanged)
    {
        Symbol = symbol;
        _selectionChanged = selectionChanged;
    }

    public SymbolVariable Symbol { get; }
    public string Name => Symbol.Name;
    public string TypeText => Symbol.Type;
    public string AddressText => Symbol.AddressText;
    public string Tooltip => Symbol.Tooltip;
    public string SearchText => $"{Symbol.Name} {Symbol.Type} {Symbol.AddressText}";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private IBrush _accentBrush = Brushes.Transparent;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppress)
        {
            _selectionChanged(this, value);
        }
    }

    public void SetSelectedSilently(bool value, IBrush? accent = null)
    {
        _suppress = true;
        IsSelected = value;
        AccentBrush = accent ?? Brushes.Transparent;
        _suppress = false;
    }
}

public sealed class PaneOption(int index, string label)
{
    public int Index { get; } = index;
    public string Label { get; } = label;
}

public sealed class PaneCountOption(int count)
{
    public int Count { get; } = count;
    public string Label { get; } = count.ToString(CultureInfo.InvariantCulture);
}

public sealed class SpeedOption(int speedKHz, bool isMax = false)
{
    public int KHz { get; } = speedKHz;
    public bool IsMax { get; } = isMax;
    public string Label => IsMax
        ? $"{KHz / 1000.0:0.#} MHz MAX"
        : $"{KHz / 1000.0:0.#} MHz";
}

public sealed class RateOption(int hz)
{
    public int Hz { get; } = hz;
    public string Label => $"{Hz} Hz";
}

public sealed class WindowOption(double seconds)
{
    public double Seconds { get; } = seconds;
    public string Label => Seconds < 1 ? $"{Seconds * 1000:0} ms" : $"{Seconds:0.#} s";
}
