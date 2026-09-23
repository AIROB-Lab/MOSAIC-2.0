using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the Projector card.
/// </summary>
public partial class ProjectorViewModel : ObservableObject
{
    public Projector Projector { get; }

    /// <summary>
    /// Editable text for indices (comma, semicolon, or space separated).
    /// </summary>
    [ObservableProperty]
    private string _indicesText = "";

    /// <summary>
    /// Error message for invalid input.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    public ProjectorViewModel(Projector projector)
    {
        Projector = projector;
        _indicesText = FormatIndices(projector.ProjectedIndices);
        
        Projector.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Projector.ProjectedIndices))
            {
                IndicesText = FormatIndices(Projector.ProjectedIndices);
                ErrorMessage = null;
            }
            else if (e.PropertyName == nameof(Projector.ValidationError))
            {
                ErrorMessage = Projector.ValidationError;
            }
        };
    }

    [RelayCommand]
    private void ApplyIndices()
    {
        if (string.IsNullOrWhiteSpace(IndicesText))
        {
            Projector.ProjectedIndices = Array.Empty<int>();
            ErrorMessage = null;
            return;
        }

        try
        {
            var indices = IndicesText
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.Parse(s.Trim()))
                .ToArray();
            
            // Validate against known input channels
            if (Projector.InputChannels > 0)
            {
                var invalid = indices.Where(i => i < 0 || i >= Projector.InputChannels).ToArray();
                if (invalid.Length > 0)
                {
                    ErrorMessage = $"Invalid: {string.Join(", ", invalid)} (range: 0-{Projector.InputChannels - 1})";
                    return;
                }
            }
            
            // Check for negative indices
            var negative = indices.Where(i => i < 0).ToArray();
            if (negative.Length > 0)
            {
                ErrorMessage = $"Negative indices not allowed: {string.Join(", ", negative)}";
                return;
            }
            
            Projector.ProjectedIndices = indices;
            ErrorMessage = null;
        }
        catch (FormatException)
        {
            ErrorMessage = "Invalid format. Use numbers separated by commas.";
        }
    }

    [RelayCommand]
    private void ResetIndices()
    {
        IndicesText = FormatIndices(Projector.ProjectedIndices);
        ErrorMessage = null;
    }

    private static string FormatIndices(int[]? indices) 
        => indices?.Length > 0 ? string.Join(", ", indices) : "";
}
