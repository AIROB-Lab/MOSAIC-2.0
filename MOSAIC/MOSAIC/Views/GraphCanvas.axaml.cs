using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MOSAIC.Components.Factory;
using MOSAIC.ViewModels.Graph;
using MOSAIC.Views.Palette;
using VertexViewModel = MOSAIC.ViewModels.Graph.VertexViewModel;

namespace MOSAIC.Views;

public partial class GraphCanvas : UserControl
{
    public static readonly StyledProperty<ObservableCollection<VertexViewModel>?> VerticesProperty =
        AvaloniaProperty.Register<GraphCanvas, ObservableCollection<VertexViewModel>?>(nameof(Vertices));

    public ObservableCollection<VertexViewModel>? Vertices
    {
        get => GetValue(VerticesProperty);
        set => SetValue(VerticesProperty, value);
    }

    #region Core canvas state

    Canvas? _panHost, _root;
    EdgeDrawableControl? _edges;
    readonly Dictionary<VertexViewModel, PropertyChangedEventHandler> _vm = new();
    bool _panning;
    Point _panStart;
    double _ox, _oy;
    double _scale = 1.0;
    const double ScaleMin = 0.05, ScaleMax = 3.0, ScaleFactor = 1.1;

    Size _lastLayoutSize;

    public event EventHandler<VertexViewModel>? NodeClicked;

    /// <summary>Fired when blocks are grouped. Contains the new group.</summary>
    public event EventHandler<VertexGroupViewModel>? GroupCreated;

    /// <summary>Fired when a group is dissolved. Contains the removed group.</summary>
    public event EventHandler<VertexGroupViewModel>? GroupRemoved;

    /// <summary>Fired when a block is dropped from the palette, with the canvas-space drop point.</summary>
    public event EventHandler<(BlockDescriptor Descriptor, Point CanvasPosition)>? BlockDescriptorDropped;

    /// <summary>Fired when the user completes a valid wire from a source block's output to a target block's input.</summary>
    public event EventHandler<(VertexViewModel Source, VertexViewModel Target)>? ConnectionCreated;

    #endregion

    #region Connection drag (build mode)

    // Dragging a wire from a block's output port to another block's input port.
    const double PortHitRadius = 14;   // grab tolerance on the source output port
    const double PortSnapRadius = 26;  // snap distance to a target input port

    bool _connecting;
    VertexViewModel? _connectSource;
    VertexViewModel? _connectTarget;
    bool _connectTargetValid;

    #endregion

    #region Selection state

    readonly HashSet<VertexViewModel> _selectedVertices = new();
    readonly HashSet<VertexGroupViewModel> _selectedGroups = new();

    Border? _selectionRect;
    bool _rubberBanding;
    Point _rubberStart;

    Dictionary<VertexViewModel, Point>? _groupDragOrigins;
    Dictionary<VertexGroupViewModel, Point>? _groupNodeDragOrigins;
    Point _groupDragAnchor;
    bool _groupDragging;

    /// <summary>
    /// Set by node/group press handlers so the canvas-level handler
    /// knows NOT to start rubber-banding.
    /// </summary>
    bool _nodeInteracting;

    #endregion

    #region Grouping state

    readonly List<VertexGroupViewModel> _groups = new();
    readonly Dictionary<VertexGroupViewModel, Control> _groupControls = new();

    /// <summary>
    /// Tracks proxy vertices temporarily added to external vertex Neighbors
    /// lists so we can clean them up before each rebuild.
    /// </summary>
    readonly List<(VertexViewModel Vertex, VertexViewModel Proxy)> _neighborRedirects = new();

    /// <summary>
    /// Tracks original member references temporarily removed from external vertex
    /// Neighbors lists so we can restore them on cleanup.
    /// </summary>
    readonly List<(VertexViewModel Vertex, VertexViewModel Member)> _memberRedirects = new();

    Border? _groupToolbar;
    Button? _groupBtn;
    Button? _ungroupBtn;

    #endregion

