using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Models.Devices;

namespace MOSAIC.ViewModels.Devices;

public partial class HannesHandViewModel : ObservableObject
{
    private readonly HannesHand _hannes;

    public HannesHand HannesHand => _hannes;

    // === Platform Detection ===
    public static bool IsDesktop { get; } = 
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    
    public static bool IsMobile { get; } = 
        OperatingSystem.IsAndroid() || 
        OperatingSystem.IsIOS();

    // Show sliders on desktop, buttons on mobile
    public bool UseSliderControl => IsDesktop;
    public bool UseButtonControl => IsMobile;

    // === Connection Status ===
    [ObservableProperty] private string _portName = "";
    [ObservableProperty] private string _dongleStatus = "Disconnected";
    [ObservableProperty] private string _dongleStatusColor = "#808080";  // Gray
    [ObservableProperty] private bool _isDongleConnected;
    
    [ObservableProperty] private string _hannesStatus = "Disconnected";
    [ObservableProperty] private string _hannesStatusColor = "#808080";  // Gray

    // Device discovery
    public ObservableCollection<string> FoundDevices { get; } = new();
    [ObservableProperty] private string? _selectedDevice;

    // Joint toggles (which joints are active)
    [ObservableProperty] private bool _wristExFlex;
    [ObservableProperty] private bool _wristSupPro;
    [ObservableProperty] private bool _handOpenClose;
    [ObservableProperty] private bool _thumb;

    // === Control Mode ===
    [ObservableProperty] 
    private bool _isManualMode;

    public string ControlModeText => IsManualMode ? "Manual Control" : "Input Control";

    // === Manual Control Values ===
    // Position controlled (slider 0-100)
    [ObservableProperty] private double _handValue;       // 0=open, 100=closed
    [ObservableProperty] private double _wristFeValue = 50;   // 0=flexed, 100=extended
    
    // Velocity controlled 
    [ObservableProperty] private double _wristPsValue;    // -100=sup, 0=stop, +100=pro
    
    // Thumb: single speed, direction only (hold buttons)
    [ObservableProperty] private double _thumbValue;      // -100=open, 0=stop, +100=close

    public HannesHandViewModel(HannesHand hannes)
    {
        _hannes = hannes;

        // Initialize toggles from model
        _wristExFlex = hannes.WristExFlex;
        _wristSupPro = hannes.WristSupPro;
        _handOpenClose = hannes.HandOpenClose;
        _thumb = hannes.Thumb;

        System.Diagnostics.Debug.WriteLine($"[HannesVM] Created for '{hannes.Name}'");
        System.Diagnostics.Debug.WriteLine($"[HannesVM] Platform: Desktop={IsDesktop}, Mobile={IsMobile}");
    }

    // === Control Mode ===
    partial void OnIsManualModeChanged(bool value)
    {
        _hannes.IsManualMode = value;
        OnPropertyChanged(nameof(ControlModeText));
        
        if (value)
        {
            // Reset velocity controls to stopped when entering manual mode
            WristPsValue = 0;
            ThumbValue = 0;
            SendManualValues();
        }
        
        System.Diagnostics.Debug.WriteLine($"[HannesVM] Control mode: {(value ? "Manual" : "Input")}");
    }

    // === Slider/Button Changes ===
    partial void OnHandValueChanged(double value) => SendManualValuesIfManual();
    partial void OnWristFeValueChanged(double value) => SendManualValuesIfManual();
    partial void OnWristPsValueChanged(double value) => SendManualValuesIfManual();
    partial void OnThumbValueChanged(double value) => SendManualValuesIfManual();

    private void SendManualValuesIfManual()
    {
        if (IsManualMode)
            SendManualValues();
    }

    private void SendManualValues()
    {
        // Create vector: [Hand, WristFE, WristPS, Thumb]
        // All values normalized to 0-1 range for SendSimultaneousRefs
        var refs = Vector<double>.Build.Dense(new[]
        {
            HandValue / 100.0,              // 0-1 (position)
            WristFeValue / 100.0,           // 0-1 (position)
            (WristPsValue + 100) / 200.0,   // -100..+100 → 0..1 (velocity)
            (ThumbValue + 100) / 200.0      // -100..+100 → 0..1 (direction only)
        });
        
        _hannes.SendManualRefs(refs);
    }

