using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NewScope.ViewModels;

public sealed partial class VariableNode : ObservableObject, IDisposable
{
    private readonly Action<VariableNode>? _toggleGroupSelection;
    private bool _disposed;

    private VariableNode(
        string name,
        string fullName,
        SymbolItemViewModel? item,
        Action<VariableNode>? toggleGroupSelection = null)
    {
        Name = name;
        FullName = fullName;
        Item = item;
        _toggleGroupSelection = toggleGroupSelection;
        if (item is not null)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }
    }

    public static VariableNode CreateGroup(
        string name,
        string fullName,
        Action<VariableNode>? toggleGroupSelection = null) =>
        new(name, fullName, null, toggleGroupSelection);

    public static VariableNode CreateLeaf(string name, string fullName, SymbolItemViewModel item) =>
        new(name, fullName, item);

    public string Name { get; }
    public string FullName { get; }
    public SymbolItemViewModel? Item { get; }
    public bool IsGroup => Item is null;
    public bool IsLeaf => Item is not null;
    public bool IsSelected
    {
        get => Item?.IsSelected == true;
        set
        {
            if (Item is { } item)
            {
                item.IsSelected = value;
            }
        }
    }

    public IBrush AccentBrush => Item?.AccentBrush ?? Brushes.Transparent;
    public string TypeText => Item?.TypeText ?? string.Empty;
    public string AddressText => Item?.AddressText ?? string.Empty;
    public ObservableCollection<VariableNode> Children { get; } = [];
    public int LeafCount { get; private set; }
    public string CountText => LeafCount == 1 ? "1 variable" : $"{LeafCount} variables";
    public string Tooltip => Item?.Tooltip ?? FullName;

    [ObservableProperty]
    private bool _isExpanded = true;

    public string Glyph => IsExpanded ? "▾" : "▸";
    public bool AllSelected => IsLeaf
        ? Item?.IsSelected == true
        : Children.Count > 0 && Children.All(child => child.AllSelected);
    public string GroupSelectionGlyph => AllSelected ? "✓" : "+";
    public string GroupSelectionTooltip => Services.Localization.Get("GroupSelectTooltip");

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Glyph));
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SymbolItemViewModel.IsSelected) or nameof(SymbolItemViewModel.AccentBrush))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void ToggleGroupSelection() => _toggleGroupSelection?.Invoke(this);

    public void RefreshSelectionVisuals()
    {
        if (IsGroup)
        {
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(GroupSelectionGlyph));
            OnPropertyChanged(nameof(GroupSelectionTooltip));
            foreach (var child in Children)
            {
                child.RefreshSelectionVisuals();
            }
        }
    }

    public int UpdateLeafCount()
    {
        LeafCount = IsLeaf ? 1 : Children.Sum(child => child.UpdateLeafCount());
        OnPropertyChanged(nameof(LeafCount));
        OnPropertyChanged(nameof(CountText));
        return LeafCount;
    }

    public bool HasSelectedLeaf() =>
        IsLeaf ? Item?.IsSelected == true : Children.Any(child => child.HasSelectedLeaf());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Item is { } item)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }

        foreach (var child in Children)
        {
            child.Dispose();
        }
    }
}
