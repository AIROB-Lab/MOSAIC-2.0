using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.Devices
{
    /// <summary>
    /// ViewModel for the <see cref="MOSAIC.Models.Devices.SiFi"/> device block card.
    /// </summary>
    /// <remarks>
    /// Sensor toggles (<see cref="EmgEnabled"/>, <see cref="ImuEnabled"/>, etc.) are
    /// editable before connecting. On connect, they are combined into
    /// <see cref="MOSAIC.Models.Devices.SiFi.EnabledSensors"/> flags. After connecting, the toggles are read-only.
    /// </remarks>
    public partial class SifiViewModel : ObservableObject
    {
        /// <summary>The underlying Sifi block.</summary>
        public SiFi Block { get; }

        /// <summary>Discovered BLE devices.</summary>
        public ObservableCollection<string> FoundDevices => Block.FoundDevices;

        /// <summary>Currently selected device in the ComboBox.</summary>
        [ObservableProperty] private string? _selectedDevice;

        // ── Proxied block properties ──

        public bool IsConnected => Block.IsConnected;
        public bool IsStreaming => Block.IsStreaming;
        public int BatteryLevel => Block.BatteryLevel;
        public string DeviceStatus => Block.DeviceStatus;
        public bool HasBattery => Block.BatteryLevel >= 0;

        // ── Per-sensor scopes (bound in the view) ──

        public ScopeMonitor ScopeImu => Block.ScopeImu;
        public ScopeMonitor ScopeEcg => Block.ScopeEcg;
        public ScopeMonitor ScopeEda => Block.ScopeEda;
        public ScopeMonitor ScopePpg => Block.ScopePpg;

        // ── Sensor toggles (editable before connect) ──

        [ObservableProperty] private bool _emgEnabled = true;
        [ObservableProperty] private bool _imuEnabled;
        [ObservableProperty] private bool _ecgEnabled;
        [ObservableProperty] private bool _edaEnabled;
        [ObservableProperty] private bool _ppgEnabled;

        /// <summary>Whether a long-running operation is in progress.</summary>
        [ObservableProperty] private bool _isBusy;

        /// <summary>Last error message for UI display.</summary>
        [ObservableProperty] private string _lastError = "";

        public SifiViewModel(SiFi block)
        {
            Block = block ?? throw new ArgumentNullException(nameof(block));

            // Sync toggles from block's current flags
            var f = Block.EnabledSensors;
            _emgEnabled = f.HasFlag(SiFi.SensorFlags.Emg);
            _imuEnabled = f.HasFlag(SiFi.SensorFlags.Imu);
            _ecgEnabled = f.HasFlag(SiFi.SensorFlags.Ecg);
            _edaEnabled = f.HasFlag(SiFi.SensorFlags.Eda);
            _ppgEnabled = f.HasFlag(SiFi.SensorFlags.Ppg);

            Block.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(SiFi.IsConnected):
                        OnPropertyChanged(nameof(IsConnected));
                        ToggleConnectCommand.NotifyCanExecuteChanged();
                        break;
                    case nameof(SiFi.IsStreaming):
                        OnPropertyChanged(nameof(IsStreaming));
                        break;
                    case nameof(SiFi.BatteryLevel):
                        OnPropertyChanged(nameof(BatteryLevel));
                        OnPropertyChanged(nameof(HasBattery));
                        break;
                    case nameof(SiFi.DeviceStatus):
                        OnPropertyChanged(nameof(DeviceStatus));
                        break;
                }
            };
        }

        /// <summary>Syncs toggle booleans → block's EnabledSensors flags.</summary>
        private void SyncSensorFlags()
        {
            var f = SiFi.SensorFlags.None;
            if (EmgEnabled) f |= SiFi.SensorFlags.Emg;
            if (ImuEnabled) f |= SiFi.SensorFlags.Imu;
            if (EcgEnabled) f |= SiFi.SensorFlags.Ecg;
            if (EdaEnabled) f |= SiFi.SensorFlags.Eda;
            if (PpgEnabled) f |= SiFi.SensorFlags.Ppg;
            Block.EnabledSensors = f;
        }

        /// <summary>Scans for nearby SiFi BLE devices.</summary>
        [RelayCommand]
        private async Task StartScan()
        {
            if (IsBusy) return;
            IsBusy = true;
            LastError = "";

            try
            {
                await Block.ScanAsync();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"[SifiVM] Scan error: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Connects or disconnects.</summary>
        [RelayCommand(CanExecute = nameof(CanToggleConnect))]
        private async Task ToggleConnect()
        {
            if (IsBusy) return;
            IsBusy = true;
            LastError = "";

            try
            {
                if (Block.IsConnected)
                {
                    await Block.DisconnectAsync();
                }
                else
                {
                    // Sync sensor flags from UI toggles before connecting
                    SyncSensorFlags();

                    var target = SelectedDevice;

                    if (string.IsNullOrWhiteSpace(target))
                    {
                        if (!string.IsNullOrWhiteSpace(Block.MacAddress))
                        {
                            await Block.ConnectWithConfigMacAsync();
                            return;
                        }
                        LastError = "No device selected";
                        return;
                    }

                    var success = await Block.ConnectAsync(target);
                    if (!success) LastError = "Connection failed";
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"[SifiVM] Connect error: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanToggleConnect()
            => Block.IsConnected || SelectedDevice is not null || !string.IsNullOrWhiteSpace(Block.MacAddress);

        /// <summary>Requests battery/status update.</summary>
        [RelayCommand]
        private async Task RefreshStatus()
        {
            try { await Block.RequestStatusAsync(); }
            catch (Exception ex) { LastError = ex.Message; }
        }

        partial void OnSelectedDeviceChanged(string? value)
            => ToggleConnectCommand.NotifyCanExecuteChanged();
    }
}