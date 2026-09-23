using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.Views;

/// <summary>
/// A single window that shows the live scope of every scope-capable block in the pipeline at once.
///
/// <para>Each card renders a <see cref="ScopeMonitor"/> that is registered as a <em>mirror</em> of the
/// block's real scope (<see cref="ChannelRingMonitorBase.AddMirror"/>), so it replays exactly the data
/// the block feeds its own inline card — after any tail-extraction, tuple-unwrapping or electrode
/// stripping the block already performs — rather than re-deriving it from the block's published output.
/// Blocks that expose several scopes (e.g. Muovi's EMG + IMU, SiFi's EMG/IMU/PPG/…) get one card each.</para>
/// </summary>
public partial class ScopeMonitorWindow : Window
{
    private readonly List<MirrorCard> _cards = new();
    private bool _gridMode;
    private double _windowSeconds = 30;
    private bool _spanReady;

    public ScopeMonitorWindow()
    {
        InitializeComponent();
    }

    public ScopeMonitorWindow(IEnumerable<BaseBlock> blocks) : this()
    {
        BuildCards(blocks);
        ApplyLayout();
        UpdateModeButtons();
        InitSpanCombo();
    }

    /// <summary>
    /// Tears down all current mirrors/cards and rebuilds the window from a fresh set of blocks.
    /// Called when the pipeline changes (block added/deleted or config reloaded) so the monitor
    /// reflects the live blocks instead of continuing to show scopes bound to removed ones.
    /// </summary>
    public void Rebind(IEnumerable<BaseBlock> blocks)
    {
        ClearCards();
        BuildCards(blocks);
        ApplyLayout();
        UpdateModeButtons();
    }

    /// <summary>True when the window currently has no scope cards to show.</summary>
    public bool IsEmpty => _cards.Count == 0;

    #region Build / teardown

