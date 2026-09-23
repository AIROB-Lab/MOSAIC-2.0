using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Devices.Wulpus;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization.Heatmap;
using MOSAIC.Visualization.SnapshotMonitor;
using Python.Runtime;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Pipeline block that integrates the Wulpus ultrasound dongle via Python (pythonnet).
/// </summary>
/// <remarks>
/// <para>
/// Uses the <c>wulpus</c> Python package to communicate with the Wulpus hardware
/// (dongle, RX/TX and USS configurations). The Python runtime is managed by
/// <see cref="MOSAIC.Components.Manager.Python.PythonNetManager"/>.
/// </para>
/// <para>
/// <b>Dual-mode triggering:</b> When a <c>ClockBlock</c> is wired as input, acquisition
/// is timer-driven (one read per tick). When no inputs are configured, a dedicated
/// background thread runs a self-triggering read loop — no external timer needed.
/// </para>
/// <para>
/// <b>Data sending modes:</b>
/// <list type="bullet">
/// <item><description><see cref="DataSendingMode.SingleAcquisitionVector"/> (0): Each acquisition
/// is sent as a <see cref="Vector"/> regardless of config index.</description></item>
/// <item><description><see cref="DataSendingMode.CompleteFrameMatrix"/> (1): Waits for a full
/// frame (all TX/RX configs), then sends as a <see cref="Matrix"/>.</description></item>
/// <item><description><see cref="DataSendingMode.UpdateSingleAcqMatrix"/> (2): Builds and sends
/// an updating <see cref="Matrix"/> on every acquisition for higher frame rate.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>JSON configuration:</b>
/// <code>
/// "wulpusPy": {
///   "Type": "WulpusPython",
///   "Inputs": [],
///   "Params": [
///     "C:\\path\\to\\uss_config.json",
///     "C:\\path\\to\\tx_rx_config.json",
///     1,
///     false
///   ]
/// }
/// </code>
/// Params: [ ussConfigPath, rxTxConfigPath, dataSendingMode (0|1|2), appendMetadata (bool) ]
/// </para>
/// </remarks>
public sealed partial class Wulpus : BaseBlock
{
    #region Enums

    /// <summary>
    /// Mode controlling how acquired data is assembled and published downstream.
    /// </summary>
    public enum DataSendingMode
    {
        /// <summary>Each A-mode acquisition published as a <see cref="Vector"/>.</summary>
        SingleAcquisitionVector = 0,

        /// <summary>Full frame (all configs) assembled, then published as a <see cref="Matrix"/>.</summary>
        CompleteFrameMatrix = 1,

        /// <summary>Matrix updated per-acquisition and published every time for higher frame rate.</summary>
        UpdateSingleAcqMatrix = 2
    }

    #endregion

    #region Constants

    /// <summary>
    /// Maximum time to wait for the self-trigger thread to terminate during shutdown.
    /// </summary>
    private const int ThreadJoinTimeoutMs = 3000;

    #endregion

    #region Fields

    private readonly Connection _connection;
    private readonly IDataAssembler _assembler;

    // Self-triggering mode
    private Thread? _readThread;
    private volatile bool _running;

    // Counters
    private int _dataCount;
    private bool _noStreamWarning;

    #endregion

    #region Observable Properties

    /// <summary>Data assembly and publishing mode.</summary>
    [ObservableProperty] private DataSendingMode _sendingMode = DataSendingMode.SingleAcquisitionVector;

    /// <summary>Whether to append Wulpus metadata (config index, acquisition counter) to output.</summary>
    [ObservableProperty] private bool _appendMetadata = true;

    /// <summary>Whether the block is running in self-triggered mode (no external timer).</summary>
    [ObservableProperty] private bool _isSelfTriggered;

    /// <summary>Total acquisitions processed.</summary>
    [ObservableProperty] private long _acquisitionCount;

    /// <summary>Index of the currently visualized config (for single-channel display).</summary>
    [ObservableProperty] private int _visualizedConfig;

    #endregion

    #region Delegated Properties (from Connection)

    /// <summary>Path to the USS configuration JSON file.</summary>
    public string UssConfigPath => _connection.UssConfigPath;

    /// <summary>Path to the RX/TX configuration JSON file.</summary>
    public string RxTxConfigPath => _connection.RxTxConfigPath;

    /// <summary>Number of TX/RX channel configurations.</summary>
    public int NumChannelConfigs => _connection.NumChannelConfigs;

    /// <summary>Number of samples per acquisition.</summary>
    public int NumSamples => _connection.NumSamples;

    /// <summary>Whether a COM (dongle) connection is currently open.</summary>
    public bool IsConnected => _connection.IsConnected;

    /// <summary>Whether data streaming is currently active.</summary>
    public bool IsStreaming => _connection.IsStreaming;

    /// <summary>Currently connected COM port name.</summary>
    public string ConnectedPort => _connection.ConnectedPort;

