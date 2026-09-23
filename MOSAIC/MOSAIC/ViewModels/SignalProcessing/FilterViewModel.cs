using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Visualization.Heatmap;
using MOSAIC.Visualization.ScopeMonitor;
using MOSAIC.Visualization.SpiderMonitor;
using Filter = MOSAIC.Models.SignalProcessing.Filter;

namespace MOSAIC.ViewModels.SignalProcessing;

/// <summary>
/// Static converters for FilterView.
/// </summary>
public static class FilterConverters
{
    public static readonly IValueConverter FilterTypeToBackground = new FilterTypeToBackgroundConverter();
    public static readonly IValueConverter BoolToLowLabel = new FuncValueConverter<bool, string>(
        isBand => isBand ? "Low:" : "Cutoff:");
}

public class FilterTypeToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Models.SignalProcessing.FilterType currentType && parameter is string paramStr)
        {
            if (Enum.TryParse<Models.SignalProcessing.FilterType>(paramStr, out var targetType2))
            {
                return currentType == targetType2 
                    ? new SolidColorBrush(Color.Parse("#00AAFF")) 
                    : new SolidColorBrush(Color.Parse("#3A3A3A"));
            }
        }
        return new SolidColorBrush(Color.Parse("#3A3A3A"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// ViewModel for FilterBlock with dynamic parameter control.
/// Includes ScopeMonitor, SpiderMonitor, and HeatMap for filtered output visualization.
/// 
/// Threading:
/// - Block events (PropertyChanged, OnPublish, OnParametersChanged) may fire on background threads
/// - ObservableProperty supplies notifications, not processing synchronization. The model applies settings under its processing lock.
/// - Avalonia marshals bound property updates; collection or control changes still require the UI thread.
/// </summary>
public partial class FilterViewModel : ObservableObject, IDisposable
{
    private readonly Filter _block;
    private bool _disposed;

    // === Filter Type Selection ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBandFilter))]
    private FilterType _selectedFilterType;

    [ObservableProperty]
    private FilterImplementation _selectedImplementation;

    // === Parameters ===
    [ObservableProperty]
    private double _cutoffLow;

    [ObservableProperty]
    private double _cutoffHigh;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private int _firTaps;

    [ObservableProperty]
    private bool _isEnabled;

    // === Stats ===
    [ObservableProperty]
    private long _samplesProcessed;

    [ObservableProperty]
    private string _filterDescription;


    // === Computed Properties ===
    public Filter Block => _block;
    public string Name => _block.Name;
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";
    
    public double SampleRate => _block.SampleRate;
    
    public MOSAIC.Components.Enums.BlockStatus BlockStatus => _block.Status;

    public bool IsBandFilter => SelectedFilterType == FilterType.Bandpass || SelectedFilterType == FilterType.Bandstop;
    public bool IsFirFilter => SelectedImplementation == FilterImplementation.FIR;

    public List<FilterType> AvailableFilterTypes { get; } = new(Enum.GetValues<FilterType>());
    public List<FilterImplementation> AvailableImplementations { get; } = new(Enum.GetValues<FilterImplementation>());

    // Slider ranges
    public double MinCutoff => 0.1;
    public double MaxCutoff => SampleRate / 2 * 0.99;

    public FilterViewModel(Filter block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        // Initialize from block
        _selectedFilterType = _block.FilterType;
        _selectedImplementation = _block.Implementation;
        _cutoffLow = _block.CutoffLow;
        _cutoffHigh = _block.CutoffHigh;
        _order = _block.Order;
        _firTaps = _block.FirTaps;
        _isEnabled = _block.IsEnabled;
        _filterDescription = _block.FilterDescription;
        

        // Subscribe to block events
        _block.OnParametersChanged += HandleParametersChanged;
        _block.PropertyChanged += Block_PropertyChanged;
        _block.OnPublish += HandleFilterOutput;
    }

    /// <summary>
    /// Handles filtered output data for visualization.
    /// Called on processing thread — all monitors' enqueue methods are thread-safe.
    /// </summary>
    private void HandleFilterOutput(object data)
    {
        if (_disposed) return;

        try
        {
            Vector<double>? vec = data switch
            {
                Vector<double> v => v,
                double[] arr => Vector<double>.Build.Dense(arr),
                _ => null
            };

            if (vec == null) return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] Visualization error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles property changes from the block.
    /// </summary>
    private void Block_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        switch (e.PropertyName)
        {
            case nameof(Filter.SamplesProcessed):
                SamplesProcessed = _block.SamplesProcessed;
                break;
                
            case nameof(Filter.FilterDescription):
                FilterDescription = _block.FilterDescription;
                break;
                
            case nameof(Filter.SampleRate):
                OnPropertyChanged(nameof(SampleRate));
                OnPropertyChanged(nameof(MaxCutoff));
                OnPropertyChanged(nameof(FrequencyText));
                ClampCutoffsToValidRange();
                break;
                
            case nameof(Filter.Status):
                OnPropertyChanged(nameof(BlockStatus));
                break;
        }
    }

    private void HandleParametersChanged(FilterType type, FilterImplementation impl, double low, double high, int order)
    {
        if (_disposed) return;
        FilterDescription = _block.FilterDescription;
    }

    private void ClampCutoffsToValidRange()
    {
        var max = MaxCutoff;
        if (CutoffLow > max) CutoffLow = max;
        if (CutoffHigh > max) CutoffHigh = max;
    }

    // === Commands ===

    [RelayCommand]
    private void ApplyChanges()
    {
        try
        {
            _block.UpdateParameters(
                filterType: SelectedFilterType,
                implementation: SelectedImplementation,
                cutoffLow: CutoffLow,
                cutoffHigh: CutoffHigh,
                order: Order,
                firTaps: FirTaps
            );
            _block.IsEnabled = IsEnabled;
            _block.ClearError();
            FilterDescription = _block.FilterDescription;
        }
        catch (ArgumentException ex)
        {
            _block.ReportError("Settings were not applied. Correct the filter parameters and try again.", ex);
        }
    }

    [RelayCommand]
    private void ResetFilters()
    {
        _block.ResetFilters();
    }

    [RelayCommand]
    private void SetLowpass()
    {
        SelectedFilterType = FilterType.Lowpass;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetHighpass()
    {
        SelectedFilterType = FilterType.Highpass;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetBandpass()
    {
        SelectedFilterType = FilterType.Bandpass;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetBandstop()
    {
        SelectedFilterType = FilterType.Bandstop;
        ApplyChanges();
    }

    // === Preset Commands ===

    [RelayCommand]
    private void SetPreset50Hz()
    {
        CutoffLow = 50;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetPreset100Hz()
    {
        CutoffLow = 100;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetPreset200Hz()
    {
        CutoffLow = 200;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetPresetNotch50Hz()
    {
        SelectedFilterType = FilterType.Bandstop;
        CutoffLow = 48;
        CutoffHigh = 52;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetPresetNotch60Hz()
    {
        SelectedFilterType = FilterType.Bandstop;
        CutoffLow = 58;
        CutoffHigh = 62;
        ApplyChanges();
    }

    [RelayCommand]
    private void SetPresetEMG()
    {
        SelectedFilterType = FilterType.Bandpass;
        CutoffLow = 20;
        CutoffHigh = (Block.SampleRate >= 900.0) 
            ? 450.0
            : (Block.SampleRate / 2.0 - 0.1);
        ApplyChanges();
    }

    [RelayCommand]
    private void SetPresetEEG()
    {
        SelectedFilterType = FilterType.Bandpass;
        CutoffLow = 0.5;
        CutoffHigh = 100;
        ApplyChanges();
    }

    // === Property Change Handlers ===

    partial void OnSelectedFilterTypeChanged(FilterType value)
    {
        OnPropertyChanged(nameof(IsBandFilter));
    }

    partial void OnSelectedImplementationChanged(FilterImplementation value)
    {
        OnPropertyChanged(nameof(IsFirFilter));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _block.OnParametersChanged -= HandleParametersChanged;
        _block.PropertyChanged -= Block_PropertyChanged;
        _block.OnPublish -= HandleFilterOutput;
    }
}
