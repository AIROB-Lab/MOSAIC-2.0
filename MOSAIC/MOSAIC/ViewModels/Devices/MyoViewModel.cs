using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Devices;

namespace MOSAIC.ViewModels.Devices;

public partial class MyoViewModel(Myo myo) : ObservableObject
{
    public Myo Myo => myo;

    // Header bindings
    public string Name => Myo.Name;
    public string FrequencyText => Myo.FrequencyText;

    // UI state
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private string? _lastError;

    // Bind directly to Myo's collection — no separate copy
    public ObservableCollection<string> FoundDevices => Myo.FoundDevices;

    [ObservableProperty] private string? _selectedDevice;

    // ===== Commands =====

    [RelayCommand]
    private async Task StartScan()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Scanning...";
        LastError = null;
        try
        {
            // Clear on UI thread (ObservableCollection must be modified on UI thread)
            await Dispatcher.UIThread.InvokeAsync(() => FoundDevices.Clear());

            // Run the BLE scan (this blocks for ~5 seconds)
            await Task.Run(async () => await Myo.ScanAsync());

            // Myo.ScanAsync populates Myo.FoundDevices on a background thread.
            // ObservableCollection may have been modified off-thread, so re-sync:
            // Take a snapshot and repopulate on UI thread.
            var devices = Myo.FoundDevices.ToArray();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FoundDevices.Clear();
                foreach (var d in devices)
                    FoundDevices.Add(d);
            });

            Status = FoundDevices.Count > 0 ? $"Found {FoundDevices.Count}" : "No devices";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Scan Error";
            Console.WriteLine($"[MyoViewModel] Scan error: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleConnect()
    {
        Console.WriteLine($"[MyoVM] ToggleConnect: IsBusy={IsBusy}, IsConnected={IsConnected}, SelectedDevice='{SelectedDevice}'");
    
        if (IsBusy) return;
        // ... rest

        if (!IsConnected)
        {
            if (string.IsNullOrWhiteSpace(SelectedDevice)) return;
            IsBusy = true;
            Status = "Connecting...";
            LastError = null;
            try
            {
                var ok = await Myo.ConnectAsync(SelectedDevice);
                IsConnected = ok && Myo.IsConnected;
                Status = IsConnected ? "EMG data received — start the connected Clock to plot." : "Connection failed";
                LastError = Myo.LastConnectionError;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Status = "Error";
                Console.WriteLine($"[MyoViewModel] Connect error: {ex}");
            }
            finally
            {
                IsBusy = false;
            }
        }
        else
        {
            IsBusy = true;
            Status = "Disconnecting...";
            try
            {
                await Myo.DisconnectAsync();
                IsConnected = false;
                Status = "Disconnected";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Status = "Error";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // Keep for backward compat
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var ok = await Myo.ConnectAsync(SelectedDevice ?? string.Empty).ConfigureAwait(false);
            IsConnected = ok && Myo.IsConnected;
            Status = IsConnected ? "EMG data received — start the connected Clock to plot." : "Connection failed";
            LastError = Myo.LastConnectionError;
        }
        catch (Exception ex) { LastError = ex.Message; Status = "Error"; }
        finally { IsBusy = false; }
    }
    private bool CanConnect() => !string.IsNullOrWhiteSpace(SelectedDevice) && !IsConnected;
    partial void OnSelectedDeviceChanged(string? value) => ConnectCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await Myo.DisconnectAsync().ConfigureAwait(false);
            IsConnected = false;
            Status = "Disconnected";
        }
        catch (Exception ex) { LastError = ex.Message; Status = "Error"; }
        finally { IsBusy = false; }
    }
    private bool CanDisconnect() => IsConnected;
}
