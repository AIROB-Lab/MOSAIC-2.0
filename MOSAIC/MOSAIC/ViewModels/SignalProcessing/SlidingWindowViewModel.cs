using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

/// <summary>
/// ViewModel for the SlidingWindow card.
/// </summary>
public partial class SlidingWindowViewModel : ObservableObject
{
    public SlidingWindow SlidingWindow { get; }

    /// <summary>
    /// Available window types for the ComboBox.
    /// </summary>
    public SlidingWindow.WindowType[] WindowTypes { get; } = 
        Enum.GetValues<SlidingWindow.WindowType>();

    /// <summary>
    /// Buffer size as editable string with validation.
    /// </summary>
    [ObservableProperty]
    private string _bufferSizeText = "";

    /// <summary>
    /// Stride as editable string with validation.
    /// </summary>
    [ObservableProperty]
    private string _strideText = "";

    /// <summary>
    /// Error message for invalid input.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    public SlidingWindowViewModel(SlidingWindow slidingWindow)
    {
        SlidingWindow = slidingWindow;
        
        
        // Initialize text from model
        _bufferSizeText = slidingWindow.BufferSize.ToString();
        _strideText = slidingWindow.Stride.ToString();
        
        // Sync when model changes externally
        SlidingWindow.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SlidingWindow.BufferSize))
                BufferSizeText = SlidingWindow.BufferSize.ToString();
            else if (e.PropertyName == nameof(SlidingWindow.Stride))
                StrideText = SlidingWindow.Stride.ToString();
        };
    }

    partial void OnBufferSizeTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = null; // Empty is ok, just don't update
            return;
        }

        if (!int.TryParse(value, out int bufferSize))
        {
            ErrorMessage = "Buffer size must be a number";
            return;
        }

        if (bufferSize <= 0)
        {
            ErrorMessage = "Buffer size must be > 0";
            return;
        }

        if (bufferSize > 10000)
        {
            ErrorMessage = "Buffer size too large (max 10000)";
            return;
        }

        ErrorMessage = null;
        
        // Validate stride doesn't exceed new buffer size
        if (SlidingWindow.Stride > bufferSize)
        {
            SlidingWindow.Stride = bufferSize;
            StrideText = bufferSize.ToString();
        }
        
        SlidingWindow.BufferSize = bufferSize;
    }

    partial void OnStrideTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = null;
            return;
        }

        if (!int.TryParse(value, out int stride))
        {
            ErrorMessage = "Stride must be a number";
            return;
        }

        if (stride <= 0)
        {
            ErrorMessage = "Stride must be > 0";
            return;
        }

        if (stride > SlidingWindow.BufferSize)
        {
            ErrorMessage = $"Stride must be ≤ buffer size ({SlidingWindow.BufferSize})";
            return;
        }

        ErrorMessage = null;
        SlidingWindow.Stride = stride;
    }
}
