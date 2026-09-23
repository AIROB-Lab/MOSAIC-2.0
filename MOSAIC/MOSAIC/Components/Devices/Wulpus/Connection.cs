using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MOSAIC.Components.Manager.Python;
using Python.Runtime;

namespace MOSAIC.Components.Devices.Wulpus;

/// <summary>
/// Raw acquisition result from the Wulpus dongle: A-mode samples plus metadata.
/// </summary>
/// <param name="Samples">Raw A-mode sample buffer (short[]), length = <c>NumSamples + 2</c>.</param>
/// <param name="ConfigIndex">TX/RX configuration index for this acquisition.</param>
/// <param name="Counter">Dongle-side acquisition counter (for loss detection).</param>
public readonly record struct WulpusRawAcquisition(short[] Samples, int ConfigIndex, int Counter);

/// <summary>
/// Manages the Python-side Wulpus dongle lifecycle: initialization, device
/// discovery, COM port open/close, config loading, streaming control, and
/// single-acquisition reads.
/// </summary>
/// <remarks>
/// <para>
/// All Python calls are wrapped in <c>Py.GIL()</c>. Callers that invoke multiple
/// methods in sequence should consider holding the GIL externally if performance
/// is critical, but each public method is self-contained and safe to call individually.
/// </para>
/// <para>
/// Config loading uses a pythonnet workaround: the GUI class mutates its own
/// <c>__dict__</c> rather than the backend config instance, so we manually sync
/// via <c>__dict__.update()</c> after each load.
/// </para>
/// </remarks>
public sealed class Connection : IDisposable
{
    #region Constants

    /// <summary>
    /// Delay after sending the restart package before sending the config package.
    /// The dongle firmware needs time to reset its internal state machine.
    /// </summary>
    private const int DongleRestartDelayMs = 2500;

    #endregion

    #region Fields

    private dynamic _dongle;
    private dynamic _ussConfig;
    private dynamic _ussConfigGui;
    private dynamic _rxTxConfigGui;
    private dynamic _np;

    private int _lastCounter;
    private readonly string _ownerName;

    #endregion

    #region Properties

    /// <summary>Whether a COM (dongle) connection is currently open.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Whether data streaming is currently active.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>Currently connected COM port name.</summary>
    public string ConnectedPort { get; private set; } = "";

    /// <summary>Number of TX/RX channel configurations (derived from RX/TX config JSON).</summary>
    public int NumChannelConfigs { get; private set; } = 1;

    /// <summary>Number of samples per acquisition (derived from USS config).</summary>
    public int NumSamples { get; private set; } = 400;

    /// <summary>Human-readable descriptions of found devices.</summary>
    public List<string> FoundDevices { get; private set; } = new();

    /// <summary>Path to the currently loaded USS configuration JSON.</summary>
    public string UssConfigPath { get; private set; } = "";

    /// <summary>Path to the currently loaded RX/TX configuration JSON.</summary>
    public string RxTxConfigPath { get; private set; } = "";

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new connection wrapper and initializes the Python runtime.
    /// </summary>
    /// <param name="ownerName">Block name used in debug output.</param>
    /// <param name="ussConfigPath">Path to USS configuration JSON (may be empty).</param>
    /// <param name="rxTxConfigPath">Path to RX/TX configuration JSON (may be empty).</param>
    public Connection(string ownerName, string ussConfigPath = "", string rxTxConfigPath = "")
    {
        _ownerName = ownerName;
        InitializePython(ussConfigPath, rxTxConfigPath);
    }

    #endregion

    #region Python Initialization

    /// <summary>
    /// Initializes the Python runtime and all Wulpus Python objects.
    /// </summary>
    private void InitializePython(string ussConfigPath, string rxTxConfigPath)
    {
        var configPath = ResolvePythonConfigPath();
        PythonNetManager.Initialize(configPath);

        using (Py.GIL())
        {
            _np = Py.Import("numpy");

            dynamic dongleModule = Py.Import("wulpus.dongle");
            _dongle = dongleModule.WulpusDongle();

            dynamic ussModule = Py.Import("wulpus.uss_conf");
            _ussConfig = ussModule.WulpusUssConfig();

            dynamic ussGuiModule = Py.Import("wulpus.uss_conf_gui");
            _ussConfigGui = ussGuiModule.WulpusUssConfigGUI(_ussConfig);

            if (!string.IsNullOrEmpty(ussConfigPath))
                LoadUssConfig(ussConfigPath);

            dynamic rxtxGuiModule = Py.Import("wulpus.rx_tx_conf_gui");
            _rxTxConfigGui = rxtxGuiModule.WulpusRxTxConfigGenGUI();

            if (!string.IsNullOrEmpty(rxTxConfigPath))
                LoadRxTxConfig(rxTxConfigPath);

            NumSamples = (int)_ussConfig!.num_samples;
            ScanDevices();
        }

        Debug.WriteLine($"[{_ownerName}] Python initialized. Samples={NumSamples}, Configs={NumChannelConfigs}");
    }

