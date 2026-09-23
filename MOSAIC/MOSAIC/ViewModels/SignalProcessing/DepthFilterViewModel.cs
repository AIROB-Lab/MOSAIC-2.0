using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

/// <summary>
/// ViewModel for the DepthFilter card. Mirrors the FilterViewModel pattern:
/// filter type buttons, slider + text cutoffs, order/taps, Apply/Reset.
/// </summary>
public partial class DepthFilterViewModel : ObservableObject
{
    private readonly DepthFilter _depthFilter;

    public DepthFilterViewModel(DepthFilter depthFilter)
    {
        _depthFilter = depthFilter;
        _depthFilter.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);

            // Recompute derived properties when filter params change
            if (e.PropertyName is nameof(DepthFilter.FilterType))
            {
                OnPropertyChanged(nameof(IsBandFilter));
                OnPropertyChanged(nameof(SelectedFilterType));
            }
            if (e.PropertyName is nameof(DepthFilter.Implementation))
                OnPropertyChanged(nameof(IsFir));
            if (e.PropertyName is nameof(DepthFilter.DepthSampleRate))
            {
                OnPropertyChanged(nameof(MaxCutoff));
                OnPropertyChanged(nameof(MinCutoff));
            }
        };
    }

    /// <summary>Exposes the underlying block for direct binding.</summary>
    public DepthFilter DepthFilter => _depthFilter;

    #region Enums for ComboBoxes

    public FilterType[] FilterTypes { get; } = Enum.GetValues<FilterType>();
    public FilterImplementation[] Implementations { get; } = Enum.GetValues<FilterImplementation>();

    #endregion

    #region Computed Properties

    /// <summary>Currently selected filter type (for button highlighting).</summary>
    public FilterType SelectedFilterType => _depthFilter.FilterType;

    /// <summary>Whether band cutoffs section should be visible.</summary>
    public bool IsBandFilter => _depthFilter.FilterType is FilterType.Bandpass or FilterType.Bandstop;

    /// <summary>Whether FIR-specific settings should be visible.</summary>
    public bool IsFir => _depthFilter.Implementation == FilterImplementation.FIR;

    /// <summary>Minimum cutoff for sliders (near DC).</summary>
    public double MinCutoff => 0.1;

    /// <summary>Maximum cutoff for sliders (Nyquist of depth sample rate).</summary>
    public double MaxCutoff => (_depthFilter.DepthSampleRate > 0 ? _depthFilter.DepthSampleRate : 1000) / 2.0;

    #endregion

    #region Filter Type Commands

    [RelayCommand]
    private void SetLowpass() => _depthFilter.FilterType = FilterType.Lowpass;

    [RelayCommand]
    private void SetHighpass() => _depthFilter.FilterType = FilterType.Highpass;

    [RelayCommand]
    private void SetBandpass() => _depthFilter.FilterType = FilterType.Bandpass;

    [RelayCommand]
    private void SetBandstop() => _depthFilter.FilterType = FilterType.Bandstop;

    #endregion

    #region Apply / Reset

    [RelayCommand]
    private void ApplyFilter() => _depthFilter.ApplyFilter();

    [RelayCommand]
    private void ResetFilter() => _depthFilter.ApplyFilter();

    #endregion
}