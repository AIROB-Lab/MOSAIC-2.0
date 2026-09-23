using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Factory;

namespace MOSAIC.ViewModels.Palette;

public sealed record BlockCategoryGroup(
    BlockCategory Category,
    string DisplayName,
    IReadOnlyList<BlockDescriptor> Blocks);

public partial class BlockPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<BlockCategoryGroup> _allGroups;

    [ObservableProperty]
    private string _searchText = "";

    public ObservableCollection<BlockCategoryGroup> FilteredGroups { get; } = new();

    public BlockPaletteViewModel()
    {
        _allGroups = BlockCatalog.All
            .GroupBy(d => d.Category)
            .OrderBy(g => g.Key)
            .Select(g => new BlockCategoryGroup(
                g.Key,
                CategoryLabel(g.Key),
                g.OrderBy(d => d.DisplayName).ToList()))
            .ToList();

        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredGroups.Clear();

        var query = SearchText?.Trim() ?? "";

        foreach (var group in _allGroups)
        {
            var matches = string.IsNullOrEmpty(query)
                ? group.Blocks
                : (IReadOnlyList<BlockDescriptor>)group.Blocks
                    .Where(b => b.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || b.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (matches.Count > 0)
                FilteredGroups.Add(new BlockCategoryGroup(group.Category, group.DisplayName, matches));
        }
    }

    private static string CategoryLabel(BlockCategory cat) => cat switch
    {
        BlockCategory.Analytics        => "Analytics",
        BlockCategory.Devices          => "Devices",
        BlockCategory.FlowControl      => "Flow Control",
        BlockCategory.MachineLearning  => "Machine Learning",
        BlockCategory.SignalProcessing => "Signal Processing",
        BlockCategory.Streaming        => "Streaming",
        BlockCategory.Tests            => "Tests",
        _                              => cat.ToString()
    };
}
