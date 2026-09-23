using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Enums;

namespace MOSAIC.ViewModels.Graph;

/// <summary>
/// Represents a collapsible group of vertices (subsystem).
/// When collapsed, all member vertices are hidden and replaced by a single group node.
/// The border colour reflects the aggregate status of all member blocks.
/// </summary>
public partial class VertexGroupViewModel : ObservableObject
{
    #region Observable properties

    [ObservableProperty] private string name = "Subsystem";
    [ObservableProperty] private float x;
    [ObservableProperty] private float y;
    [ObservableProperty] private float width = 180;
    [ObservableProperty] private float height = 70;
    [ObservableProperty] private bool isSelected;

    /// <summary>
    /// The worst BlockStatus across all member blocks.
    /// Error > Warning > Normal > Idle.
    /// Drives the group node's border colour via StatusToColorConverter.
    /// </summary>
    [ObservableProperty] private BlockStatus aggregateStatus = BlockStatus.Idle;

    #endregion

    #region Members

    /// <summary>
    /// The vertices contained in this group.
    /// </summary>
    public List<VertexViewModel> Members { get; } = new();

    /// <summary>
    /// The actual BaseBlock instances for rendering in the flyout panel
    /// via BlockTemplateSelector. Populated when members are added.
    /// </summary>
    public ObservableCollection<object> MemberBlocks { get; } = new();

    /// <summary>
    /// Saved positions of members so they can be restored on ungroup.
    /// </summary>
    public Dictionary<VertexViewModel, (float X, float Y)> SavedPositions { get; } = new();

    /// <summary>Number of blocks in this group.</summary>
    public int BlockCount => Members.Count;

    /// <summary>Parent group (for nested groups). Null if at root level.</summary>
    public VertexGroupViewModel? Parent { get; set; }

    /// <summary>Child groups contained within this group.</summary>
    public List<VertexGroupViewModel> ChildGroups { get; } = new();

    #endregion

    #region Proxy vertex (for edge routing)

    /// <summary>
    /// A hidden VertexViewModel that stands in for the group in the edge graph.
    /// </summary>
    public VertexViewModel? ProxyVertex { get; set; }

    /// <summary>
    /// Keeps the proxy vertex position in sync with the group.
    /// </summary>
    public void SyncProxyPosition()
    {
        if (ProxyVertex is null) return;
        ProxyVertex.X = X;
        ProxyVertex.Y = Y;
        ProxyVertex.Width = Width;
        ProxyVertex.Height = Height;
    }

    #endregion

    #region Status monitoring

    private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _statusSubscriptions = new();

    /// <summary>
    /// Subscribes to all member blocks' PropertyChanged to track Status changes.
    /// Call once after Members are populated.
    /// </summary>
    public void StartStatusMonitoring()
    {
        StopStatusMonitoring();

        foreach (var member in Members)
        {
            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(Components.Basics.BaseBlock.Status))
                    RefreshAggregateStatus();
            };
            member.Value.PropertyChanged += handler;
            _statusSubscriptions.Add((member.Value, handler));
        }

        RefreshAggregateStatus();
    }

    /// <summary>
    /// Unsubscribes from all member block status change events.
    /// </summary>
    public void StopStatusMonitoring()
    {
        foreach (var (source, handler) in _statusSubscriptions)
            source.PropertyChanged -= handler;
        _statusSubscriptions.Clear();
    }

    /// <summary>
    /// Severity ranking for each BlockStatus. Higher = more concerning.
    /// Stumbling (red) > Lagging (amber) > Normal (green) > Idle (gray).
    /// </summary>
    private static int Severity(BlockStatus s) => s switch
    {
        BlockStatus.Stumbling => 3,
        BlockStatus.Lagging   => 2,
        BlockStatus.Normal    => 1,
        _                     => 0  // Idle
    };

    /// <summary>
    /// Recomputes AggregateStatus from all member blocks.
    /// The worst (most severe) status wins.
    /// </summary>
    public void RefreshAggregateStatus()
    {
        if (Members.Count == 0)
        {
            AggregateStatus = BlockStatus.Idle;
            return;
        }

        var worst = BlockStatus.Idle;
        int worstSev = 0;
        foreach (var member in Members)
        {
            var s = member.Value.Status;
            var sev = Severity(s);
            if (sev > worstSev)
            {
                worstSev = sev;
                worst = s;
            }
        }

        AggregateStatus = worst;
    }

    #endregion

    #region Content helpers

    /// <summary>
    /// Summary text showing the types of blocks inside.
    /// </summary>
    public string ContentSummary
    {
        get
        {
            if (Members.Count == 0) return "Empty";
            var types = Members
                .Select(m => m.Value.GetType().Name.Replace("Block", ""))
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
            return string.Join(", ", types);
        }
    }

    #endregion
}