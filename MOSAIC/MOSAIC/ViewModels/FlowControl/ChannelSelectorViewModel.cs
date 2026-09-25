using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.FlowControl;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the ChannelSelector card, managing channel activation states.
/// </summary>
public partial class ChannelSelectorViewModel : ObservableObject, IDisposable
{
    public ChannelSelector ChannelSelector { get; }

    [ObservableProperty] 
    private ObservableCollection<ChannelState> _channels = new();

    /// <summary>
    /// Gets the count of currently active channels.
    /// </summary>
    public int ActiveChannelCount => Channels.Count(c => c.IsActive);
    
    /// <summary>
    /// Gets summary text for channel selection.
    /// </summary>
    public string SelectionSummary => $"{ActiveChannelCount} / {Channels.Count} active";
    
    /// <summary>
    /// Gets the output mode text.
    /// </summary>
    public string OutputModeText => ChannelSelector.DeleteChannel ? "Remove inactive" : "Zero inactive";

    private bool _isUpdating;

    public ChannelSelectorViewModel(ChannelSelector channelSelector)
    {
        ChannelSelector = channelSelector;
        ChannelSelector.NumberOfChannelsChanged += OnNumberOfChannelsChanged;
        ChannelSelector.PropertyChanged += OnChannelSelectorPropertyChanged;
        RefreshChannels();
    }

    private void OnChannelSelectorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelSelector.DeleteChannel))
        {
            OnPropertyChanged(nameof(OutputModeText));
        }
    }

    private void OnNumberOfChannelsChanged(int count)
    {
        // Dispatch to UI thread if needed
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshChannels);
    }

    [RelayCommand]
    private void RefreshChannels()
    {
        _isUpdating = true;
        try
        {
            var activations = ChannelSelector.ChannelActivations;
            var count = ChannelSelector.NumberOfChannels;
            
            // Optimize: only rebuild if count changed
            if (Channels.Count != count)
            {
                Channels.Clear();
                for (int i = 0; i < count; i++)
                {
                    var isActive = i < activations.Count && activations[i];
                    Channels.Add(new ChannelState(i, isActive, this));
                }
            }
            else
            {
                // Just update existing states
                for (int i = 0; i < count; i++)
                {
                    var isActive = i < activations.Count && activations[i];
                    Channels[i].SetActiveWithoutNotify(isActive);
                }
            }
            
            OnPropertyChanged(nameof(ActiveChannelCount));
            OnPropertyChanged(nameof(SelectionSummary));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        _isUpdating = true;
        try
        {
            foreach (var ch in Channels)
                ch.SetActiveWithoutNotify(true);
        }
        finally
        {
            _isUpdating = false;
        }
        
        ApplyChanges();
        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand]
    private void DeselectAll()
    {
        _isUpdating = true;
        try
        {
            foreach (var ch in Channels)
                ch.SetActiveWithoutNotify(false);
        }
        finally
        {
            _isUpdating = false;
        }
        
        ApplyChanges();
        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }
    
    [RelayCommand]
    private void InvertSelection()
    {
        _isUpdating = true;
        try
        {
            foreach (var ch in Channels)
                ch.SetActiveWithoutNotify(!ch.IsActive);
        }
        finally
        {
            _isUpdating = false;
        }
        
        ApplyChanges();
        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }
    
    [RelayCommand]
    private void SelectRange(string rangeSpec)
    {
        // Parse range like "1-4" or "1,3,5" or "1-4,7,9-12"
        if (string.IsNullOrWhiteSpace(rangeSpec)) return;
        
        var indices = ParseRangeSpec(rangeSpec);
        
        _isUpdating = true;
        try
        {
            foreach (var ch in Channels)
                ch.SetActiveWithoutNotify(indices.Contains(ch.Index));
        }
        finally
        {
            _isUpdating = false;
        }
        
        ApplyChanges();
        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }
    
    private HashSet<int> ParseRangeSpec(string spec)
    {
        var result = new HashSet<int>();
        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var range = trimmed.Split('-');
                if (range.Length == 2 && 
                    int.TryParse(range[0], out int start) && 
                    int.TryParse(range[1], out int end))
                {
                    // Convert to 0-based
                    for (int i = start - 1; i < end && i < Channels.Count; i++)
                    {
                        if (i >= 0) result.Add(i);
                    }
                }
            }
            else if (int.TryParse(trimmed, out int index))
            {
                // Convert to 0-based
                if (index > 0 && index <= Channels.Count)
                    result.Add(index - 1);
            }
        }
        
        return result;
    }

    public void OnChannelStateChanged()
    {
        if (_isUpdating) return;
        
        ApplyChanges();
        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public void ApplyChanges()
    {
        ChannelSelector.UpdateChannelActivations(Channels.Select(c => c.IsActive));
    }
    
    public void Dispose()
    {
        ChannelSelector.NumberOfChannelsChanged -= OnNumberOfChannelsChanged;
        ChannelSelector.PropertyChanged -= OnChannelSelectorPropertyChanged;
    }
}

/// <summary>
/// Represents the state of a single channel with color matching the scope.
/// </summary>
public partial class ChannelState : ObservableObject
{
    private readonly ChannelSelectorViewModel _parent;

    public int Index { get; }
    
    /// <summary>
    /// Gets the 1-based display name for the channel.
    /// </summary>
    public string DisplayName => $"Ch {Index + 1}";
    
    /// <summary>
    /// Gets a short label for compact display.
    /// </summary>
    public string ShortLabel => $"{Index + 1}";
    
    /// <summary>
    /// Gets the color for this channel (from ScopeMonitor's palette).
    /// </summary>
    public string ColorHex => ScopeMonitor.GetChannelColorHex(Index);
    
    /// <summary>
    /// Gets the color as a brush for binding.
    /// </summary>
    public IBrush ColorBrush => new SolidColorBrush(Color.Parse(ColorHex));
    
    /// <summary>
    /// Gets the background color based on active state.
    /// Active = channel color, Inactive = dark gray
    /// </summary>
    public IBrush BackgroundBrush => IsActive 
        ? new SolidColorBrush(Color.Parse(ColorHex)) 
        : new SolidColorBrush(Color.Parse("#2A2A2A"));
    
    /// <summary>
    /// Gets the foreground color based on active state.
    /// </summary>
    public IBrush ForegroundBrush => IsActive 
        ? new SolidColorBrush(Colors.White) 
        : new SolidColorBrush(Color.Parse("#606060"));
    
    /// <summary>
    /// Gets the border color (always shows channel color for reference).
    /// </summary>
    public IBrush BorderBrush => new SolidColorBrush(Color.Parse(ColorHex));

    [ObservableProperty] 
    private bool _isActive;

    public ChannelState(int index, bool isActive, ChannelSelectorViewModel parent)
    {
        Index = index;
        _isActive = isActive;
        _parent = parent;
    }
    
    /// <summary>
    /// Sets the active state without triggering parent notification.
    /// Used for batch updates.
    /// </summary>
    public void SetActiveWithoutNotify(bool value)
    {
#pragma warning disable MVVMTK0034
        if (_isActive != value)
        {
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(ForegroundBrush));
        }
#pragma warning restore MVVMTK0034
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundBrush));
        OnPropertyChanged(nameof(ForegroundBrush));
        _parent.OnChannelStateChanged();
    }
}
