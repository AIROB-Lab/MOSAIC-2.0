using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Diagnostics;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the <see cref="Delsys"/> source block.
/// Supports per-sensor selection and mode, per-signal-type visualization
/// (EMG / Acc / Gyro / Orientation), and channel labelling.
/// </summary>
public partial class DelsysViewModel(Delsys delsys) : ObservableObject
{
    // ---- Model ----
    public Delsys Delsys => delsys;

    // ---- Header bindings ----
    public string Name          => Delsys.Name;
    public string FrequencyText => Delsys.FrequencyText;

    // ---- UI state ----
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _isArmed;
    [ObservableProperty] private bool   _isStreaming;
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private string? _lastError;

    // ── Visualization bundles (Scope + Spider + Heatmap per signal type) ──
    // XAML: <viz:VisualizationPanel Source="{Binding VizEmg}" ... />

    public BlockVisualization VizEmg         { get; } = new();
    public BlockVisualization VizAcc         { get; } = new();
    public BlockVisualization VizGyro        { get; } = new();
    public BlockVisualization VizOrientation { get; } = new();

    // ---- Channel labels with scope colors (populated after Arm) ----
    public ObservableCollection<ScopeLegendItem> EmgChannelLabels         { get; } = new();
    public ObservableCollection<ScopeLegendItem> AccChannelLabels         { get; } = new();
    public ObservableCollection<ScopeLegendItem> GyroChannelLabels        { get; } = new();
    public ObservableCollection<ScopeLegendItem> OrientationChannelLabels { get; } = new();

    // ===== Construction wiring =====

    /// <summary>
    /// Call once after construction to wire visualization bundles to the model.
    /// Typically done by the ViewLocator or wherever the VM is created.
    /// </summary>
    public void Initialize()
    {
        // Wire viz bundles to the model so OnReceive/PublishVector can feed them
        Delsys.VizEmg         = VizEmg;
        Delsys.VizAcc         = VizAcc;
        Delsys.VizGyro        = VizGyro;
        Delsys.VizOrientation = VizOrientation;
    }

    // ===== Commands =====

    /// <summary>Initialize the Delsys device source and API pipeline.</summary>
    [RelayCommand]
    private void InitializeDevice()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Console.WriteLine("[DelsysVM] InitializeDevice() called");
            Delsys.InitializeDeviceSource();
            Status = "Initialized";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Error";
            Log.Error("DelsysVM", ex, "InitializeDevice() FAILED.");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Scan for paired Trigno RF sensors.</summary>
    [RelayCommand]
    private async Task StartScan()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Console.WriteLine("[DelsysVM] StartScan() called");
            await Delsys.ScanAsync();

            Console.WriteLine($"[DelsysVM] ScanAsync returned. DiscoveredSensors.Count={Delsys.DiscoveredSensors.Count}");
            foreach (var s in Delsys.DiscoveredSensors)
                Console.WriteLine($"[DelsysVM]   Sensor: \"{s.Name}\" SID={s.Sid} Pair#{s.PairNumber} Modes={s.SampleModes.Count}");

            Status = $"Scanned ({Delsys.DiscoveredSensors.Count} sensor(s))";
            ArmCommand.NotifyCanExecuteChanged();
            ApplyModeToAllCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Error";
            Log.Error("DelsysVM", ex, "StartScan() FAILED.");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Copy the first sensor's selected mode to all other sensors.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyModeToAll))]
    private void ApplyModeToAll()
    {
        Console.WriteLine($"[DelsysVM] ApplyModeToAll() — first sensor mode index = {Delsys.DiscoveredSensors[0].SelectedModeIndex}");
        Delsys.ApplyFirstSensorModeToAll();
    }
    private bool CanApplyModeToAll() => Delsys.DiscoveredSensors.Count >= 2;

    /// <summary>
    /// Arm the pipeline. Uses each sensor's IsSelected and SelectedModeIndex
    /// from the DiscoveredSensors list (bound per-sensor in the UI).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanArm))]
    private async Task Arm()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var selectedCount = Delsys.DiscoveredSensors.Count(s => s.IsSelected);
            Console.WriteLine($"[DelsysVM] Arm() called — {selectedCount} sensor(s) selected");

