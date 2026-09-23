using System;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Devices.Wulpus;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.Devices;
using MOSAIC.Services;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the Wulpus ultrasound dongle card.
/// </summary>
/// <remarks>
/// <para>
/// Bridges the <see cref="Wulpus"/> pipeline block and the Avalonia UI.
/// All settable JSON parameters (USS config path, RX/TX config path, data sending mode,
/// append metadata) are exposed as two-way bindable properties. Device control
/// (scan, open, close, stream start/stop) and config file browsing are exposed as
/// <see cref="RelayCommand"/>/<see cref="AsyncRelayCommand"/> instances.
/// </para>
/// <para>
/// File picker dialogs are accessed via <see cref="IFilePickerService"/>, resolved
/// from the application's DI container at construction time.
/// </para>
/// </remarks>
public partial class WulpusViewModel : ObservableObject
{
    #region Fields

    private readonly Wulpus _wulpus;
    private readonly IFilePickerService _filePicker;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new ViewModel wrapping the given <see cref="Wulpus"/> block.
    /// </summary>
    /// <param name="wulpus">The pipeline block to wrap.</param>
    public WulpusViewModel(Wulpus wulpus)
    {
        _wulpus = wulpus;
        _filePicker = ((App)Application.Current!).Services.GetRequiredService<IFilePickerService>();

        _wulpus.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);

            if (e.PropertyName is nameof(Wulpus.IsConnected) or nameof(Wulpus.IsStreaming))
                NotifyCommandsChanged();
        };
    }

    #endregion

    #region Public Surface

    /// <summary>Exposes the underlying block for direct binding from the view.</summary>
    public Wulpus Wulpus => _wulpus;

    /// <summary>Available data sending modes for the ComboBox.</summary>
    public Wulpus.DataSendingMode[] SendingModes { get; } = Enum.GetValues<Wulpus.DataSendingMode>();

    #endregion

    #region Observable Properties

    /// <summary>Index of the device selected in the device list ComboBox.</summary>
    [ObservableProperty] private int _selectedDeviceIndex;

    #endregion

    #region Device Commands

    /// <summary>Scans for available Wulpus dongles.</summary>
    [RelayCommand]
    private void Scan()
    {
        _wulpus.ScanDevices();
        OnPropertyChanged(nameof(Wulpus));
    }

    /// <summary>Opens the COM port for the currently selected device.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        _wulpus.OpenPort(SelectedDeviceIndex);
        OnPropertyChanged(nameof(Wulpus));
    }

    private bool CanOpen() => !_wulpus.IsConnected && _wulpus.FoundDevices.Count > 0;

    /// <summary>Closes the active COM connection.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        _wulpus.ClosePort();
        OnPropertyChanged(nameof(Wulpus));
    }

    private bool CanClose() => _wulpus.IsConnected;

    #endregion

    #region Streaming Commands

    /// <summary>Starts data streaming from the dongle.</summary>
    [RelayCommand(CanExecute = nameof(CanStartStream))]
    private void StartStream()
    {
        _wulpus.StartStreaming();
        OnPropertyChanged(nameof(Wulpus));
    }

    private bool CanStartStream() => _wulpus.IsConnected && !_wulpus.IsStreaming;

    /// <summary>Stops data streaming.</summary>
    [RelayCommand(CanExecute = nameof(CanStopStream))]
    private void StopStream()
    {
        _wulpus.StopStreaming();
        OnPropertyChanged(nameof(Wulpus));
    }

    private bool CanStopStream() => _wulpus.IsStreaming;

    #endregion

    #region Configuration Commands

    /// <summary>Opens a file picker for the USS configuration JSON and applies it.</summary>
    [RelayCommand]
    private async Task BrowseUssConfigAsync()
    {
        var path = await _filePicker.PickJsonFileAsync("Select USS Configuration (.json)");
        if (path is not null)
        {
            _wulpus.LoadUssConfig(path);
            OnPropertyChanged(nameof(Wulpus));
        }
    }

    /// <summary>Opens a file picker for the RX/TX configuration JSON and applies it.</summary>
    [RelayCommand]
    private async Task BrowseRxTxConfigAsync()
    {
        var path = await _filePicker.PickJsonFileAsync("Select RX/TX Configuration (.json)");
        if (path is not null)
        {
            _wulpus.LoadRxTxConfig(path);
            OnPropertyChanged(nameof(Wulpus));
        }
    }

    #endregion

    #region Helpers

    private void NotifyCommandsChanged()
    {
        OpenCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        StartStreamCommand.NotifyCanExecuteChanged();
        StopStreamCommand.NotifyCanExecuteChanged();
    }

    #endregion
}