using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the SlopeSignChanges card.
/// </summary>
public partial class SlopeSignChangesViewModel : ObservableObject
{
    public SlopeSignChanges SlopeSignChanges { get; }

    /// <summary>
    /// Formula description for display.
    /// </summary>
    public static string Description => "Σ sign(Δx) changes";

    /// <summary>
    /// Threshold as editable string with validation.
    /// </summary>
    [ObservableProperty]
    private string _thresholdText;

    /// <summary>
    /// Error message for invalid input.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    public SlopeSignChangesViewModel(SlopeSignChanges slopeSignChanges)
    {
        SlopeSignChanges = slopeSignChanges;
        _thresholdText = slopeSignChanges.Threshold.ToString("F3", System.Globalization.CultureInfo.CurrentCulture);
    }

    partial void OnThresholdTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = null;
            return;
        }

        // Handle both comma and period as decimal separator
        var normalized = value.Replace(',', '.');

        if (!double.TryParse(normalized, System.Globalization.NumberStyles.Float, 
            System.Globalization.CultureInfo.InvariantCulture, out double threshold))
        {
            ErrorMessage = "Invalid number format";
            return;
        }

        if (threshold < 0)
        {
            ErrorMessage = "Threshold must be ≥ 0";
            return;
        }

        if (threshold > 1000)
        {
            ErrorMessage = "Threshold too large (max 1000)";
            return;
        }

        ErrorMessage = null;
        SlopeSignChanges.Threshold = threshold;
    }
}