    [RelayCommand]
    private void CenterSliders()
    {
        HandValue = 0;
        WristFeValue = 50;
        WristPsValue = 0;
        ThumbValue = 0;
    }

    // Thumb button commands (for press-and-hold via PointerPressed/Released)
    public void ThumbOpen() 
    {
        ThumbValue = -100;
        System.Diagnostics.Debug.WriteLine($"[HannesVM] ThumbOpen: ThumbValue={ThumbValue}");
    }
    
    public void ThumbClose() 
    {
        ThumbValue = 100;
        System.Diagnostics.Debug.WriteLine($"[HannesVM] ThumbClose: ThumbValue={ThumbValue}");
    }
    
    public void ThumbStop() 
    {
        ThumbValue = 0;
        System.Diagnostics.Debug.WriteLine($"[HannesVM] ThumbStop: ThumbValue={ThumbValue}");
    }

    // ==== Connection Commands ====

    [RelayCommand(CanExecute = nameof(CanConnectDongle))]
    private async Task ConnectDongle()
    {
        System.Diagnostics.Debug.WriteLine($"[HannesVM] ConnectDongle, PortName='{PortName}'");
        
        if (string.IsNullOrWhiteSpace(PortName))
        {
            DongleStatus = "Enter port!";
            DongleStatusColor = "#FF4444";  // Red
            return;
        }
        
        _hannes.PortName = PortName;
        DongleStatus = "Connecting...";
        DongleStatusColor = "#FFAA00";  // Orange
        
        try
        {
            var ok = await _hannes.ConnectDongleAsync();
            DongleStatus = ok ? "Connected" : "Failed";
            DongleStatusColor = ok ? "#00FF88" : "#FF4444";  // Green or Red
            IsDongleConnected = ok;
        }
        catch (System.Exception ex)
        {
            DongleStatus = "Error";
            DongleStatusColor = "#FF4444";  // Red
            IsDongleConnected = false;
            System.Diagnostics.Debug.WriteLine($"[HannesVM] Exception: {ex.Message}");
        }
    }

    private bool CanConnectDongle() => !IsDongleConnected;

    partial void OnIsDongleConnectedChanged(bool value) => ConnectDongleCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task StartScan()
    {
        System.Diagnostics.Debug.WriteLine($"[HannesVM] StartScan called");
        FoundDevices.Clear();
        
        try
        {
            var devices = await _hannes.ScanForDevicesAsync();
            System.Diagnostics.Debug.WriteLine($"[HannesVM] Scan returned {devices.Count} devices");
            
            foreach (var d in devices)
            {
                System.Diagnostics.Debug.WriteLine($"[HannesVM] Adding device: {d}");
                FoundDevices.Add(d);
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HannesVM] StartScan exception: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectHannes))]
    private async Task ConnectHannes()
    {
        _hannes.SelectedDevice = SelectedDevice;
        HannesStatus = "Connecting...";
        HannesStatusColor = "#FFAA00";  // Orange
        
        var ok = await _hannes.ConnectHannesAsync();
        HannesStatus = ok ? "Connected" : "Failed";
        HannesStatusColor = ok ? "#00FF88" : "#FF4444";  // Green or Red
    }

    private bool CanConnectHannes() => !string.IsNullOrEmpty(SelectedDevice);

    partial void OnSelectedDeviceChanged(string? value) => ConnectHannesCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void DisconnectHannes()
    {
        _hannes.DisconnectHannes();
        HannesStatus = "Disconnected";
        HannesStatusColor = "#808080";  // Gray
    }

    // ==== Joint Toggle Hooks ====
    partial void OnWristExFlexChanged(bool value) => _hannes.WristExFlex = value;
    partial void OnWristSupProChanged(bool value) => _hannes.WristSupPro = value;
    partial void OnHandOpenCloseChanged(bool value) => _hannes.HandOpenClose = value;
    partial void OnThumbChanged(bool value) => _hannes.Thumb = value;
}