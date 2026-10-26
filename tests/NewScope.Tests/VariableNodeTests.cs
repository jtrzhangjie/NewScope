using Avalonia.Media;
using NewScope.Models;
using NewScope.ViewModels;

namespace NewScope.Tests;

public sealed class VariableNodeTests
{
    [Fact]
    public void Leaf_proxies_selection_and_presentation()
    {
        var item = new SymbolItemViewModel(
            new SymbolVariable
            {
                Name = "steppCtrl.TargetSpeed",
                Address = 0x10000BF4,
                Size = 4,
                Type = "int32_t",
                Signed = true,
                Kind = "int"
            },
            (_, _) => { });
        var node = VariableNode.CreateLeaf("TargetSpeed", item.Name, item);
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(node.IsSelected);
        node.IsSelected = true;
        Assert.True(item.IsSelected);

        item.SetSelectedSilently(false, Brushes.Red);
        Assert.False(node.IsSelected);
        Assert.Same(Brushes.Red, node.AccentBrush);
        Assert.Equal(item.TypeText, node.TypeText);
        Assert.Equal(item.AddressText, node.AddressText);
        Assert.Contains(nameof(VariableNode.IsSelected), changed);
        Assert.Contains(nameof(VariableNode.AccentBrush), changed);
    }

    [Fact]
    public void Group_has_safe_empty_presentation()
    {
        var node = VariableNode.CreateGroup("Commut", "Commut");

        Assert.True(node.IsGroup);
        Assert.False(node.IsSelected);
        node.IsSelected = true;
        Assert.False(node.IsSelected);
        Assert.Empty(node.TypeText);
        Assert.Empty(node.AddressText);
        Assert.Same(Brushes.Transparent, node.AccentBrush);
    }

    [Fact]
    public void DisposingATreeUnsubscribesEveryLeaf()
    {
        var item = new SymbolItemViewModel(new SymbolVariable { Name = "group.signal" }, (_, _) => { });
        var root = VariableNode.CreateGroup("group", "group");
        var leaf = VariableNode.CreateLeaf("signal", item.Name, item);
        root.Children.Add(leaf);
        var notifications = 0;
        leaf.PropertyChanged += (_, _) => notifications++;

        root.Dispose();
        root.Dispose();
        item.IsSelected = true;
        item.SetSelectedSilently(false, Brushes.Red);

        Assert.Equal(0, notifications);
        using var replacement = VariableNode.CreateLeaf("signal", item.Name, item);
        replacement.PropertyChanged += (_, _) => notifications++;
        item.IsSelected = true;
        Assert.Equal(1, notifications);
    }
}