    /// <summary>
    /// Locates <c>configWulpus.json</c>, beside the executable in a published build and in the
    /// source tree otherwise.
    /// </summary>
    /// <returns>Absolute path to the Wulpus Python config JSON.</returns>
    /// <exception cref="FileNotFoundException">The config was in neither location.</exception>
    /// <remarks>
    /// Wulpus has its own config because it needs Python 3.9, while the machine-learning blocks are
    /// built against 3.12. See <see cref="PythonNetManager.ResolveConfigPath"/> for the search order.
    /// </remarks>
    private static string ResolvePythonConfigPath()
        => PythonNetManager.ResolveConfigPath("configWulpus.json");

    #endregion

    #region Device Communication

    /// <summary>
    /// Scans for available Wulpus devices via the Python dongle API.
    /// </summary>
    /// <returns>Number of devices found.</returns>
    public int ScanDevices()
    {
        var descriptions = new List<string>();

        using (Py.GIL())
        {
            dynamic devices = _dongle!.get_available();
            if (devices is not null)
            {
                foreach (dynamic device in devices)
                    descriptions.Add((string)device.description);
            }
        }

        FoundDevices = descriptions;
        Debug.WriteLine($"[{_ownerName}] Found {descriptions.Count} device(s).");
        return descriptions.Count;
    }