    /// <summary>Human-readable descriptions of found devices.</summary>
    public List<string> FoundDevices => _connection.FoundDevices;

    #endregion

    #region Public Surface

    /// <summary>Standalone heatmap for B-mode display (configs × samples grid).</summary>
    public HeatMapMonitor Heatmap { get; } = new();

    /// <summary>Standalone A-mode snapshot monitor (spatial waveform, not time-series).</summary>
    public SnapshotMonitor Snapshot { get; } = new();

    /// <summary>The underlying connection for direct device control.</summary>
    public Connection Connection => _connection;

    #endregion

    #region Constructor & Factory

    /// <summary>
    /// Initializes a new <see cref="Wulpus"/> block.
    /// </summary>
    /// <param name="name">Block name used in the pipeline.</param>
    /// <param name="desiredRate">Desired execution rate in Hz.</param>
    /// <param name="ussConfigPath">Path to USS configuration JSON.</param>
    /// <param name="rxTxConfigPath">Path to RX/TX configuration JSON.</param>
    /// <param name="sendingMode">Data sending mode (0, 1, or 2).</param>
    /// <param name="appendMetadata">Whether to append Wulpus metadata to output.</param>
    public Wulpus(
        string name,
        double desiredRate,
        string ussConfigPath = "",
        string rxTxConfigPath = "",
        DataSendingMode sendingMode = DataSendingMode.SingleAcquisitionVector,
        bool appendMetadata = true)
        : base(name, desiredRate)
    {
        _sendingMode = sendingMode;
        _appendMetadata = appendMetadata;

        _connection = new Connection(name, ussConfigPath, rxTxConfigPath);
        _assembler = DataAssemblerFactory.Create(sendingMode);
        _assembler.Reset(_connection);

        Heatmap.UseGlobalAuto();
    }

    /// <summary>
    /// Creates a <see cref="Wulpus"/> block from JSON pipeline configuration.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model containing block configuration.</param>
    /// <returns>A fully configured <see cref="Wulpus"/> block.</returns>
    public static Wulpus ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name     = m.Name ?? "WulpusPython";
        var rate     = m.DesiredRate ?? 0;
        var ussPath  = ResolveConfigParam(m.Params?.Count > 0 ? GetString(m.Params[0], "") ?? "" : "");
        var rxtxPath = ResolveConfigParam(m.Params?.Count > 1 ? GetString(m.Params[1], "") ?? "" : "");
        var mode     = m.Params?.Count > 2 ? (DataSendingMode)GetInt(m.Params[2], 0) : DataSendingMode.SingleAcquisitionVector;
        var meta     = m.Params?.Count > 3 ? GetBool(m.Params[3], true) : true;

        var block = ActivatorUtilities.CreateInstance<Wulpus>(
            sp, name, rate, ussPath, rxtxPath, mode, meta);

        block.IsSelfTriggered = m.Inputs is null || m.Inputs.Count == 0;