    private void BuildCards(IEnumerable<BaseBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var src in CollectScopeSources(block))
            {
                var mirror = new ScopeMonitor();
                mirror.SetWindow(TimeSpan.FromSeconds(_windowSeconds));
                if (src.Source.SignalRate > 0)
                    mirror.UpdateSignalRate(src.Source.SignalRate);

                src.Source.AddMirror(mirror);
                src.OwnerViz?.NotifyAttached();
                mirror.Resume();

                var mc = new MirrorCard
                {
                    Mirror = mirror,
                    Source = src.Source,
                    OwnerViz = src.OwnerViz,
                };
                mc.Card = CreateCard(mc, src.Block, src.Suffix);
                _cards.Add(mc);
            }
        }
    }

    /// <summary>Unregister and dispose every mirror, and drop all cards.</summary>
    private void ClearCards()
    {
        foreach (var mc in _cards)
        {
            mc.Card.EffectiveViewportChanged -= OnCardViewportChanged;
            mc.Source.RemoveMirror(mc.Mirror);
            mc.OwnerViz?.NotifyDetached();
            mc.Mirror.Dispose();
        }
        _cards.Clear();
        CardContainer.Children.Clear();
    }

    private Border CreateCard(MirrorCard mc, BaseBlock block, string? suffix)
    {
        mc.Up = ReorderButton("▲");
        mc.Down = ReorderButton("▼");

        var reorderPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { mc.Up, mc.Down }
        };
        DockPanel.SetDock(reorderPanel, Dock.Right);

        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Bind to the live block name so renames are reflected without a rebuild.
        var nameBinding = new Binding(nameof(BaseBlock.Name)) { Source = block };
        if (suffix != null) nameBinding.StringFormat = "{0} · " + suffix;
        title[!TextBlock.TextProperty] = nameBinding;

        var header = new DockPanel();
        header.Children.Add(reorderPanel);
        header.Children.Add(title);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    header,
                    new VisualizationPanel
                    {
                        Scope = mc.Mirror,
                        ScopeVisibleByDefault = true,
                        ShowScopeViewModeToggle = true,
                        MinHeight = 200,
                    }
                }
            }
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("ThemeCardGradient");

        mc.Up.Click += (_, _) => MoveCard(mc, -1);
        mc.Down.Click += (_, _) => MoveCard(mc, +1);

        // Pause the scope while its card is scrolled out of view (see OnCardViewportChanged).
        card.EffectiveViewportChanged += OnCardViewportChanged;

        return card;
    }

    /// <summary>
    /// Viewport gating: cards live in a non-virtualizing StackPanel/WrapPanel, so scrolling never
    /// detaches them and every scope would otherwise keep rendering at 30fps off-screen. Here we pause
    /// a card's mirror when it leaves the scroll viewport (with a pre-fetch margin) and resume it on
    /// re-entry — a paused monitor stops both its ring ingest and its ScottPlot refresh.
    /// </summary>
    private void OnCardViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is not Control card) return;

        MirrorCard? mc = null;
        foreach (var c in _cards)
            if (ReferenceEquals(c.Card, card)) { mc = c; break; }
        if (mc is null) return;

        const double margin = 250;
        var vp = e.EffectiveViewport;
        var bounds = new Rect(card.Bounds.Size).Inflate(margin);
        bool onScreen = vp.Width > 0 && vp.Height > 0 && vp.Intersects(bounds);

        if (mc.OnScreen == onScreen) return;
        mc.OnScreen = onScreen;
        if (onScreen) mc.Mirror.Resume();
        else mc.Mirror.Pause();
    }

    private static Button ReorderButton(string glyph)
    {
        var btn = new Button
        {
            Content = glyph, FontSize = 10, Padding = new Thickness(6, 2),
            MinWidth = 0, MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        btn[!Button.ForegroundProperty] = new DynamicResourceExtension("ThemeTextPrimaryBrush");
        return btn;
    }

    #endregion

    #region Layout & reorder

    private void OnFlexClicked(object? sender, RoutedEventArgs e)
    {
        _gridMode = false;
        ApplyLayout();
        UpdateModeButtons();
    }

    private void OnGridClicked(object? sender, RoutedEventArgs e)
    {
        _gridMode = true;
        ApplyLayout();
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        var active = !_gridMode ? FlexBtn : GridBtn;
        var inactive = !_gridMode ? GridBtn : FlexBtn;
        active[!Button.BackgroundProperty] = new DynamicResourceExtension("ThemeAccentBrush");
        active.Opacity = 1.0;
        inactive.Background = Brushes.Transparent;
        inactive.Opacity = 0.5;
    }

    private void ApplyLayout()
    {
        foreach (var mc in _cards)
        {
            // A full re-add detaches then re-attaches each card, and the view's attach logic resumes
            // its mirror — so reset OnScreen to match; viewport events then re-pause off-screen cards.
            mc.OnScreen = true;
            if (mc.Card.Parent is Border wrapper)
                wrapper.Child = null;
            else if (mc.Card.Parent is Panel p)
                p.Children.Remove(mc.Card);
        }
        CardContainer.Children.Clear();

        if (!_gridMode)
        {
            foreach (var mc in _cards)
            {
                mc.LayoutElement = mc.Card;
                CardContainer.Children.Add(mc.Card);
            }
        }
        else
        {
            // Responsive columns: cards stretch to fill each row between a min and max width, so
            // they never clip at the window's min width and never leave a ragged gutter when wide.
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var mc in _cards)
            {
                var slot = new Border
                {
                    MinWidth = 320, MaxWidth = 520,
                    Margin = new Thickness(4),
                    Child = mc.Card,
                };
                mc.LayoutElement = slot;
                wrap.Children.Add(slot);
            }
            CardContainer.Children.Add(wrap);
        }

        UpdateEdgeButtons();
        UpdateEmptyState();
    }

    /// <summary>Disable the top card's ▲ and the bottom card's ▼ so edge buttons don't look clickable.</summary>
    private void UpdateEdgeButtons()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].Up.IsEnabled = i > 0;
            _cards[i].Down.IsEnabled = i < _cards.Count - 1;
        }
    }

    private void MoveCard(MirrorCard card, int direction)
    {
        var idx = _cards.IndexOf(card);
        var newIdx = idx + direction;
        if (idx < 0 || newIdx < 0 || newIdx >= _cards.Count) return;

        _cards.RemoveAt(idx);
        _cards.Insert(newIdx, card);

        // Move the layout element in place instead of rebuilding: Children.Move keeps the control
        // parented, so its AvaPlot/streamers are NOT destroyed and recreated (unlike a full ApplyLayout).
        if (card.LayoutElement?.Parent is Panel panel)
        {
            int from = panel.Children.IndexOf(card.LayoutElement);
            if (from >= 0 && newIdx >= 0 && newIdx < panel.Children.Count)
                panel.Children.Move(from, newIdx);
        }

        UpdateEdgeButtons();
    }

    private void UpdateEmptyState()
    {
        if (EmptyState != null) EmptyState.IsVisible = _cards.Count == 0;
    }

    #endregion

    #region Time span

    private void InitSpanCombo()
    {
        for (int i = 0; i < SpanCombo.ItemCount; i++)
        {
            if (SpanCombo.Items[i] is ComboBoxItem item &&
                item.Tag is string tag && double.TryParse(tag, out var s) &&
                Math.Abs(s - _windowSeconds) < 0.5)
            {
                SpanCombo.SelectedIndex = i;
                break;
            }
        }
        _spanReady = true;
    }

    private void OnSpanChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_spanReady) return;
        if (SpanCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag || !double.TryParse(tag, out var seconds))
            return;

        _windowSeconds = seconds;
        var span = TimeSpan.FromSeconds(seconds);
        foreach (var mc in _cards)
            mc.Mirror.SetWindow(span);
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        ClearCards();
        base.OnClosed(e);
    }

    #region Scope-source discovery

    private readonly record struct ScopeSource(
        ScopeMonitor Source, BlockVisualization? OwnerViz, BaseBlock Block, string? Suffix);

    /// <summary>
    /// Finds every <see cref="ScopeMonitor"/> a block feeds — both the one inside a
    /// <see cref="BlockVisualization"/> and any directly-exposed <see cref="ScopeMonitor"/> properties
    /// (e.g. Muovi's ScopeEmg/ScopeImu). When a block has more than one, each source is labelled with a
    /// short suffix derived from its property name.
    /// </summary>
    private static List<ScopeSource> CollectScopeSources(BaseBlock block)
    {
        var found = new List<(ScopeMonitor mon, BlockVisualization? viz, string prop)>();

        foreach (var prop in block.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            try
            {
                if (prop.PropertyType == typeof(BlockVisualization))
                {
                    if (prop.GetValue(block) is BlockVisualization viz && viz.Scope is { } scope)
                        found.Add((scope, viz, prop.Name));
                }
                else if (prop.PropertyType == typeof(ScopeMonitor))
                {
                    if (prop.GetValue(block) is ScopeMonitor scope)
                        found.Add((scope, null, prop.Name));
                }
            }
            catch { /* skip properties that throw on get */ }
        }

        var result = new List<ScopeSource>(found.Count);
        bool multi = found.Count > 1;
        foreach (var (mon, viz, prop) in found)
        {
            // Give raw sub-scopes a suffix (EMG/IMU/…); the primary Viz keeps the plain block name.
            string? suffix = viz == null && multi ? PrettySuffix(prop) : null;
            result.Add(new ScopeSource(mon, viz, block, suffix));
        }
        return result;
    }

    private static string PrettySuffix(string prop)
    {
        var s = prop.StartsWith("Scope", StringComparison.Ordinal) ? prop.Substring(5) : prop;
        if (string.IsNullOrEmpty(s)) s = prop;
        return s.Length <= 3 ? s.ToUpperInvariant() : s;
    }

    private static readonly ConcurrentDictionary<Type, bool> ScopePropertyCache = new();

    /// <summary>True if the block <em>type</em> exposes any scope-capable property (used to decide
    /// whether the Scope Monitor FAB should be shown at all).</summary>
    internal static bool HasScopeProperty(BaseBlock block) =>
        ScopePropertyCache.GetOrAdd(block.GetType(), static type =>
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType == typeof(BlockVisualization) || prop.PropertyType == typeof(ScopeMonitor))
                    return true;
            }
            return false;
        });

    #endregion

    private sealed class MirrorCard
    {
        public Border Card = null!;
        public ScopeMonitor Mirror = null!;
        public ChannelRingMonitorBase Source = null!;
        public BlockVisualization? OwnerViz;
        public Button Up = null!;
        public Button Down = null!;

        /// <summary>The element sitting directly in the flow panel (the card in flex mode, its
        /// wrapper Border in grid mode); the target of in-place reordering.</summary>
        public Control? LayoutElement;

        /// <summary>Whether the card is currently within the scroll viewport (drives pause/resume).</summary>
        public bool OnScreen = true;
    }
}