            await Delsys.ArmAsync();

            IsArmed = Delsys.IsArmed;
            Status  = IsArmed ? "Armed" : "Arm failed (none selected?)";

            // Populate channel labels with scope trace colors
            EmgChannelLabels.Clear();
            AccChannelLabels.Clear();
            GyroChannelLabels.Clear();
            OrientationChannelLabels.Clear();

            int emgIdx = 0;
            foreach (var cl in Delsys.ChannelLabels.Where(c => c.IsEmg))
            {
                EmgChannelLabels.Add(new ScopeLegendItem(
                    cl.Label, ScopeMonitor.GetChannelColorHex(emgIdx)));
                emgIdx++;
            }

            int accIdx = 0;
            foreach (var cl in Delsys.ChannelLabels.Where(c => c.IsAcc))
            {
                AccChannelLabels.Add(new ScopeLegendItem(
                    cl.Label, ScopeMonitor.GetChannelColorHex(accIdx)));
                accIdx++;
            }

            int gyroIdx = 0;
            foreach (var cl in Delsys.ChannelLabels.Where(c => c.IsGyro))
            {
                GyroChannelLabels.Add(new ScopeLegendItem(
                    cl.Label, ScopeMonitor.GetChannelColorHex(gyroIdx)));
                gyroIdx++;
            }

            int oriIdx = 0;
            foreach (var cl in Delsys.ChannelLabels.Where(c => c.IsOrientation))
            {
                OrientationChannelLabels.Add(new ScopeLegendItem(
                    cl.Label, ScopeMonitor.GetChannelColorHex(oriIdx)));
                oriIdx++;
            }

            Console.WriteLine($"[DelsysVM] Arm done. EMG={EmgChannelLabels.Count}, " +
                              $"ACC={AccChannelLabels.Count}, GYRO={GyroChannelLabels.Count}, " +
                              $"ORI={OrientationChannelLabels.Count}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Error";
            Log.Error("DelsysVM", ex, "Arm() FAILED.");
        }
        finally
        {
            IsBusy = false;
            ArmCommand.NotifyCanExecuteChanged();
            ToggleStreamCommand.NotifyCanExecuteChanged();
        }
    }
    private bool CanArm() => Delsys.DiscoveredSensors.Count > 0 && !IsArmed;

    /// <summary>Toggle streaming on/off.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleStream))]
    private async Task ToggleStream()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!IsStreaming)
            {
                Delsys.StartStream();
                IsStreaming = true;
                Status      = "Streaming";
            }
            else
            {
                await Delsys.StopStreamAsync();
                IsStreaming = false;
                Status      = "Stopped";
            }
        }
        catch (Exception ex) { LastError = ex.Message; Status = "Error"; }
        finally
        {
            IsBusy = false;
            ToggleStreamCommand.NotifyCanExecuteChanged();
        }
    }
    private bool CanToggleStream() => IsArmed;

    /// <summary>Full pipeline reset.</summary>
    [RelayCommand]
    private async Task Reset()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await Delsys.ResetAsync();
            EmgChannelLabels.Clear();
            AccChannelLabels.Clear();
            GyroChannelLabels.Clear();
            OrientationChannelLabels.Clear();
            IsArmed     = false;
            IsStreaming = false;
            Status      = "Reset";
        }
        catch (Exception ex) { LastError = ex.Message; Status = "Error"; }
        finally
        {
            IsBusy = false;
            ArmCommand.NotifyCanExecuteChanged();
            ToggleStreamCommand.NotifyCanExecuteChanged();
            ApplyModeToAllCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Export collected data to CSV.</summary>
    [RelayCommand]
    private void Export()
    {
        try
        {
            var path = Delsys.ExportData();
            Status = path is not null ? $"Exported → {path}" : "Nothing to export";
        }
        catch (Exception ex) { LastError = ex.Message; Status = "Export error"; }
    }

    // ---- CanExecute notifications ----
    partial void OnIsArmedChanged(bool value)
    {
        ArmCommand.NotifyCanExecuteChanged();
        ToggleStreamCommand.NotifyCanExecuteChanged();
    }
}