using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.ViewModels.Graph;

/// <summary>Visual state of a single input port slot on a block node.</summary>
public enum PortState
{
    /// <summary>Wired to an upstream block.</summary>
    Connected,
    /// <summary>Empty and still required to satisfy <see cref="VertexViewModel.MinInputs"/>.</summary>
    Required,
    /// <summary>Empty and optional (block already meets its minimum).</summary>
    Optional
}

/// <summary>One input port slot, by index, with its current state.</summary>
public readonly record struct InputPortInfo(int Index, PortState State);

public partial class VertexViewModel(BaseBlock block) : ObservableObject
{
    public BaseBlock Value { get; } = block;
    [ObservableProperty] private float x;
    [ObservableProperty] private float y;
    [ObservableProperty] private float width  = 132;
    [ObservableProperty] private float height = 45;
    [ObservableProperty] private bool isSelected;

    /// <summary>True while a palette drag is over the canvas and this block can connect with the dragged type.</summary>
    [ObservableProperty] private bool isDropCompatible;

    /// <summary>Upstream blocks feeding this one — each neighbour is one input edge.</summary>
    public List<VertexViewModel> Neighbors { get; } = new();

    private BlockConstraints.Inputs Constraints => BlockConstraints.For(Value.GetType());

    public int MinInputs => Constraints.Min;
    public int MaxInputs => Constraints.Max;

    /// <summary>True when the block takes no inputs and can originate a stream.</summary>
    public bool IsSource => Constraints.Max == 0;

    /// <summary>Inputs currently wired (one neighbour = one input edge).</summary>
    public int ConnectedInputs => Neighbors.Count;

    public bool NeedsMoreInputs => ConnectedInputs < MinInputs;
    public int MissingInputs => Math.Max(0, MinInputs - ConnectedInputs);

    /// <summary>Every <see cref="BaseBlock"/> is an <see cref="IPublisher"/>, so it always exposes an output port.</summary>
    public bool HasOutput => Value is IPublisher;

    public BlockCategory Category =>
        BlockCatalog.CategoryByClass.TryGetValue(Value.GetType().Name, out var c) ? c : BlockCategory.FlowControl;

    /// <summary>
    /// When set, overrides the reflected input-port count. Used by group edge-routing proxies, which
    /// aggregate many external inputs and don't follow any single block's constraints.
    /// </summary>
    public int? InputPortCountOverride { get; set; }

    /// <summary>
    /// Number of input port slots to draw: 0 for sources; exactly <see cref="MaxInputs"/> for bounded
    /// blocks; for unbounded blocks (e.g. Joiner) the connected count plus one spare "add" slot.
    /// </summary>
    public int InputPortCount
    {
        get
        {
            if (InputPortCountOverride is { } o) return o;
            var c = Constraints;
            if (c.Max <= 0) return 0;
            if (c.Max == int.MaxValue) return Math.Max(Math.Max(c.Min, ConnectedInputs), 1) + 1;
            return c.Max;
        }
    }

    /// <summary>Per-slot input port states, left-to-top order (connected first, then required, then optional).</summary>
    public IReadOnlyList<InputPortInfo> InputPorts
    {
        get
        {
            int n = InputPortCount, connected = ConnectedInputs, min = MinInputs;
            var list = new List<InputPortInfo>(n);
            for (int i = 0; i < n; i++)
            {
                var state = i < connected ? PortState.Connected
                          : i < min       ? PortState.Required
                          :                 PortState.Optional;
                list.Add(new InputPortInfo(i, state));
            }
            return list;
        }
    }

    /// <summary>Compact "connected / capacity" badge shown on the node.</summary>
    public string ArityBadge =>
        IsSource                    ? "source"
        : MaxInputs == int.MaxValue ? $"{ConnectedInputs} / ∞"
        :                             $"{ConnectedInputs} / {MaxInputs}";

    /// <summary>Human-readable input requirement, for the node's info popup.</summary>
    public string InputSummaryText =>
        IsSource                    ? "No inputs — source block"
        : MinInputs == MaxInputs    ? $"{MinInputs} required · {ConnectedInputs} connected"
        : MaxInputs == int.MaxValue ? $"{MinInputs}+ · {ConnectedInputs} connected"
        :                             $"{MinInputs}–{MaxInputs} · {ConnectedInputs} connected";

    /// <summary>Friendly list of accepted upstream block types (from <c>AllowableBlocks</c>), or "any block".</summary>
    public string AcceptsSummary
    {
        get
        {
            var allow = Constraints.Allowable;
            if (allow.Count == 0) return "any block";
            return string.Join(", ", allow.Select(n =>
                BlockCatalog.DisplayNameByClass.TryGetValue(n, out var dn) ? dn : n));
        }
    }

    public string NeedsText =>
        MissingInputs == 0 ? "" : $"Needs {MissingInputs} more input{(MissingInputs == 1 ? "" : "s")}";

    /// <summary>
    /// Raise change notifications for every input-derived read-only property. Call after mutating
    /// <see cref="Neighbors"/> so the node's ports, badge, and popup refresh (used from Phase 2 wiring).
    /// </summary>
    public void NotifyInputsChanged()
    {
        OnPropertyChanged(nameof(ConnectedInputs));
        OnPropertyChanged(nameof(NeedsMoreInputs));
        OnPropertyChanged(nameof(MissingInputs));
        OnPropertyChanged(nameof(InputPortCount));
        OnPropertyChanged(nameof(InputPorts));
        OnPropertyChanged(nameof(ArityBadge));
        OnPropertyChanged(nameof(InputSummaryText));
        OnPropertyChanged(nameof(NeedsText));
    }
}
