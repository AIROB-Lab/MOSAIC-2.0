using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Media;
using MOSAIC.Converters;
using MOSAIC.ViewModels.Graph;

namespace MOSAIC.Views;

public partial class GraphVertexView : UserControl
{
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#4A9EF5"));
    private static readonly BoxShadows SelectedShadow = BoxShadows.Parse("0 0 0 4 #884A9EF5");
    private static readonly BoxShadows DefaultShadow = BoxShadows.Parse("0 2 8 0 #40000000");
    private static readonly StatusToColorConverter StatusConverter = new();

    // Port dot palette (fixed hexes that read on both light and dark node surfaces).
    private static readonly IBrush ConnectedFill = new SolidColorBrush(Color.Parse("#6B7280")); // wired
    private static readonly IBrush RequiredStroke = new SolidColorBrush(Color.Parse("#EF9F27")); // empty · required
    private static readonly IBrush OptionalStroke = new SolidColorBrush(Color.Parse("#9CA3AF")); // empty · optional
    private static readonly IBrush OutputFill = new SolidColorBrush(Color.Parse("#6B7280"));

    // The VertexViewModel we've subscribed PropertyChanged on, so we can unsubscribe (these node
    // controls are recreated on every graph rebuild while the VMs survive — an untracked handler
    // would leak the old control and pile up stale handlers on the VM).
    private VertexViewModel? _boundVm;

    public GraphVertexView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        App.BuildModeChanged += OnBuildModeChanged;
        ApplyBuildMode(App.BuildMode);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        App.BuildModeChanged -= OnBuildModeChanged;
        UnbindVm();
    }

    private void UnbindVm()
    {
        if (_boundVm is null) return;
        _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = null;
    }

    private void OnBuildModeChanged(object? sender, bool on) => ApplyBuildMode(on);

    /// <summary>Build affordances (ports + arity badge) are visible only while building a pipeline.</summary>
    private void ApplyBuildMode(bool on)
    {
        if (this.FindControl<Canvas>("PortCanvas") is { } canvas) canvas.IsVisible = on;
        if (this.FindControl<Border>("ArityBadge") is { } badge) badge.IsVisible = on;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        UnbindVm();   // drop the handler on any previous VM

        if (DataContext is not VertexViewModel vm) return;

        var border = this.FindControl<Border>("RootBorder");
        if (border is null) return;

        _boundVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;

        ApplySelectionVisual(border, vm.IsSelected);
        BuildPorts(vm);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs a)
    {
        if (DataContext is not VertexViewModel vm) return;

        if (a.PropertyName == nameof(VertexViewModel.IsSelected))
        {
            if (this.FindControl<Border>("RootBorder") is { } border)
                ApplySelectionVisual(border, vm.IsSelected);
        }
        else if (a.PropertyName == nameof(VertexViewModel.InputPorts))
        {
            BuildPorts(vm);
        }
    }

    /// <summary>
    /// Draws the input port dots along the top edge and the output port on the bottom edge (the
    /// pipeline flows top→down), using the same <see cref="PortLayout"/> geometry that
    /// <see cref="ViewModels.Graph.EdgeDrawableControl"/> uses to anchor edges, so dots and arrows line up.
    /// </summary>
    private void BuildPorts(VertexViewModel vm)
    {
        if (this.FindControl<Canvas>("PortCanvas") is not { } canvas) return;
        canvas.Children.Clear();

        double w = vm.Width > 0 ? vm.Width : 132;
        double h = vm.Height > 0 ? vm.Height : 45;

        var ports = vm.InputPorts;
        int n = ports.Count;
        for (int i = 0; i < n; i++)
        {
            var (fill, stroke) = ports[i].State switch
            {
                PortState.Connected => (ConnectedFill, (IBrush?)null),
                PortState.Required  => (Brushes.Transparent, RequiredStroke),
                _                   => (Brushes.Transparent, OptionalStroke),
            };
            var dot = MakePort(fill, stroke);
            Canvas.SetLeft(dot, w * (i + 1) / (n + 1) - PortLayout.Radius);
            Canvas.SetTop(dot, -PortLayout.Radius);
            canvas.Children.Add(dot);
        }

        if (vm.HasOutput)
        {
            var outDot = MakePort(OutputFill, null);
            Canvas.SetLeft(outDot, w / 2 - PortLayout.Radius);
            Canvas.SetTop(outDot, h - PortLayout.Radius);
            canvas.Children.Add(outDot);
        }
    }

    private static Ellipse MakePort(IBrush fill, IBrush? stroke) => new()
    {
        Width = PortLayout.Diameter,
        Height = PortLayout.Diameter,
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = stroke is null ? 0 : 2,
        IsHitTestVisible = false
    };

    private static void ApplySelectionVisual(Border border, bool selected)
    {
        if (selected)
        {
            border.BorderBrush = SelectionBrush;
            border.BoxShadow = SelectedShadow;
        }
        else
        {
            // Re-create the binding that the AXAML originally had
            border.Bind(Border.BorderBrushProperty, new Binding("Value.Status")
            {
                Converter = StatusConverter
            });
            border.BoxShadow = DefaultShadow;
        }
    }

    /// <summary>
    /// Sync the toggle if the popup is light-dismissed (clicked outside / ESC).
    /// </summary>
    private void InfoPopup_OnClosed(object? sender, EventArgs e)
    {
        var toggle = this.FindControl<ToggleButton>("InfoToggle");
        if (toggle != null && toggle.IsChecked == true)
            toggle.IsChecked = false;
    }
}
