using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.SignalProcessing;

public partial class FunctionViewModel : ObservableObject, System.IDisposable
{
    private readonly Function _function;
    
    public Function Function => _function;

    /// <summary>
    /// Available function types for the dropdown.
    /// </summary>
    public IReadOnlyList<string> FunctionTypes => Function.AvailableFunctionTypes;

    /// <summary>
    /// Gets whether Param1 is used by the current function type.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Param1Label))]
    private bool _showParam1;

    /// <summary>
    /// Gets whether Param2 is used by the current function type.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Param2Label))]
    private bool _showParam2;

    // ═══════════════════════════════════════════════════════════════════
    // Visualization — one line. That's it.
    // The VisualizationPanel manages pause/resume, the BlockVisualization
    // owns all three monitors, Feed() updates them all.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// All visualization monitors (Scope + Spider + Heatmap) in one bundle.
    /// Bind in XAML with: &lt;viz:VisualizationPanel Source="{Binding Viz}" /&gt;
    /// Feed from the model with: _viewModel.Viz.Feed(outputVector);
    /// </summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>
    /// Label for Param1 based on function type.
    /// </summary>
    public string Param1Label => _function.FunctionType?.ToLowerInvariant() switch
    {
        "add" => "Addend",
        "multiply" => "Factor",
        "power" => "Exponent",
        "clip" => "Min",
        "threshold" => "Threshold",
        _ => "Parameter"
    };

    /// <summary>
    /// Label for Param2 based on function type.
    /// </summary>
    public string Param2Label => _function.FunctionType?.ToLowerInvariant() switch
    {
        "clip" => "Max",
        _ => "Parameter 2"
    };

    public FunctionViewModel(Function function)
    {
        _function = function;
        
        // Wire visualization to the model so OnReceive can feed all monitors
        _function.Viz = Viz;

        // Set initial visibility
        UpdateVisibility();
        
        // Subscribe to function type changes
        _function.PropertyChanged += OnFunctionPropertyChanged;
    }

    private void OnFunctionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Function.FunctionType))
        {
            UpdateVisibility();
        }
    }

    private void UpdateVisibility()
    {
        var type = _function.FunctionType?.ToLowerInvariant();
        ShowParam1 = type != "abs";
        ShowParam2 = type == "clip";
    }

    public void Dispose()
    {
        _function.PropertyChanged -= OnFunctionPropertyChanged;
        if (ReferenceEquals(_function.Viz, Viz))
            _function.Viz = null;
        Viz.Dispose();
    }
}