    public GraphCanvas()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, __) => Init();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == VerticesProperty)
            {
                if (e.OldValue is ObservableCollection<VertexViewModel> o) o.CollectionChanged -= OnVerts;
                if (e.NewValue is ObservableCollection<VertexViewModel> n) n.CollectionChanged += OnVerts;
                Rebuild();
            }
        };
    }

    void Init()
    {
        _panHost = this.FindControl<Canvas>("PanHost");
        // Pivot the zoom/pan transform at the top-left (0,0). Avalonia defaults to the control's
        // centre, which would offset the graph by (1-scale)*viewportCentre — the fit and the
        // wheel-zoom math both assume screen = scale*point + offset.
        if (_panHost is not null)
            _panHost.RenderTransformOrigin = RelativePoint.TopLeft;
        _root = this.FindControl<Canvas>("RootCanvas");
        _selectionRect = this.FindControl<Border>("SelectionRect");
        _groupToolbar = this.FindControl<Border>("GroupToolbar");
        _groupBtn = this.FindControl<Button>("GroupBtn");
        _ungroupBtn = this.FindControl<Button>("UngroupBtn");

        PointerPressed += OnPress;
        PointerMoved += OnMove;
        PointerReleased += OnRelease;
        PointerWheelChanged += OnWheel;
        SizeChanged += OnSizeChanged;

        // Accept blocks dragged from the palette.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnPaletteDragOver);
        AddHandler(DragDrop.DropEvent, OnPaletteDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnPaletteDragLeave);

        _lastLayoutSize = Bounds.Size;
        Rebuild();
    }

    #region Resize handling

    void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SizeEdges();
        _lastLayoutSize = e.NewSize;

        if (Vertices is null || Vertices.Count == 0) return;
        if (e.NewSize.Width < 100 || e.NewSize.Height < 100) return;

        // Auto fit-to-view: re-frame the whole graph for the new window size. Node positions stay
        // in graph-space; only the viewport transform changes, so nothing overlaps or drifts.
        FitToView();
    }

    #endregion

    #region Connection drag logic

    static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>True when <paramref name="graphPt"/> (graph space) is on the block's output port.</summary>
    bool IsOnOutputPort(VertexViewModel vm, Point graphPt) =>
        vm.HasOutput && Dist(graphPt, PortLayout.OutputAnchor(vm)) <= PortHitRadius;

    void BeginConnection(VertexViewModel source)
    {
        _connecting = true;
        _connectSource = source;
        _connectTarget = null;
        _connectTargetValid = false;
    }

    /// <summary>Recompute the target/validity and update the rubber-band wire for the current cursor.</summary>
    void UpdateConnectionDrag(Point graphPt)
    {
        if (_connectSource is not { } src) return;

        (_connectTarget, _connectTargetValid) = FindInputTarget(src, graphPt);

        var from = PortLayout.OutputAnchor(src);
        Point to = _connectTarget is { } t ? NearestInputAnchor(t, graphPt) : graphPt;
        var kind = _connectTarget is null ? PendingKind.Neutral
                 : _connectTargetValid    ? PendingKind.Valid
                 :                          PendingKind.Invalid;
        _edges?.SetPending(from, to, kind);
    }

    /// <summary>Nearest input port within snap range, plus whether wiring source→it is allowed.</summary>
    (VertexViewModel?, bool) FindInputTarget(VertexViewModel src, Point graphPt)
    {
        VertexViewModel? best = null;
        double bestDist = PortSnapRadius;

        foreach (var v in GetVisibleVertices())
        {
            if (ReferenceEquals(v, src) || v.IsSource) continue;
            int n = v.InputPortCount;
            for (int i = 0; i < n; i++)
            {
                double d = Dist(graphPt, PortLayout.InputAnchor(v, i, n));
                if (d < bestDist) { bestDist = d; best = v; }
            }
        }

        return best is null ? (null, false) : (best, CanConnect(src, best));
    }

    static Point NearestInputAnchor(VertexViewModel v, Point graphPt)
    {
        int n = v.InputPortCount;
        if (n <= 0) return PortLayout.Center(v);
        Point best = PortLayout.InputAnchor(v, 0, n);
        double bd = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var a = PortLayout.InputAnchor(v, i, n);
            double d = Dist(graphPt, a);
            if (d < bd) { bd = d; best = a; }
        }
        return best;
    }

    /// <summary>Whether a wire source→target is allowed (capacity, whitelist, no duplicate, no cycle).</summary>
    static bool CanConnect(VertexViewModel src, VertexViewModel tgt)
    {
        if (ReferenceEquals(src, tgt) || tgt.IsSource) return false;
        if (tgt.MaxInputs != int.MaxValue && tgt.ConnectedInputs >= tgt.MaxInputs) return false; // full
        if (tgt.Neighbors.Contains(src)) return false;                                            // already wired
        if (!BlockConstraints.Accepts(tgt.Value.GetType(), src.Value.GetType().Name)) return false;
        return !CreatesCycle(src, tgt);
    }

    /// <summary>Adding src as tgt's input loops iff tgt already feeds src (tgt in src's upstream closure).</summary>
    static bool CreatesCycle(VertexViewModel src, VertexViewModel tgt)
    {
        var seen = new HashSet<VertexViewModel>();
        var stack = new Stack<VertexViewModel>();
        stack.Push(src);
        while (stack.Count > 0)
        {
            foreach (var up in stack.Pop().Neighbors)
            {
                if (ReferenceEquals(up, tgt)) return true;
                if (seen.Add(up)) stack.Push(up);
            }
        }
        return false;
    }

    void EndConnectionDrag(bool commit)
    {
        if (commit && _connectSource is { } src && _connectTarget is { } tgt && _connectTargetValid)
            ConnectionCreated?.Invoke(this, (src, tgt));

        _connecting = false;
        _connectSource = null;
        _connectTarget = null;
        _connectTargetValid = false;
        _edges?.ClearPending();
        _edges?.InvalidateVisual();
    }

    #endregion

    #region Palette drop

    // Live preview while dragging a block from the palette flyout onto the canvas.
    Border? _ghost;
    const double SnapGrid = 20;
    const double GhostW = 132, GhostH = 45;   // default node size

    void OnPaletteDragOver(object? sender, DragEventArgs e)
    {
        bool isBlock = e.DataTransfer.Formats.Contains(BlockPaletteView.BlockKeyFormat);
        e.DragEffects = isBlock ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (isBlock && BlockPaletteView.PendingDescriptor is { } d && _root is not null)
        {
            var pos = ResolveDropPosition(e.GetPosition(_root));
            ShowGhost(d, pos);
            UpdateDropHighlights(d);
        }
        else
        {
            ClearPalettePreview();
        }
    }

    void OnPaletteDrop(object? sender, DragEventArgs e)
    {
        ClearPalettePreview();
        if (BlockPaletteView.PendingDescriptor is not { } descriptor || _root is null) return;

        // Same snap/overlap resolution the ghost showed, so the block lands where it was previewed.
        var pos = ResolveDropPosition(e.GetPosition(_root));
        BlockDescriptorDropped?.Invoke(this, (descriptor, pos));
        e.Handled = true;
    }

    void OnPaletteDragLeave(object? sender, DragEventArgs e) => ClearPalettePreview();

    static double Snap(double v) => Math.Round(v / SnapGrid) * SnapGrid;

    /// <summary>Snaps the drop point (centred on the cursor) to the grid, then nudges downward off
    /// any existing node so a fresh block never lands on top of another.</summary>
    Point ResolveDropPosition(Point cursorGraph)
    {
        double x = Snap(cursorGraph.X - GhostW / 2);
        double y = Snap(cursorGraph.Y - GhostH / 2);
        var rect = new Rect(x, y, GhostW, GhostH);
        int guard = 0;
        while (OverlapsExisting(rect) && guard++ < 200)
        {
            y += SnapGrid;
            rect = new Rect(x, y, GhostW, GhostH);
        }
        return new Point(x, y);
    }

    bool OverlapsExisting(Rect r)
    {
        foreach (var v in GetVisibleVertices())
        {
            double w = v.Width > 0 ? v.Width : GhostW;
            double h = v.Height > 0 ? v.Height : GhostH;
            if (r.Intersects(new Rect(v.X, v.Y, w, h).Inflate(6))) return true;
        }
        return false;
    }

    void ShowGhost(BlockDescriptor d, Point pos)
    {
        if (_root is null) return;
        if (_ghost is null || !_root.Children.Contains(_ghost))
        {
            _ghost = new Border
            {
                Width = GhostW,
                Height = GhostH,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.Parse("#4A9EF5")),
                Background = new SolidColorBrush(Color.Parse("#264A9EF5")),
                IsHitTestVisible = false,
                Opacity = 0.85,
                ZIndex = 10000,
                Child = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#4A9EF5"))
                }
            };
            _root.Children.Add(_ghost);
        }

        if (_ghost.Child is TextBlock tb) tb.Text = d.DisplayName;
        Canvas.SetLeft(_ghost, pos.X);
        Canvas.SetTop(_ghost, pos.Y);
    }

    void UpdateDropHighlights(BlockDescriptor d)
    {
        foreach (var v in GetVisibleVertices())
            v.IsDropCompatible = IsPaletteCompatible(d, v);
    }

    /// <summary>Whether the dragged palette type <paramref name="d"/> could connect with existing block
    /// <paramref name="e"/> in either direction (feed it, or be fed by it), honouring type and capacity.</summary>
    static bool IsPaletteCompatible(BlockDescriptor d, VertexViewModel e)
    {
        // e → d : the new block accepts e as an input (and isn't a source).
        bool eIntoD = !BlockConstraints.CanBeSource(d.BlockType)
                   && BlockConstraints.Accepts(d.BlockType, e.Value.GetType().Name);
        // d → e : e accepts the new block and has a free input slot.
        bool dIntoE = !e.IsSource
                   && (e.MaxInputs == int.MaxValue || e.ConnectedInputs < e.MaxInputs)
                   && BlockConstraints.Accepts(e.Value.GetType(), d.BlockType.Name);
        return eIntoD || dIntoE;
    }

    void ClearPalettePreview()
    {
        if (_ghost is not null)
        {
            _root?.Children.Remove(_ghost);
            _ghost = null;
        }
        if (Vertices is not null)
            foreach (var v in Vertices) v.IsDropCompatible = false;
    }

    #endregion

    #region Collection change / rebuild

    void OnVerts(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (_root is null) return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Rebuild();
            return;
        }

        if (e.OldItems != null)
            foreach (VertexViewModel v in e.OldItems) RemoveNode(v);
        if (e.NewItems != null)
            foreach (VertexViewModel v in e.NewItems) AddNode(v);
        _edges?.InvalidateVisual();
    }

    void Rebuild()
    {
        if (_root is null) return;

        // ── Clean up ──
        CleanUpProxyRedirects();
        foreach (var kv in _vm.ToArray()) kv.Key.PropertyChanged -= kv.Value;
        _vm.Clear();
        _root.Children.Clear();
        _ghost = null;                 // was a child of _root, just cleared
        _groupControls.Clear();
        ClearSelection();

        // ── Determine visibility ──
        var visible = GetVisibleVertices();
        var visibleGroups = _groups.Where(g => g.Parent is null).ToList();
        bool hasGroups = visibleGroups.Count > 0;

        // ── Wire proxy edges for groups ──
        WireGroupProxies(visible, visibleGroups);

        // ── Build edge collection ──
        // No groups → pass original Vertices directly (preserves original behavior)
        // Groups active → build filtered collection with proxies
        ObservableCollection<VertexViewModel> edgeCollection;
        if (!hasGroups && Vertices is not null)
        {
            edgeCollection = Vertices;
        }
        else
        {
            var edgeVertices = new List<VertexViewModel>(visible);
            foreach (var g in visibleGroups)
                if (g.ProxyVertex != null)
                    edgeVertices.Add(g.ProxyVertex);
            edgeCollection = new ObservableCollection<VertexViewModel>(edgeVertices);
        }

        _edges = new EdgeDrawableControl
        {
            Vertices = edgeCollection,
            IsHitTestVisible = false
        };
        _root.Children.Add(_edges);
        Canvas.SetLeft(_edges, 0);
        Canvas.SetTop(_edges, 0);
        _edges.ZIndex = 0;
        SizeEdges();

        // ── Add visible vertex controls ──
        foreach (var v in visible) AddNode(v);

        // ── Add visible group node controls ──
        foreach (var g in visibleGroups) AddGroupNode(g);

        _edges.InvalidateVisual();
        UpdateToolbar();
        FitToView();
    }

    /// <summary>
    /// Returns vertices that are NOT inside any collapsed group.
    /// </summary>
    List<VertexViewModel> GetVisibleVertices()
    {
        if (Vertices is null) return new List<VertexViewModel>();

        var grouped = new HashSet<VertexViewModel>();
        foreach (var g in _groups)
            foreach (var m in g.Members)
                grouped.Add(m);
        return Vertices.Where(v => !grouped.Contains(v)).ToList();
    }

    #endregion

    #region Proxy edge wiring

    /// <summary>
    /// For each visible group, creates/updates a proxy VertexViewModel and wires
    /// external edges so EdgeDrawableControl draws arrows to/from the group node.
    /// </summary>
    void WireGroupProxies(List<VertexViewModel> visibleVertices, List<VertexGroupViewModel> visibleGroups)
    {
        if (Vertices is null) return;

        foreach (var group in visibleGroups)
        {
            // Create proxy if needed
            if (group.ProxyVertex is null)
            {
                group.ProxyVertex = new VertexViewModel(group.Members[0].Value)
                {
                    Width = group.Width,
                    Height = group.Height
                };
            }

            var proxy = group.ProxyVertex;
            proxy.X = group.X;
            proxy.Y = group.Y;
            proxy.Width = group.Width;
            proxy.Height = group.Height;
            proxy.Neighbors.Clear();

            var memberSet = new HashSet<VertexViewModel>(group.Members);

            // Incoming edges: group members' parents that are external → proxy.Neighbors
            foreach (var member in group.Members)
            {
                foreach (var neighbor in member.Neighbors)
                {
                    if (!memberSet.Contains(neighbor) && !proxy.Neighbors.Contains(neighbor))
                        proxy.Neighbors.Add(neighbor);
                }
            }

            // The proxy aggregates every external input; give it one input port per distinct producer
            // so their arrows land on separate anchors instead of collapsing onto Members[0]'s ports.
            proxy.InputPortCountOverride = proxy.Neighbors.Count;

            // Outgoing edges: external vertices whose parents include group members.
            // REPLACE member references with the proxy (not just add) to avoid ghost arrows.
            foreach (var v in visibleVertices)
            {
                if (memberSet.Contains(v)) continue;

                // Find all group members in this vertex's Neighbors
                var membersInNeighbors = v.Neighbors.Where(n => memberSet.Contains(n)).ToList();
                if (membersInNeighbors.Count == 0) continue;

                // Remove member references, track them for restoration
                foreach (var m in membersInNeighbors)
                {
                    v.Neighbors.Remove(m);
                    _memberRedirects.Add((v, m));
                }

                // Add the proxy once
                v.Neighbors.Add(proxy);
                _neighborRedirects.Add((v, proxy));
            }
        }
    }

    void CleanUpProxyRedirects()
    {
        // Remove proxies
        foreach (var (vertex, proxy) in _neighborRedirects)
            vertex.Neighbors.Remove(proxy);
        _neighborRedirects.Clear();

        // Restore original member references
        foreach (var (vertex, member) in _memberRedirects)
            vertex.Neighbors.Add(member);
        _memberRedirects.Clear();
    }

    #endregion

    #region Selection helpers

    void ClearSelection()
    {
        foreach (var v in _selectedVertices) v.IsSelected = false;
        _selectedVertices.Clear();
        foreach (var g in _selectedGroups) g.IsSelected = false;
        _selectedGroups.Clear();
        UpdateToolbar();
    }

    void SelectVertex(VertexViewModel vm)
    {
        vm.IsSelected = true;
        _selectedVertices.Add(vm);
        UpdateToolbar();
    }

    void DeselectVertex(VertexViewModel vm)
    {
        vm.IsSelected = false;
        _selectedVertices.Remove(vm);
        UpdateToolbar();
    }

    void ToggleVertex(VertexViewModel vm)
    {
        if (_selectedVertices.Contains(vm)) DeselectVertex(vm);
        else SelectVertex(vm);
    }

    void SelectGroup(VertexGroupViewModel gvm)
    {
        gvm.IsSelected = true;
        _selectedGroups.Add(gvm);
        UpdateToolbar();
    }

    void DeselectGroup(VertexGroupViewModel gvm)
    {
        gvm.IsSelected = false;
        _selectedGroups.Remove(gvm);
        UpdateToolbar();
    }

    void ToggleGroup(VertexGroupViewModel gvm)
    {
        if (_selectedGroups.Contains(gvm)) DeselectGroup(gvm);
        else SelectGroup(gvm);
    }

    void SelectVerticesInRect(Rect rect)
    {
        foreach (var v in GetVisibleVertices())
        {
            var nodeRect = new Rect(v.X, v.Y, v.Width, v.Height);
            if (rect.Intersects(nodeRect))
                SelectVertex(v);
        }
        foreach (var g in _groups.Where(g => g.Parent is null))
        {
            var groupRect = new Rect(g.X, g.Y, g.Width, g.Height);
            if (rect.Intersects(groupRect))
                SelectGroup(g);
        }
    }

    #endregion

    #region Toolbar

    void UpdateToolbar()
    {
        if (_groupToolbar is null) return;

        bool canGroup = _selectedVertices.Count >= 2;
        bool canUngroup = _selectedGroups.Count == 1 && _selectedVertices.Count == 0;

        _groupToolbar.IsVisible = canGroup || canUngroup;
        if (_groupBtn != null) _groupBtn.IsVisible = canGroup;
        if (_ungroupBtn != null) _ungroupBtn.IsVisible = canUngroup;
    }

    void OnGroupClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedVertices.Count < 2) return;
        GroupSelectedVertices();
    }

    void OnUngroupClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGroups.Count != 1) return;
        UngroupSelectedGroup();
    }

    #endregion

    #region Grouping logic

    /// <summary>Releases group subscriptions and references when the pipeline is replaced.</summary>
    public void ClearGroups()
    {
        ClearSelection();
        foreach (var group in _groups)
            group.StopStatusMonitoring();
        _groups.Clear();
        _groupControls.Clear();
        _groupDragOrigins = null;
        _groupNodeDragOrigins = null;
        _connectSource = null;
        _connectTarget = null;
        _connecting = false;
        Rebuild();
    }

    /// <summary>Restores affected member cards before their blocks are deleted.</summary>
    public void UngroupBlocks(ISet<MOSAIC.Components.Basics.BaseBlock> blocks)
    {
        ClearSelection();
        foreach (var group in _groups.Where(g => g.Members.Any(m => blocks.Contains(m.Value))).ToArray())
        {
            group.StopStatusMonitoring();
            group.ProxyVertex = null;
            _groups.Remove(group);
            GroupRemoved?.Invoke(this, group);
        }
        Rebuild();
    }

    void GroupSelectedVertices()
    {
        if (_selectedVertices.Count < 2) return;

        var group = new VertexGroupViewModel
        {
            Name = $"Group {_groups.Count + 1}",
        };

        // Add members and save their positions
        foreach (var v in _selectedVertices)
        {
            group.Members.Add(v);
            group.MemberBlocks.Add(v.Value);   // BaseBlock for flyout rendering
            group.SavedPositions[v] = (v.X, v.Y);
        }

        // Size based on member spread
        float minX = group.Members.Min(m => m.X);
        float maxX = group.Members.Max(m => m.X + m.Width);
        float minY = group.Members.Min(m => m.Y);
        float maxY = group.Members.Max(m => m.Y + m.Height);
        group.Width = Math.Max(180, Math.Min((maxX - minX) * 0.4f, 300));

        // Position group so its center aligns with the center of the members' bounding box
        float membersCenterX = (minX + maxX) / 2f;
        float membersCenterY = (minY + maxY) / 2f;
        group.X = membersCenterX - group.Width / 2f;
        group.Y = membersCenterY - group.Height / 2f;

        _groups.Add(group);

        // Start monitoring member block statuses
        group.StartStatusMonitoring();

        ClearSelection();
        Rebuild();

        GroupCreated?.Invoke(this, group);
    }

    void UngroupSelectedGroup()
    {
        var group = _selectedGroups.FirstOrDefault();
        if (group is null) return;

        ClearSelection();

        // Stop status monitoring
        group.StopStatusMonitoring();

        // Restore member positions
        foreach (var v in group.Members)
        {
            if (group.SavedPositions.TryGetValue(v, out var pos))
            {
                v.X = pos.X;
                v.Y = pos.Y;
            }
        }

        group.ProxyVertex = null;
        _groups.Remove(group);

        Rebuild();

        GroupRemoved?.Invoke(this, group);
    }

    #endregion

    #region Node add / remove

    void AddNode(VertexViewModel vm)
    {
        if (_root is null) return;
        var node = new GraphVertexView { DataContext = vm };
        node.Background ??= Brushes.Transparent;
        if (vm.Width > 0) node.Width = vm.Width;
        if (vm.Height > 0) node.Height = vm.Height;
        node.ZIndex = 1;
        Canvas.SetLeft(node, vm.X);
        Canvas.SetTop(node, vm.Y);
        _root.Children.Add(node);

        PropertyChangedEventHandler h = (_, a) =>
        {
            if (a.PropertyName is nameof(VertexViewModel.X) or nameof(VertexViewModel.Y))
            {
                Canvas.SetLeft(node, vm.X);
                Canvas.SetTop(node, vm.Y);
                _edges?.InvalidateVisual();
            }
        };
        vm.PropertyChanged += h;
        _vm[vm] = h;

        const double TH = 4;
        bool maybe = false, drag = false;
        Point press = default;

        node.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (!e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
            if (_connecting) { e.Handled = true; return; }

            // Build mode: grabbing the output port starts a wire, not a move/select.
            if (App.BuildMode && _root is not null && IsOnOutputPort(vm, e.GetPosition(_root)))
            {
                BeginConnection(vm);
                UpdateConnectionDrag(e.GetPosition(_root));
                e.Pointer.Capture(node);
                e.Handled = true;
                return;
            }

            _nodeInteracting = true;
            maybe = true;
            drag = false;
            press = e.GetPosition(node);

            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (ctrl)
                ToggleVertex(vm);
            else if (!_selectedVertices.Contains(vm))
            {
                ClearSelection();
                SelectVertex(vm);
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        node.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (_connecting)
            {
                if (ReferenceEquals(_connectSource, vm) && _root is not null)
                    UpdateConnectionDrag(e.GetPosition(_root));
                e.Handled = true;
                return;
            }

            if (!maybe && !drag) return;
            var p = e.GetPosition(node);
            if (!drag)
            {
                if (Math.Abs(p.X - press.X) < TH && Math.Abs(p.Y - press.Y) < TH) return;

                drag = true;
                _groupDragging = true;
                node.Cursor = new Cursor(StandardCursorType.SizeAll);

                var maxZ = _root!.Children.OfType<Control>()
                    .Where(c => !ReferenceEquals(c, _edges))
                    .Select(c => c.ZIndex).DefaultIfEmpty(1).Max();
                foreach (var child in _root.Children.OfType<Control>())
                    if (child.DataContext is VertexViewModel v && _selectedVertices.Contains(v))
                        child.ZIndex = maxZ + 1;

                _groupDragAnchor = e.GetPosition(_root!);
                _groupDragOrigins = new Dictionary<VertexViewModel, Point>();
                foreach (var sv in _selectedVertices)
                    _groupDragOrigins[sv] = new Point(sv.X, sv.Y);

                _groupNodeDragOrigins = new Dictionary<VertexGroupViewModel, Point>();
                foreach (var sg in _selectedGroups)
                    _groupNodeDragOrigins[sg] = new Point(sg.X, sg.Y);

                e.Pointer.Capture(node);
            }

            if (_groupDragOrigins is null) return;
            var current = e.GetPosition(_root!);
            double deltaX = current.X - _groupDragAnchor.X;
            double deltaY = current.Y - _groupDragAnchor.Y;

            foreach (var (sv, origin) in _groupDragOrigins)
            {
                sv.X = (float)(origin.X + deltaX);
                sv.Y = (float)(origin.Y + deltaY);
            }
            if (_groupNodeDragOrigins != null)
                foreach (var (sg, origin) in _groupNodeDragOrigins)
                    MoveGroupNode(sg, (float)(origin.X + deltaX), (float)(origin.Y + deltaY));

            _edges?.InvalidateVisual();
            e.Handled = true;

        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        node.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (_connecting)
            {
                if (ReferenceEquals(_connectSource, vm))
                {
                    EndConnectionDrag(commit: true);
                    e.Pointer.Capture(null);
                }
                e.Handled = true;
                return;
            }

            _nodeInteracting = false;
            if (drag)
            {
                drag = false;
                maybe = false;
                _groupDragging = false;
                _groupDragOrigins = null;
                _groupNodeDragOrigins = null;
                node.Cursor = new Cursor(StandardCursorType.Arrow);
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            else if (maybe)
            {
                maybe = false;
                bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (!ctrl)
                {
                    ClearSelection();
                    SelectVertex(vm);
                    NodeClicked?.Invoke(this, vm);
                }
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        node.PointerCaptureLost += (_, __) =>
        {
            if (_connecting) EndConnectionDrag(commit: false);
            drag = false;
            maybe = false;
            _nodeInteracting = false;
            _groupDragging = false;
            _groupDragOrigins = null;
            _groupNodeDragOrigins = null;
            node.Cursor = new Cursor(StandardCursorType.Arrow);
        };
    }

    void RemoveNode(VertexViewModel vm)
    {
        if (_root is null) return;
        DeselectVertex(vm);
        for (int i = _root.Children.Count - 1; i >= 0; i--)
            if (_root.Children[i] is Control c && ReferenceEquals(c.DataContext, vm))
                _root.Children.RemoveAt(i);
        if (_vm.TryGetValue(vm, out var h))
        {
            vm.PropertyChanged -= h;
            _vm.Remove(vm);
        }
    }

    #endregion

    #region Group node add / remove / drag

    void AddGroupNode(VertexGroupViewModel gvm)
    {
        if (_root is null) return;

        var node = new GraphVertexGroupView { DataContext = gvm };
        node.Background ??= Brushes.Transparent;
        node.Width = gvm.Width;
        node.Height = gvm.Height;
        node.ZIndex = 1;
        Canvas.SetLeft(node, gvm.X);
        Canvas.SetTop(node, gvm.Y);
        _root.Children.Add(node);
        _groupControls[gvm] = node;

        // Keep proxy in sync when group moves
        gvm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName is nameof(VertexGroupViewModel.X) or nameof(VertexGroupViewModel.Y))
            {
                Canvas.SetLeft(node, gvm.X);
                Canvas.SetTop(node, gvm.Y);
                gvm.SyncProxyPosition();
                _edges?.InvalidateVisual();
            }
        };

        const double TH = 4;
        bool maybe = false, drag = false;
        Point press = default;

        node.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (!e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
            _nodeInteracting = true;

            // Double-click → inline rename
            if (e.ClickCount >= 2)
            {
                node.BeginRename();
                e.Handled = true;
                maybe = false;
                return;
            }

            maybe = true;
            drag = false;
            press = e.GetPosition(node);

            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (ctrl)
                ToggleGroup(gvm);
            else if (!_selectedGroups.Contains(gvm))
            {
                ClearSelection();
                SelectGroup(gvm);
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        node.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (node.IsEditing) return;  // Don't drag while renaming
            if (!maybe && !drag) return;
            var p = e.GetPosition(node);
            if (!drag)
            {
                if (Math.Abs(p.X - press.X) < TH && Math.Abs(p.Y - press.Y) < TH) return;

                drag = true;
                _groupDragging = true;
                node.Cursor = new Cursor(StandardCursorType.SizeAll);

                _groupDragAnchor = e.GetPosition(_root!);
                _groupNodeDragOrigins = new Dictionary<VertexGroupViewModel, Point>();
                foreach (var sg in _selectedGroups)
                    _groupNodeDragOrigins[sg] = new Point(sg.X, sg.Y);

                _groupDragOrigins = new Dictionary<VertexViewModel, Point>();
                foreach (var sv in _selectedVertices)
                    _groupDragOrigins[sv] = new Point(sv.X, sv.Y);

                e.Pointer.Capture(node);
            }

            var current = e.GetPosition(_root!);
            double dx = current.X - _groupDragAnchor.X;
            double dy = current.Y - _groupDragAnchor.Y;

            if (_groupNodeDragOrigins != null)
                foreach (var (sg, origin) in _groupNodeDragOrigins)
                    MoveGroupNode(sg, (float)(origin.X + dx), (float)(origin.Y + dy));

            if (_groupDragOrigins != null)
                foreach (var (sv, origin) in _groupDragOrigins)
                {
                    sv.X = (float)(origin.X + dx);
                    sv.Y = (float)(origin.Y + dy);
                }

            _edges?.InvalidateVisual();
            e.Handled = true;

        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        node.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            _nodeInteracting = false;
            if (drag)
            {
                drag = false;
                maybe = false;
                _groupDragging = false;
                _groupDragOrigins = null;
                _groupNodeDragOrigins = null;
                node.Cursor = new Cursor(StandardCursorType.Arrow);
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            else if (maybe)
            {
                maybe = false;
                bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (!ctrl)
                {
                    ClearSelection();
                    SelectGroup(gvm);
                }
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        node.PointerCaptureLost += (_, __) =>
        {
            if (_connecting) EndConnectionDrag(commit: false);
            drag = false;
            maybe = false;
            _nodeInteracting = false;
            _groupDragging = false;
            _groupDragOrigins = null;
            _groupNodeDragOrigins = null;
            node.Cursor = new Cursor(StandardCursorType.Arrow);
        };
    }

    void MoveGroupNode(VertexGroupViewModel gvm, float nx, float ny)
    {
        gvm.X = nx;
        gvm.Y = ny;
        gvm.SyncProxyPosition();
        if (_groupControls.TryGetValue(gvm, out var ctrl))
        {
            Canvas.SetLeft(ctrl, nx);
            Canvas.SetTop(ctrl, ny);
        }
    }

    void RemoveGroupControl(VertexGroupViewModel gvm)
    {
        if (_root is null) return;
        if (_groupControls.TryGetValue(gvm, out var ctrl))
        {
            _root.Children.Remove(ctrl);
            _groupControls.Remove(gvm);
        }
    }

    #endregion

    #region Edge helpers

    void SizeEdges()
    {
        if (_edges is null) return;
        _edges.Width = Bounds.Width;
        _edges.Height = Bounds.Height;
        _edges.InvalidateVisual();
    }

    #endregion

    #region Fit to view

    /// <summary>
    /// Frames the entire graph — every visible node and group node — inside the viewport using the
    /// pan/zoom transform, so the whole block diagram stays on screen. Called on (re)build and on
    /// every resize; manual zoom/pan overrides it until the next fit. Large/complex graphs scale
    /// down to fit; small graphs are shown at up to natural size (never blown up absurdly).
    /// </summary>
    public void FitToView()
    {
        if (_panHost is null) return;

        double vpW = Bounds.Width, vpH = Bounds.Height;
        if (vpW <= 0 || vpH <= 0) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var any = false;

        void Extend(double x, double y, double w, double h)
        {
            any = true;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x + w > maxX) maxX = x + w;
            if (y + h > maxY) maxY = y + h;
        }

        foreach (var v in GetVisibleVertices()) Extend(v.X, v.Y, v.Width, v.Height);
        foreach (var g in _groups.Where(g => g.Parent is null)) Extend(g.X, g.Y, g.Width, g.Height);
        if (!any) return;

        double graphW = maxX - minX;
        double graphH = maxY - minY;

        const double margin = 48;   // viewport padding around the graph
        double availW = Math.Max(1, vpW - 2 * margin);
        double availH = Math.Max(1, vpH - 2 * margin);

        double fitW = graphW > 0 ? availW / graphW : double.MaxValue;
        double fitH = graphH > 0 ? availH / graphH : double.MaxValue;
        double fit = Math.Min(fitW, fitH);
        if (double.IsInfinity(fit) || fit <= 0) fit = 1.0;

        // Always fit everything: shrink as far as needed for big graphs, cap zoom-in for tiny ones.
        _scale = Math.Clamp(fit, ScaleMin, 1.5);

        double graphCx = (minX + maxX) / 2.0;
        double graphCy = (minY + maxY) / 2.0;
        _ox = vpW / 2.0 - _scale * graphCx;
        _oy = vpH / 2.0 - _scale * graphCy;

        ApplyTransform();
        _edges?.InvalidateVisual();
    }

    #endregion

    #region Canvas-level pointer handlers (pan, zoom, rubber-band)

    void OnPress(object? s, PointerPressedEventArgs e)
    {
        if (_connecting) return;
        var p = e.GetCurrentPoint(this).Properties;

        if (p.IsMiddleButtonPressed || p.IsRightButtonPressed)
        {
            _panning = true;
            _panStart = e.GetPosition(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Left click → rubber-band ONLY if no node was pressed
        if (p.IsLeftButtonPressed && !_nodeInteracting && !_groupDragging)
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (!ctrl) ClearSelection();

            _rubberBanding = true;
            _rubberStart = e.GetPosition(this);
            if (_selectionRect != null)
            {
                _selectionRect.IsVisible = false;
                _selectionRect.Width = 0;
                _selectionRect.Height = 0;
            }
            e.Pointer.Capture(this);
        }
    }

    void OnMove(object? s, PointerEventArgs e)
    {
        if (_connecting) return;
        if (_panning && _panHost is not null)
        {
            var p = e.GetPosition(this);
            var tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(_scale, _scale));
            tg.Children.Add(new TranslateTransform(
                _ox + (p.X - _panStart.X),
                _oy + (p.Y - _panStart.Y)));
            _panHost.RenderTransform = tg;
            return;
        }

        if (_rubberBanding && _selectionRect is not null)
        {
            var p = e.GetPosition(this);
            var x = Math.Min(p.X, _rubberStart.X);
            var y = Math.Min(p.Y, _rubberStart.Y);
            var w = Math.Abs(p.X - _rubberStart.X);
            var h = Math.Abs(p.Y - _rubberStart.Y);

            if (w > 4 || h > 4)
            {
                _selectionRect.IsVisible = true;
                _selectionRect.Margin = new Thickness(x, y, 0, 0);
                _selectionRect.Width = w;
                _selectionRect.Height = h;
                _selectionRect.HorizontalAlignment = HorizontalAlignment.Left;
                _selectionRect.VerticalAlignment = VerticalAlignment.Top;
            }
        }
    }

    void OnRelease(object? s, PointerReleasedEventArgs e)
    {
        if (_connecting) return;
        _nodeInteracting = false;

        if (_panning && _panHost is not null)
        {
            var p = e.GetPosition(this);
            _ox += p.X - _panStart.X;
            _oy += p.Y - _panStart.Y;
            _panning = false;
            Cursor = new Cursor(StandardCursorType.Arrow);
            e.Pointer.Capture(null);
            return;
        }

        if (_rubberBanding)
        {
            _rubberBanding = false;
            e.Pointer.Capture(null);

            if (_selectionRect is not null && _selectionRect.IsVisible)
            {
                _selectionRect.IsVisible = false;

                var p = e.GetPosition(this);
                var screenRect = new Rect(
                    Math.Min(p.X, _rubberStart.X),
                    Math.Min(p.Y, _rubberStart.Y),
                    Math.Abs(p.X - _rubberStart.X),
                    Math.Abs(p.Y - _rubberStart.Y));

                var canvasRect = new Rect(
                    (screenRect.X - _ox) / _scale,
                    (screenRect.Y - _oy) / _scale,
                    screenRect.Width / _scale,
                    screenRect.Height / _scale);

                SelectVerticesInRect(canvasRect);
            }
        }
    }

    void OnWheel(object? s, PointerWheelEventArgs e)
    {
        if (_panHost is null) return;

        var mouse = e.GetPosition(this);
        var oldScale = _scale;
        _scale = e.Delta.Y > 0
            ? Math.Min(_scale * ScaleFactor, ScaleMax)
            : Math.Max(_scale / ScaleFactor, ScaleMin);

        _ox = mouse.X - (_scale / oldScale) * (mouse.X - _ox);
        _oy = mouse.Y - (_scale / oldScale) * (mouse.Y - _oy);

        ApplyTransform();
        e.Handled = true;
    }

    void ApplyTransform()
    {
        if (_panHost is null) return;
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(_scale, _scale));
        tg.Children.Add(new TranslateTransform(_ox, _oy));
        _panHost.RenderTransform = tg;
    }

    #endregion
}