        return block;
    }

    /// <summary>
    /// Resolves a USS or TX/RX config path from the pipeline JSON against the application folder.
    /// </summary>
    /// <param name="path">The raw <c>Params</c> value. Absolute paths and empty strings pass through.</param>
    /// <returns>An absolute path, or the input unchanged when it is empty or already rooted.</returns>
    /// <remarks>
    /// A pipeline shared between machines cannot name an absolute path, and the presets ship beside
    /// the executable, so a relative entry like <c>Assets/WulpusConfigs/uss_config_hand_tests.json</c>
    /// is resolved from there rather than from the working directory - which is wherever the app
    /// happened to be launched from, and is not the same thing.
    /// </remarks>
    private static string ResolveConfigParam(string path)
        => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    #endregion

    #region JSON Export

    /// <inheritdoc/>
    protected override string JsonTypeName => "WulpusPython";

    /// <inheritdoc/>
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { UssConfigPath, RxTxConfigPath, (int)SendingMode, AppendMetadata };

    #endregion

    #region Device Control (delegated to Connection)

    /// <summary>Scans for available Wulpus devices.</summary>
    /// <returns>Number of devices found.</returns>
    public int ScanDevices()
    {
        var count = _connection.ScanDevices();
        OnPropertyChanged(nameof(FoundDevices));
        return count;
    }

    /// <summary>Opens a COM connection to the device at the specified index.</summary>
    /// <param name="deviceIndex">Index into <see cref="FoundDevices"/>.</param>
    public void OpenPort(int deviceIndex)
    {
        if (_connection.OpenPort(deviceIndex))
        {
            _noStreamWarning = false;
        }
        NotifyConnectionStateChanged();
    }

    /// <summary>Closes the current COM connection and stops streaming.</summary>
    public void ClosePort()
    {
        _connection.ClosePort();
        NotifyConnectionStateChanged();
    }

    /// <summary>Loads and applies a USS configuration JSON file.</summary>
    /// <param name="configPath">Path to the USS config JSON.</param>
    public void LoadUssConfig(string configPath)
    {
        _connection.LoadUssConfig(configPath);
        _assembler.Reset(_connection);
        NotifyConfigChanged();
    }

    /// <summary>Loads and applies a RX/TX configuration JSON file.</summary>
    /// <param name="rxtxPath">Path to the RX/TX config JSON.</param>
    public void LoadRxTxConfig(string rxtxPath)
    {
        _connection.LoadRxTxConfig(rxtxPath);
        _assembler.Reset(_connection);
        NotifyConfigChanged();
    }

    #endregion

    #region Streaming Control

    /// <summary>
    /// Starts streaming: sends config to the dongle, then either launches a
    /// self-triggering read loop or waits for external timer ticks.
    /// </summary>
    public void StartStreaming()
    {
        if (!_connection.StartStreaming()) return;

        _dataCount = 0;
        _assembler.Reset(_connection);

        if (IsSelfTriggered)
        {
            _running = true;
            _readThread = new Thread(SelfTriggerLoop)
            {
                IsBackground = true,
                Name = $"Wulpus-{Name}",
                Priority = ThreadPriority.AboveNormal
            };
            _readThread.Start();
        }

        Debug.WriteLine($"[{Name}] Streaming started (mode={(_isSelfTriggered ? "self-triggered" : "timer-driven")}).");
        NotifyConnectionStateChanged();
    }

    /// <summary>
    /// Stops streaming and shuts down the self-trigger thread if running.
    /// </summary>
    public void StopStreaming()
    {
        if (!IsStreaming) return;

        _running = false;
        _readThread?.Join(ThreadJoinTimeoutMs);
        _readThread = null;

        _connection.StopStreaming();
        Debug.WriteLine($"[{Name}] Total acquisitions: {_dataCount}");
        NotifyConnectionStateChanged();
    }

    #endregion

    #region Data Acquisition

    /// <summary>
    /// Self-triggering read loop running on a background thread.
    /// </summary>
    private void SelfTriggerLoop()
    {
        Debug.WriteLine($"[{Name}] Self-trigger loop started.");
        while (_running)
        {
            try
            {
                ProcessOutput();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{Name}] Read loop error: {ex.Message}");
                Thread.Sleep(1);
            }
        }
        Debug.WriteLine($"[{Name}] Self-trigger loop ended.");
    }

    /// <summary>
    /// Performs a single acquisition cycle: reads data via the assembler,
    /// optionally crops metadata, publishes downstream, and feeds visualization.
    /// </summary>
    private void ProcessOutput()
    {
        object? result;

        using (Py.GIL())
        {
            result = _assembler.Assemble(_connection);
        }

        if (result is null) return;

        var output = AppendMetadata ? result : _assembler.CropMetadata(result, _connection);
        if (output is null) return;

        Publish(output);
        AcquisitionCount = ++_dataCount;

        // Feed visualization
        if (output is Vector v)
        {
            Heatmap.EnqueueFrame(v);
            Snapshot.EnqueueSnapshot(v);
        }
        else if (output is Matrix mat)
        {
            // Auto-configure heatmap grid; skip frame on dimension change
            if (Heatmap.Rows != mat.RowCount || Heatmap.Columns != mat.ColumnCount)
                Heatmap.ConfigureGrid(mat.RowCount, mat.ColumnCount);
            else
                Heatmap.EnqueueFrame(mat.ToRowMajorArray().AsSpan());

            Snapshot.EnqueueSnapshot(mat);
        }
    }

    #endregion

    #region OnReceive (Timer-driven mode)

    /// <summary>
    /// Called when the upstream timer fires. Only active in timer-driven mode.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        if (!IsStreaming)
        {
            if (!_noStreamWarning)
            {
                Debug.WriteLine($"[{Name}] Streaming not started. Start streaming, then restart the timer.");
                _noStreamWarning = true;
            }
            return;
        }

        // Flush input buffer on first tick to discard stale dongle data
        if (_dataCount == 0)
            _connection.FlushInputBuffer();

        ProcessOutput();
    }

    #endregion

    #region Property Change Helpers

    /// <summary>
    /// Raises <see cref="ObservableObject.PropertyChanged"/> for all delegated connection-state
    /// properties so the UI (and ViewModel commands) can react.
    /// </summary>
    private void NotifyConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(ConnectedPort));
    }

    /// <summary>
    /// Raises <see cref="ObservableObject.PropertyChanged"/> for all delegated config properties.
    /// </summary>
    private void NotifyConfigChanged()
    {
        OnPropertyChanged(nameof(UssConfigPath));
        OnPropertyChanged(nameof(RxTxConfigPath));
        OnPropertyChanged(nameof(NumChannelConfigs));
        OnPropertyChanged(nameof(NumSamples));
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Disposes resources: stops streaming, closes port, and cleans up visualization.
    /// </summary>
    public override void Dispose()
    {
        StopStreaming();
        _connection.Dispose();
        Heatmap.Dispose();
        Snapshot.Dispose();
        base.Dispose();
    }

    #endregion
}
