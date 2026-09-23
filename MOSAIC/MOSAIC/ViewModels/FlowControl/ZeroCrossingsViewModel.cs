using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the ZeroCrossings card.
/// </summary>
public partial class ZeroCrossingsViewModel : ObservableObject
{
    
    [ObservableProperty]
    private string _thresholdText;

    [ObservableProperty]
    private string? _errorMessage;
    
    private ZeroCrossings zeroCrossings;
    
    public ZeroCrossings ZeroCrossings
    {
        get => zeroCrossings;
        set => zeroCrossings = value;
    }

    /// <summary>
    /// Formula description for display.
    /// </summary>
    public static string Description => "ZC = count of sign changes";
    
    public ZeroCrossingsViewModel(ZeroCrossings zeroCrossings)
    {
        ZeroCrossings = zeroCrossings;
        this.zeroCrossings = zeroCrossings;
        _thresholdText = zeroCrossings.Threshold.ToString("F3", System.Globalization.CultureInfo.CurrentCulture); ;
    }
    
    partial void OnThresholdTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = null;  // Empty is ok, no update
            return;
        }

        // Handle both comma and period
        var normalized = value.Replace(',', '.');

        if (!double.TryParse(normalized, NumberStyles.Float, 
                CultureInfo.InvariantCulture, out double threshold))
        {
            ErrorMessage = "Invalid number format";
            return;
        }

        if (threshold < 0)
        {
            ErrorMessage = "Threshold must be ≥ 0";
            return;
        }

        ErrorMessage = null;
        ZeroCrossings.Threshold = threshold;
    }
}
