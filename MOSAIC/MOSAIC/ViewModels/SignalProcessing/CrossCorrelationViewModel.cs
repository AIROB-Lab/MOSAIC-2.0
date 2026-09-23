using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Enums;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

public partial class CrossCorrelationViewModel : ObservableObject, IDisposable
{
    private readonly CrossCorrelation _block;
    private bool _disposed;

    public CrossCorrelation Block => _block;
    public string Name => _block.Name;

    [ObservableProperty] private BlockStatus _blockStatus;

    public int MaxLag => _block.MaxLag;
    public int BufferLen => _block.BufferLen;
    public IReadOnlyList<string> ChannelOptions { get; }
    public string SelectedChannelOption1
    {
        get => ChannelOptions[_block.SelectedChannel1 + 1];
        set { int index = ChannelOptions.ToList().IndexOf(value); if (index >= 0) _block.SelectedChannel1 = index - 1; }
    }
    public string SelectedChannelOption2
    {
        get => ChannelOptions[_block.SelectedChannel2 + 1];
        set { int index = ChannelOptions.ToList().IndexOf(value); if (index >= 0) _block.SelectedChannel2 = index - 1; }
    }

    public CrossCorrelationViewModel(CrossCorrelation block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        ChannelOptions = new[] { "All (avg)" }.Concat(Enumerable.Range(0, block.ChannelCount).Select(i => $"Ch {i}")).ToArray();
        _blockStatus = _block.Status;
        _block.PropertyChanged += OnBlockPropertyChanged;
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(CrossCorrelation.Status))
            BlockStatus = _block.Status;
        if (e.PropertyName == nameof(CrossCorrelation.MaxLag)) OnPropertyChanged(nameof(MaxLag));
        if (e.PropertyName == nameof(CrossCorrelation.BufferLen)) OnPropertyChanged(nameof(BufferLen));
        if (e.PropertyName == nameof(CrossCorrelation.SelectedChannel1)) OnPropertyChanged(nameof(SelectedChannelOption1));
        if (e.PropertyName == nameof(CrossCorrelation.SelectedChannel2)) OnPropertyChanged(nameof(SelectedChannelOption2));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _block.PropertyChanged -= OnBlockPropertyChanged;
    }
}