    /// <summary>
    /// Opens a COM connection to the Wulpus device at the specified index.
    /// </summary>
    /// <param name="deviceIndex">Index into the <see cref="FoundDevices"/> list.</param>
    /// <returns><see langword="true"/> if the port was opened successfully.</returns>
    public bool OpenPort(int deviceIndex)
    {
        if (IsConnected) return true;

        using (Py.GIL())
        {
            try
            {
                dynamic devices = _dongle!.get_available();
                dynamic device = devices[deviceIndex];
                _dongle.open(device);
                ConnectedPort = (string)device.name;
                IsConnected = true;
                Debug.WriteLine($"[{_ownerName}] Opened {ConnectedPort}.");
                return true;
            }
            catch (PythonException ex)
            {
                Debug.WriteLine($"[{_ownerName}] Python error opening port: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_ownerName}] Error opening port: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Closes the currently open COM connection and stops streaming if active.
    /// </summary>
    public void ClosePort()
    {
        if (!IsConnected) return;

        StopStreaming();

        using (Py.GIL())
        {
            try
            {
                _dongle.send_config(_ussConfig!.get_restart_package());
                _dongle.close();
                ConnectedPort = "";
                IsConnected = false;
                Debug.WriteLine($"[{_ownerName}] Port closed.");
            }
            catch (PythonException ex) { Debug.WriteLine($"[{_ownerName}] Python error: {ex.Message}"); }
            catch (Exception ex) { Debug.WriteLine($"[{_ownerName}] Error: {ex.Message}"); }
        }
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Loads and applies a USS configuration JSON file.
    /// </summary>
    /// <remarks>Must be called under GIL if called from within an existing GIL block.</remarks>
    /// <param name="configPath">Path to the USS config JSON.</param>
    public void LoadUssConfig(string configPath)
    {
        using (Py.GIL())
        {
            try
            {
                _ussConfigGui!.with_file(configPath);
                // Pythonnet workaround: GUI mutates its own __dict__, not the backend's.
                _ussConfig!.__dict__.update(_ussConfigGui.__dict__);
                NumSamples = (int)_ussConfig.num_samples;
                UssConfigPath = configPath;
                Debug.WriteLine($"[{_ownerName}] Loaded USS config: {configPath}. MeasPeriod={_ussConfig.meas_period}");
            }
            catch (PythonException ex) { Debug.WriteLine($"[{_ownerName}] Python error loading USS config: {ex.Message}"); }
            catch (Exception ex) { Debug.WriteLine($"[{_ownerName}] Error loading USS config: {ex}"); }
        }
    }

    /// <summary>
    /// Loads and applies a RX/TX configuration JSON file.
    /// </summary>
    /// <remarks>Must be called under GIL if called from within an existing GIL block.</remarks>
    /// <param name="rxtxPath">Path to the RX/TX config JSON.</param>
    public void LoadRxTxConfig(string rxtxPath)
    {
        using (Py.GIL())
        {
            try
            {
                _rxTxConfigGui.with_file(rxtxPath);
                _ussConfig.tx_configs = _rxTxConfigGui.get_tx_configs();
                _ussConfig.rx_configs = _rxTxConfigGui.get_rx_configs();
                _ussConfig.num_txrx_configs = _ussConfig.tx_configs.__len__();
                NumChannelConfigs = (int)_ussConfig.num_txrx_configs;
                RxTxConfigPath = rxtxPath;
                Debug.WriteLine($"[{_ownerName}] Loaded RxTx config: {rxtxPath}. Configs={NumChannelConfigs}");
            }
            catch (Exception ex) { Debug.WriteLine($"[{_ownerName}] Error loading RxTx config: {ex}"); }
        }
    }

    #endregion

    #region Streaming Control

    /// <summary>
    /// Starts streaming: sends restart + config packages to the dongle.
    /// </summary>
    /// <returns><see langword="true"/> if streaming started successfully.</returns>
    public bool StartStreaming()
    {
        if (!IsConnected || IsStreaming) return false;

        using (Py.GIL())
        {
            try
            {
                Debug.WriteLine($"[{_ownerName}] RX={_ussConfig!.rx_configs}, TX={_ussConfig.tx_configs}");
                Debug.WriteLine($"[{_ownerName}] Configs={_ussConfig.num_txrx_configs}, MeasPeriod={_ussConfig.meas_period}");
                _dongle.acq_length = _ussConfig.num_samples;
                _dongle!.send_config(_ussConfig.get_restart_package());
                Thread.Sleep(DongleRestartDelayMs);
                _dongle.send_config(_ussConfig.get_conf_package());

                _lastCounter = 0;
                IsStreaming = true;
                Debug.WriteLine($"[{_ownerName}] Streaming started.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_ownerName}] Failed to start streaming: {ex}");
                IsStreaming = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Stops streaming by sending a restart package to the dongle.
    /// </summary>
    public void StopStreaming()
    {
        if (!IsStreaming) return;

        using (Py.GIL())
        {
            try { _dongle!.send_config(_ussConfig!.get_restart_package()); }
            catch { /* best effort */ }
        }

        IsStreaming = false;
        Debug.WriteLine($"[{_ownerName}] Streaming stopped.");
    }

    /// <summary>
    /// Flushes the serial input buffer. Call once before the first read to discard stale data.
    /// </summary>
    public void FlushInputBuffer()
    {
        using (Py.GIL())
        {
            try { _dongle!.__ser__.flushInput(); }
            catch { /* best effort */ }
        }
    }

    #endregion

    #region Data Acquisition

    /// <summary>
    /// Reads a single A-mode acquisition from the dongle.
    /// </summary>
    /// <remarks>
    /// <b>Must be called under GIL.</b> The caller is responsible for acquiring the GIL
    /// before invoking this method (typically via <c>using (Py.GIL())</c>).
    /// </remarks>
    /// <param name="buffer">
    /// Pre-allocated buffer of length <c>NumSamples + 2</c>. The last two slots are
    /// filled with config index and acquisition counter respectively.
    /// </param>
    /// <returns>
    /// A <see cref="WulpusRawAcquisition"/> with the filled buffer, or <see langword="null"/>
    /// if no data was available.
    /// </returns>
    public WulpusRawAcquisition? ReceiveOne(short[] buffer)
    {
        Debug.Assert(buffer.Length >= NumSamples + 2,
            $"Buffer too small: {buffer.Length} < {NumSamples + 2}");

        if (!IsStreaming) return null;

        dynamic data = _dongle!.receive_data();
        if (data is null) return null;

        int counter = (int)data[1];
        if (counter - _lastCounter > 1)
            Debug.WriteLine($"[{_ownerName}] Acquisition loss detected (gap={counter - _lastCounter - 1}).");
        _lastCounter = counter;

        PyObject npArray = data[0];
        PyObject bytesObj = npArray.InvokeMethod("tobytes");
        byte[] raw = bytesObj.As<byte[]>();
        Buffer.BlockCopy(raw, 0, buffer, 0, raw.Length);

        int configIx = (int)(short)data[2];
        buffer[buffer.Length - 2] = (short)configIx;
        buffer[buffer.Length - 1] = (short)counter;

        return new WulpusRawAcquisition(buffer, configIx, counter);
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Stops streaming, closes the port, and releases resources.
    /// </summary>
    public void Dispose()
    {
        StopStreaming();
        if (IsConnected)
        {
            try { ClosePort(); }
            catch { /* best effort */ }
        }
    }

    #endregion
}