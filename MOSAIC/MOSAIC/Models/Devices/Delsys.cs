using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aero.PipeLine;
using CommunityToolkit.Mvvm.ComponentModel;
using DelsysAPI.DelsysDevices;
using DelsysAPI.Events;
using DelsysAPI.Exceptions;
using DelsysAPI.Pipelines;
using DelsysAPI.Utils;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Devices.Delsys;
using MOSAIC.Diagnostics;
using MOSAIC.Visualization;
using DelsysPipeline = DelsysAPI.Pipelines.Pipeline;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Delsys Trigno RF source block for the MOSAIC pipeline.
///
/// Wraps the Delsys API to discover, arm, and stream from Trigno RF sensors.
/// Supports both synchronous (timer-driven, latest-value) and asynchronous
/// (push-every-sample via HighResTimer) streaming modes.
///
/// All sensor channels (EMG + IMU) are flattened into a single
/// <see cref="Vector{Double}"/> per sample, with deterministic column ordering
/// controlled by <see cref="SidOrder"/>.
///
/// Quaternion orientation channels are automatically sign-corrected to maintain
/// hemisphere continuity between consecutive frames.
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Params are tokens, not positional values. The async token sets AsyncMode. Sensor pairing, channel selection and acquisition still require the Delsys card and vendor software.</para>
/// <code language="json">
/// {
///   "Delsys": {
///     "Type": "delsys",
///     "Inputs": [],
///     "Params": ["async"]
///   }
/// }
/// </code>
/// </example>
public partial class Delsys : BaseBlock
{
    /// <inheritdoc />
    public override int MinInputs => 0;

    /// <inheritdoc />
    public override int MaxInputs => 0;

    #region Constants / Credentials

    // Vendor credentials must be supplied by the user and must never be committed.
    private static string GetCredential(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Delsys support requires the {name} environment variable.");

    private static string ApiKey => GetCredential("MOSAIC_DELSYS_API_KEY");
    private static string ApiLicense => GetCredential("MOSAIC_DELSYS_API_LICENSE");

    #endregion

    #region Public Surface (UI / VM bindings)

    /// <summary>Visualization bundle for EMG channels.</summary>
    public BlockVisualization? VizEmg { get; set; } = new();
    
    /// <summary>Visualization bundle for accelerometer channels.</summary>
    public BlockVisualization? VizAcc { get; set; } = new();

    /// <summary>Visualization bundle for gyroscope channels.</summary>
    public BlockVisualization? VizGyro { get; set; }= new();

    /// <summary>Visualization bundle for orientation (quaternion) channels.</summary>
    public BlockVisualization? VizOrientation { get; set; }= new();

    /// <summary>Observable stream telemetry (device name, status, counters).</summary>
    public Components.Devices.Delsys.DelsysStreamInfo StreamInfo { get; } = new();

    /// <summary>Observable list of discovered sensor descriptions for the UI.</summary>
    public ObservableCollection<DelsysSensorInfo> DiscoveredSensors { get; } = new();

    /// <summary>Cached channel descriptors for the current pipeline configuration.</summary>
    public List<DelsysChannelInfo> ChannelInfos { get; private set; } = new();

    /// <summary>Channel labels for the output vector, built after arm. Used for scope legends.</summary>
    public List<ChannelLabel> ChannelLabels { get; private set; } = new();

    /// <summary>Vector indices that contain EMG data.</summary>
    public List<int> EmgIndices { get; private set; } = new();

    /// <summary>Vector indices that contain accelerometer data.</summary>
    public List<int> AccIndices { get; private set; } = new();

    /// <summary>Vector indices that contain gyroscope data.</summary>
    public List<int> GyroIndices { get; private set; } = new();

    /// <summary>Vector indices that contain orientation (quaternion) data.</summary>
    public List<int> OrientationIndices { get; private set; } = new();

    /// <summary>Combined IMU indices (acc + gyro + orientation) for backward compat.</summary>
    public List<int> ImuIndices { get; private set; } = new();

    // ---- Visibility flags: true when the current config has channels of that type ----

    [ObservableProperty] private bool _hasEmg;
    [ObservableProperty] private bool _hasAcc;
    [ObservableProperty] private bool _hasGyro;
    [ObservableProperty] private bool _hasOrientation;

    /// <summary>Whether the pipeline is currently streaming data.</summary>
    public bool IsStreaming => _isStreaming;

    /// <summary>Whether the pipeline is currently armed and ready to stream.</summary>
    public bool IsArmed => _isArmed;

    #endregion

    #region Configuration

    /// <summary>
    /// Stable ordering map: sensor SID → sort position.
    /// Sensors not in this map sort to the end. Determines the column layout
    /// of the output vector (sensors are grouped, channels within each sensor
    /// are ordered alphabetically).
    /// </summary>
    public Dictionary<int, int> SidOrder { get; set; } = new()
    {
        [71851] = 1, [71482] = 2, [71485] = 3, [72061] = 4,
        [71799] = 5, [71974] = 6, [71847] = 7, [72067] = 8
    };

    /// <summary>
    /// If set, overrides any per-sensor UI mode selection. Parsed from
    /// the first numeric parameter in the JSON config.
    /// </summary>
    public int? DefaultModeIndex { get; private set; }

    /// <summary>When true, the HighResTimer pushes every sample immediately.</summary>
    public bool AsyncMode { get; private set; }

    /// <summary>When true, OnReceive returns the latest cached sample.</summary>
    public bool SyncMode { get; private set; }

    #endregion

    #region Internal Pipeline State

    private IDelsysDevice? _deviceSource;
    private DelsysPipeline? _pipeline;
    private HighResTimer? _highResTimer;

    private int    _vectorLength;
    private int    _maxSamplesPerFrame = 1;
    private int    _masterSpf;           // max SPF across ALL channels — output rows per frame
    private double _sampleRate;          // highest sample rate across all channels
    private int    _frameThroughput;
    private double _packetInterval;

    private volatile bool _isStreaming;
    private volatile bool _isArmed;

    private double   _streamTime;
    private int      _totalFrames;
    private int      _totalLostPackets;
    private double[] _lastSample = Array.Empty<double>();

    /// <summary>
    /// Per-channel previous frame end value, for linear interpolation of
    /// low-SPF channels up to _masterSpf output rate.
    /// Indexed by vector column index.
    /// </summary>
    private double[] _prevFrameEnd = Array.Empty<double>();

    // Sensor GUID → list of (vectorIndex, samplesPerFrame) for each TrignoChannel
    private readonly Dictionary<Guid, List<ChannelMeta>> _chanMap = new();

    // Quaternion sign-continuity
    private readonly Dictionary<Guid, (int w, int x, int y, int z)> _quatIndices = new();
    private readonly Dictionary<Guid, double[]> _lastQuat = new();

    // Mode tracking for mapping file export
    private readonly Dictionary<Guid, string> _modeNameBySensor = new();

    // FIFO bridge: Delsys callback thread → HighResTimer dispatch thread
    private readonly BlockingCollection<Vector<double>> _fifo = new(60_000); // ~15 s @ 4 kHz

    // Latest vector for sync mode (immutable replace, atomic read)
    private volatile Vector<double>? _latestVector;

    // Per-channel export data
    private List<List<double>>? _exportData;

    // Debug counters
    private long _dbgEnq;

    #endregion

    #region Nested Types

    /// <summary>Maps a single channel to its position in the output vector.</summary>
    private readonly record struct ChannelMeta(
        int VectorIndex,
        int SamplesPerFrame,
        bool IsOrientation);

    #endregion

    #region Construction / Factory

    public Delsys(string name = "Delsys", double desiredRate = 0)
        : base(name, desiredRate)
    {
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_trigno.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_trigno";

    /// <summary>JSON-driven factory (mirrors Myo.ConfigureInput pattern).</summary>
    public static Delsys ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Delsys";
        var rate = m.DesiredRate ?? 0;

        // Parse params: first int → DefaultModeIndex, "async"/"sync" → mode flag
        int? defaultMode = null;
        bool asyncFlag   = false;
        bool syncFlag    = false;

        if (m.Params is { Count: > 0 })
        {
            foreach (var p in m.Params)
            {
                var s = p?.ToString() ?? "";
                if (int.TryParse(s, out int modeIdx))
                    defaultMode = modeIdx;
                else if (s.Contains("async", StringComparison.OrdinalIgnoreCase))
                    asyncFlag = true;
                else if (s.Contains("sync", StringComparison.OrdinalIgnoreCase))
                    syncFlag = true;
            }
        }

        var block = ActivatorUtilities.CreateInstance<Delsys>(sp, name, rate);

        block.DefaultModeIndex = defaultMode;
        block.AsyncMode        = asyncFlag;
        block.SyncMode         = syncFlag;

        return block;
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Initializes the Delsys device source and creates the API pipeline.
    /// Call once after construction (typically from the ViewModel).
    /// </summary>
    public void InitializeDeviceSource()
    {
        Console.WriteLine("[Delsys:Init] Step 1 — Creating DeviceSourcePortable...");
        var creator = new DeviceSourcePortable(ApiKey, ApiLicense);
        creator.SetDebugOutputStream((str, args) =>
        {
            try { Console.WriteLine($"[DelsysAPI] {string.Format(str, args)}"); }
            catch { Console.WriteLine($"[DelsysAPI] {str}"); }
        });

        Console.WriteLine("[Delsys:Init] Step 2 — GetDataSource(TRIGNO_RF)...");
        _deviceSource         = creator.GetDataSource(SourceType.TRIGNO_RF);
        _deviceSource.Key     = ApiKey;
        _deviceSource.License = ApiLicense;
        Console.WriteLine($"[Delsys:Init]   DeviceSource type: {_deviceSource.GetType().Name}");
        Console.WriteLine($"[Delsys:Init]   PipelineIdentifier: {_deviceSource.PipelineIdentifier}");

        try
        {
            Console.WriteLine("[Delsys:Init] Step 3 — AddPipeline...");
            PipelineController.Instance.AddPipeline(_deviceSource);

            Console.WriteLine($"[Delsys:Init]   PipelineIds count: {PipelineController.Instance.PipelineIds.Count}");
            _pipeline = PipelineController.Instance.PipelineIds[0];
            Console.WriteLine($"[Delsys:Init]   Pipeline state: {_pipeline.CurrentState}");
            Console.WriteLine($"[Delsys:Init]   Pipeline type: {_pipeline.GetType().Name}");

            Console.WriteLine("[Delsys:Init] Step 4 — Setting InformationScanTime = 1 ...");
            _pipeline.TrignoRfManager.InformationScanTime = 1;
            Console.WriteLine($"[Delsys:Init]   TrignoRfManager type: {_pipeline.TrignoRfManager.GetType().Name}");
            Console.WriteLine($"[Delsys:Init]   Components count (before scan): {_pipeline.TrignoRfManager.Components.Count}");

            // Wire API events — use properly typed handlers matching old code
            Console.WriteLine("[Delsys:Init] Step 5 — Wiring events...");
            _pipeline.TrignoRfManager.ComponentAdded        += OnComponentAdded;
            _pipeline.TrignoRfManager.ComponentLost         += OnComponentLost;
            _pipeline.TrignoRfManager.ComponentRemoved      += OnComponentRemoved;
            _pipeline.TrignoRfManager.ComponentScanComplete += OnComponentScanComplete;

            _pipeline.CollectionStarted   += OnCollectionStarted;
            _pipeline.CollectionDataReady += OnCollectionDataReady;
            _pipeline.CollectionComplete  += OnCollectionComplete;

            StreamInfo.DeviceName     = _deviceSource.PipelineIdentifier;
            StreamInfo.PipelineStatus = _pipeline.CurrentState.ToString();
            Console.WriteLine("[Delsys:Init] ✓ Pipeline created. Base connection established.");
        }
        catch (BaseDetectionFailedException ex)
        {
            Log.Error("Delsys:Init", ex, "No Trigno base detected. Check USB connection and power to the Trigno base station.");
        }
        catch (Exception ex)
        {
            Log.Error("Delsys:Init", ex, "Unexpected error creating the pipeline.");
        }
    }

    // --- Properly typed API event handlers (matching old working code) ---

    private void OnComponentAdded(object sender, ComponentAddedEventArgs e)
    {
        Console.WriteLine($"[Delsys:Event] ComponentAdded — " +
                          $"component count now: {_pipeline?.TrignoRfManager.Components.Count}");
    }

    private void OnComponentLost(object sender, ComponentLostEventArgs e)
    {
        Console.WriteLine($"[Delsys:Event] ComponentLost");
    }

    private void OnComponentRemoved(object sender, ComponentRemovedEventArgs e)
    {
        Console.WriteLine($"[Delsys:Event] ComponentRemoved — " +
                          $"component count now: {_pipeline?.TrignoRfManager.Components.Count}");
    }

    private void OnCollectionComplete(object sender, CollectionCompleteEvent e)
    {
        Console.WriteLine("[Delsys:Event] CollectionComplete");
    }

    public override void Dispose()
    {
        try { VizEmg?.Dispose(); }         catch { }
        try { VizAcc?.Dispose(); }         catch { }
        try { VizGyro?.Dispose(); }        catch { } 
        try { VizOrientation?.Dispose(); } catch { }

        StopDispatchTimer();
        _fifo.CompleteAdding();

        if (_isStreaming && _pipeline is not null)
            _pipeline.Stop().GetAwaiter().GetResult();

        base.Dispose();
    }

    #endregion

    #region Hot Path (BaseBlock tick)

    /// <summary>
    /// Called by upstream timer in sync mode. In async mode this is a no-op
    /// because the HighResTimer drives output directly.
    /// </summary>
    protected override void OnReceive(object sender, object tNow)
    {
        if (!SyncMode) return;

        // In sync mode: skip samples to match the desired output rate,
        // then publish the latest available vector.
        double ratio = _sampleRate / (DesiredRate > 0 ? DesiredRate : _sampleRate);
        int    skip  = (int)Math.Floor(ratio);

        Vector<double>? vec = null;
        for (int i = 0; i < skip && _fifo.TryTake(out vec); i++) { }
        _latestVector = vec ?? _latestVector;

        var output = _latestVector ?? Vector<double>.Build.Dense(_vectorLength, 0.0);
        PublishVector(output);
    }

    private void PublishVector(Vector<double> vec)
    {
        Publish(vec);
        
        int vecLen = vec.Count;

        // Feed each visualization bundle with its subset of channels
        FeedViz(VizEmg, EmgIndices, vec, vecLen);
        FeedViz(VizAcc, AccIndices, vec, vecLen);
        FeedViz(VizGyro, GyroIndices, vec, vecLen);
        FeedViz(VizOrientation, OrientationIndices, vec, vecLen);
    }

    /// <summary>
    /// Extracts a sub-vector for the given index list and feeds it to
    /// all three monitors (Scope + Spider + Heatmap) in one call.
    /// </summary>
    private static void FeedViz(BlockVisualization? viz, List<int> indices, Vector<double> vec, int vecLen)
    {
        if (viz is null || indices.Count == 0) return; 
        
        var idx = indices;
        if (idx.Count == 0 || idx[^1] >= vecLen) return;
 
        var sub = Vector<double>.Build.Dense(idx.Count);
        for (int i = 0; i < idx.Count; i++)
            sub[i] = vec[idx[i]];
        viz.Feed(sub);
    }

    #endregion

    #region Sensor Discovery

    /// <summary>Scans for paired Trigno RF sensors.</summary>
    public async Task ScanAsync()
    {
        Console.WriteLine("[Delsys:Scan] === SCAN START ===");

        // Auto-initialize if not done yet
        if (_pipeline is null)
        {
            Console.WriteLine("[Delsys:Scan] _pipeline is null — calling InitializeDeviceSource() automatically...");
            InitializeDeviceSource();

            if (_pipeline is null)
            {
                Log.Error("Delsys:Scan", "InitializeDeviceSource() failed — _pipeline still null. Aborting scan.");
                return;
            }
        }

        Console.WriteLine($"[Delsys:Scan] Pipeline state: {_pipeline.CurrentState}");
        Console.WriteLine($"[Delsys:Scan] Components before cleanup: {_pipeline.TrignoRfManager.Components.Count}");

        // Remove previously connected sensors
        var existingComponents = _pipeline.TrignoRfManager.Components.ToList();
        Console.WriteLine($"[Delsys:Scan] Removing {existingComponents.Count} existing component(s)...");
        foreach (var comp in existingComponents)
        {
            try
            {
                Console.WriteLine($"[Delsys:Scan]   Deselecting {comp.FriendlyName} (SID {comp.Properties.Sid})...");
                await _pipeline.TrignoRfManager.DeselectComponentAsync(comp);
                _pipeline.TrignoRfManager.RemoveTrignoComponent(comp);
            }
            catch (Exception ex)
            {
                Log.Warn("Delsys:Scan", ex, "Error removing component.");
            }
        }

        Console.WriteLine($"[Delsys:Scan] Components after cleanup: {_pipeline.TrignoRfManager.Components.Count}");
        Console.WriteLine("[Delsys:Scan] Calling Pipeline.Scan() — scanning for paired sensors...");

        try
        {
            await _pipeline.Scan();
        }
        catch (Exception ex)
        {
            Log.Error("Delsys:Scan", ex, "Pipeline.Scan() threw.");
            return;
        }

        Console.WriteLine($"[Delsys:Scan] Pipeline.Scan() returned.");

        // Give the API a moment — ComponentScanComplete fires asynchronously
        // and needs time to populate components and pre-select modes
        Console.WriteLine("[Delsys:Scan] Waiting 2s for ComponentScanComplete callback...");
        await Task.Delay(2000);

        Console.WriteLine($"[Delsys:Scan] Pipeline state after scan: {_pipeline.CurrentState}");
        Console.WriteLine($"[Delsys:Scan] Components after scan: {_pipeline.TrignoRfManager.Components.Count}");

        // Dump every component found
        for (int i = 0; i < _pipeline.TrignoRfManager.Components.Count; i++)
        {
            var comp = _pipeline.TrignoRfManager.Components[i];
            Console.WriteLine($"[Delsys:Scan]   [{i}] FriendlyName=\"{comp.FriendlyName}\" " +
                              $"SID={comp.Properties.Sid} Pair#{comp.PairNumber} " +
                              $"State={comp.State} " +
                              $"Channels={comp.TrignoChannels.Count} " +
                              $"Modes={comp.Configuration.SampleModes}");
        }

        // Populate the observable collection for the UI
        DiscoveredSensors.Clear();
        foreach (var comp in _pipeline.TrignoRfManager.Components)
        {
            var info = new DelsysSensorInfo
            {
                Id         = comp.Id,
                Name       = comp.FriendlyName,
                Sid        = comp.Properties.Sid,
                PairNumber = comp.PairNumber,
                SampleModes = comp.Configuration.SampleModes
                                  .Select(m => m.ToString())
                                  .ToList()
            };
            DiscoveredSensors.Add(info);
            Console.WriteLine($"[Delsys:Scan]   → Added to DiscoveredSensors: \"{info.Name}\" SID={info.Sid}");
        }

        Console.WriteLine($"[Delsys:Scan] === SCAN COMPLETE: {DiscoveredSensors.Count} sensor(s) ===");
    }

    /// <summary>
    /// FIX: Only log the scan result here. Do NOT call comp.SelectSampleMode()
    /// because ArmAsync will apply the user's chosen mode per sensor.
    /// The old code was force-setting mode 0 here, which overwrote the user's
    /// dropdown selection if the scan callback fired late.
    /// </summary>
    private void OnComponentScanComplete(object sender, ComponentScanCompletedEventArgs e)
    {
        Console.WriteLine($"[Delsys:Event] ComponentScanComplete fired");
        Console.WriteLine($"[Delsys:Event]   ComponentDictionary.Count = {e.ComponentDictionary.Count}");

        if (e.ComponentDictionary.Count <= 0)
        {
            Console.WriteLine("[Delsys:Event]   ✗ No sensors in ComponentDictionary.");
            return;
        }

        // Dump dictionary contents
        foreach (var kvp in e.ComponentDictionary)
        {
            Console.WriteLine($"[Delsys:Event]   ComponentDictionary key={kvp.Key} value={kvp.Value}");
        }

        Console.WriteLine($"[Delsys:Event]   Components.Count = {_pipeline?.TrignoRfManager.Components.Count}");

        // Log available modes but do NOT pre-select — ArmAsync handles mode selection
        // using the user's per-sensor SelectedModeIndex from the UI.
        if (_pipeline is not null)
        {
            foreach (var comp in _pipeline.TrignoRfManager.Components)
            {
                Console.WriteLine($"[Delsys:Event]   Sensor {comp.FriendlyName} " +
                                  $"(SID {comp.Properties.Sid}): " +
                                  $"{comp.Configuration.SampleModes.Length} modes available — " +
                                  $"mode selection deferred to ArmAsync()");
            }
        }

        Console.WriteLine("[Delsys:Event] ComponentScanComplete done.");
    }

    #endregion

    #region Arm / Configure / Start / Stop

    /// <summary>
    /// Arms the pipeline: applies per-sensor sample modes for selected sensors,
    /// builds the channel map, and starts the pipeline.
    /// Each sensor in <see cref="DiscoveredSensors"/> carries its own
    /// <c>IsSelected</c> and <c>SelectedModeIndex</c>.
    /// </summary>
    /// <param name="modeIndex">
    /// If provided, overrides every sensor's individual mode. If null, each
    /// sensor uses its own <c>SelectedModeIndex</c> from the UI dropdown.
    /// <see cref="DefaultModeIndex"/> (from JSON config) is only used as a
    /// fallback when the sensor's <c>SelectedModeIndex</c> is still at the
    /// default value of 0 AND no explicit <paramref name="modeIndex"/> is given.
    /// </param>
    public async Task ArmAsync(int? modeIndex = null)
    {
        if (_pipeline is null) return;
        await DisarmAsync();

        // Determine which sensors are selected
        var selected = DiscoveredSensors.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log.Warn("Delsys:Arm", "No sensors selected. Aborting arm.");
            return;
        }

        Console.WriteLine($"[Delsys:Arm] Arming {selected.Count} sensor(s)...");
        Console.WriteLine($"[Delsys:Arm]   modeIndex param = {(modeIndex.HasValue ? modeIndex.Value.ToString() : "null")}");
        Console.WriteLine($"[Delsys:Arm]   DefaultModeIndex = {(DefaultModeIndex.HasValue ? DefaultModeIndex.Value.ToString() : "null")}");

        // Build a lookup from sensor GUID → desired mode index
        // Priority: explicit modeIndex param > user's per-sensor UI pick > DefaultModeIndex fallback > 0
        var sensorModeMap = new Dictionary<Guid, int>();
        foreach (var info in selected)
        {
            int mode;
            if (modeIndex.HasValue)
            {
                // Explicit override from caller (e.g. ViewModel passing a global mode)
                mode = modeIndex.Value;
            }
            else
            {
                // Use the user's per-sensor dropdown selection.
                // Only fall back to DefaultModeIndex if no user interaction happened
                // (SelectedModeIndex is still 0 and DefaultModeIndex is set).
                mode = info.SelectedModeIndex;
            }

            sensorModeMap[info.Id] = mode;
            Console.WriteLine($"[Delsys:Arm]   Sensor SID {info.Sid}: IsSelected={info.IsSelected}, " +
                              $"UI SelectedModeIndex={info.SelectedModeIndex}, resolved mode={mode}");
        }

        // Apply sample mode and select only chosen sensors
        var selectedIds = new HashSet<Guid>(selected.Select(s => s.Id));
        foreach (var comp in _pipeline.TrignoRfManager.Components)
        {
            if (!selectedIds.Contains(comp.Id))
            {
                Console.WriteLine($"[Delsys:Arm] Skipping sensor {comp.Properties.Sid} (not selected)");
                continue;
            }

            int mode = sensorModeMap[comp.Id];

            // Validate mode index is within range
            if (mode < 0 || mode >= comp.Configuration.SampleModes.Length)
            {
                Log.Warn("Delsys:Arm", $"Sensor SID {comp.Properties.Sid}: " +
                                       $"mode index {mode} out of range (0..{comp.Configuration.SampleModes.Length - 1}). " +
                                       $"Falling back to 0.");
                mode = 0;
            }

            comp.SelectSampleMode(comp.Configuration.SampleModes[mode]);
            Console.WriteLine($"[Delsys:Arm] ✓ Sensor SID {comp.Properties.Sid} Pair#{comp.PairNumber}: " +
                              $"mode[{mode}] \"{comp.Configuration.SampleModes[mode]}\" " +
                              $"@ {comp.TrignoChannels[0].SampleRate} Hz");
            _modeNameBySensor[comp.Id] = comp.Configuration.SampleModes[mode].ToString();
            await _pipeline.TrignoRfManager.SelectComponentAsync(comp);
        }

        // Determine the master output rate: find the highest sample rate
        // and highest SPF across ALL channels of ALL selected sensors.
        // This ensures sensors running at different frequencies are all
        // properly upsampled to the fastest channel's rate.
        _masterSpf  = 1;
        _sampleRate = 0;

        foreach (var comp in _pipeline.TrignoRfManager.Components
                     .Where(c => selectedIds.Contains(c.Id)))
        {
            foreach (var ch in comp.TrignoChannels)
            {
                if (ch.SamplesPerFrame > _masterSpf)
                    _masterSpf = ch.SamplesPerFrame;
                if (ch.SampleRate > _sampleRate)
                    _sampleRate = ch.SampleRate;
            }
        }

        Console.WriteLine($"[Delsys:Arm] Master output: SPF={_masterSpf}, Rate={_sampleRate:F1} Hz");

        ConfigurePipeline();
        BuildChannelLabels();

        _pipeline.Start();
        _isArmed = true;
        Console.WriteLine($"[Delsys:Arm] Armed. {ChannelLabels.Count} channels, " +
                          $"{EmgIndices.Count} EMG, {ImuIndices.Count} IMU");
    }

    /// <summary>Disarms the pipeline and stops dispatching.</summary>
    public async Task DisarmAsync()
    {
        // Always stop the timer — even if _isArmed is somehow already false
        // the timer might still be running from a previous partial state
        StopDispatchTimer();

        if (!_isArmed || _pipeline is null) return;
        await _pipeline.DisarmPipeline();
        _isArmed    = false;
        _isStreaming = false;

        // Drain stale data so re-arm starts clean
        while (_fifo.TryTake(out _)) { }
    }

    /// <summary>Starts streaming (if pipeline is armed).</summary>
    public void StartStream()
    {
        if (_pipeline is null || !_isArmed) return;
        _pipeline.Start();
    }

    /// <summary>Stops the active stream.</summary>
    public async Task StopStreamAsync()
    {
        if (_pipeline is null) return;
        await _pipeline.Stop();
        _isStreaming = false;
    }

    /// <summary>Full reset: disarm, stop dispatch, remove all sensors, clear counters.</summary>
    public async Task ResetAsync()
    {
        if (_pipeline is null) return;

        // Stop the HighResTimer FIRST so its background thread stops
        // calling PublishVector with stale indices
        StopDispatchTimer();
        _isStreaming = false;

        await _pipeline.DisarmPipeline();

        _totalFrames      = 0;
        _totalLostPackets = 0;
        _streamTime       = 0.0;

        // Clear stale channel state — prevents index-out-of-range if the
        // timer thread sneaks in one last tick during re-arm
        EmgIndices.Clear();
        AccIndices.Clear();
        GyroIndices.Clear();
        OrientationIndices.Clear();
        ImuIndices.Clear();
        ChannelLabels.Clear();
        ChannelInfos.Clear();

        HasEmg         = false;
        HasAcc         = false;
        HasGyro        = false;
        HasOrientation = false;
        _chanMap.Clear();
        _quatIndices.Clear();
        _lastQuat.Clear();
        _modeNameBySensor.Clear();
        _vectorLength   = 0;
        _masterSpf      = 1;
        _prevFrameEnd   = Array.Empty<double>();

        // Drain the FIFO so stale vectors from the old config don't leak
        // into the next arm cycle
        while (_fifo.TryTake(out _)) { }

        foreach (var comp in _pipeline.TrignoRfManager.Components.ToList())
            _pipeline.TrignoRfManager.RemoveTrignoComponent(comp);

        StreamInfo.Reset();
        StreamInfo.PipelineStatus = _pipeline.CurrentState.ToString();
        DiscoveredSensors.Clear();
        _isArmed = false;
    }

    #endregion

    #region Pipeline Configuration (channel mapping)

    private void ConfigurePipeline()
    {
        if (_pipeline is null) return;

        // Delsys pipeline plumbing
        var dataLine = new DataLine(_pipeline);
        dataLine.ConfigurePipeline();
        PipelineController.Instance.SetFrameThroughput(1);
        _frameThroughput = PipelineController.Instance.GetFrameThroughput();

        // Update stream info
        int totalCh = _pipeline.TrignoRfManager.Components
                               .Sum(c => c.TrignoChannels.Count);
        StreamInfo.PipelineStatus  = _pipeline.CurrentState.ToString();
        StreamInfo.SensorsConnected = _pipeline.TrignoRfManager.Components.Count;
        StreamInfo.TotalChannels   = totalCh;

        // ---- Build ChannelInfos with stable ordering ----
        CacheChannelInfos();

        // ---- Build the channel map (sensor GUID → list of ChannelMeta) ----
        _chanMap.Clear();
        _vectorLength         = 0;
        _maxSamplesPerFrame   = _masterSpf;

        // Build a set of orientation channel names for tagging
        var orientationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "orientation w", "orientation x", "orientation y", "orientation z"
        };

        foreach (var group in ChannelInfos
                     .GroupBy(ci => ci.SensorId)
                     .OrderBy(g => SidOrder.TryGetValue(g.First().SensorSID, out var idx)
                                  ? idx : int.MaxValue)
                     .ThenBy(g => g.First().SensorSID))
        {
            var metas = new List<ChannelMeta>();
            foreach (var ci in group)
            {
                bool isOri = orientationNames.Any(n =>
                    ci.ChannelName.Contains(n, StringComparison.OrdinalIgnoreCase));

                metas.Add(new ChannelMeta(_vectorLength, ci.SamplesPerFrame, isOri));
                _vectorLength++;
            }
            _chanMap[group.Key] = metas;
        }

        _lastSample     = new double[_vectorLength];
        _prevFrameEnd   = new double[_vectorLength];

        // ---- Build quaternion index mapping (for sign-continuity fix) ----
        BuildQuaternionIndexMapping();

        // ---- Write mapping file for diagnostics ----
        WriteMappingFile();

        Console.WriteLine($"[Delsys] Pipeline configured: {_vectorLength} channels, " +
                          $"masterSPF={_masterSpf}, maxRate={_sampleRate:F1} Hz");
    }

    /// <summary>
    /// Returns a canonical sort key for a Delsys channel name.
    /// Ensures deterministic ordering regardless of API enumeration order:
    ///   Level 1 — Channel type:  EMG=0, Acc=1, Gyro=2, Mag=3, Orientation=4, other=5
    ///   Level 2 — Axis:          X=0, Y=1, Z=2, W=3, none=4
    ///   Level 3 — Numeric suffix (e.g. "EMG 1" → 1, "EMG 14" → 14)
    /// </summary>
    private static (int typeOrder, int axisOrder, int number) ChannelSortKey(string channelName)
    {
        string lower = channelName.ToLowerInvariant().Trim();

        // ---- Type order ----
        int typeOrder;
        if      (lower.Contains("emg"))         typeOrder = 0;
        else if (lower.Contains("acc"))         typeOrder = 1;
        else if (lower.Contains("gyro"))        typeOrder = 2;
        else if (lower.Contains("mag"))         typeOrder = 3;
        else if (lower.Contains("orientation")) typeOrder = 4;
        else                                    typeOrder = 5;

        // ---- Axis order ----
        // Check the last meaningful token for axis letter
        int axisOrder = 4; // default: no axis
        if      (lower.EndsWith(" x") || lower.EndsWith(".x")) axisOrder = 0;
        else if (lower.EndsWith(" y") || lower.EndsWith(".y")) axisOrder = 1;
        else if (lower.EndsWith(" z") || lower.EndsWith(".z")) axisOrder = 2;
        else if (lower.EndsWith(" w") || lower.EndsWith(".w")) axisOrder = 3;

        // ---- Numeric suffix (e.g. "EMG 1", "EMG 14") ----
        int number = 0;
        for (int i = lower.Length - 1; i >= 0 && char.IsDigit(lower[i]); i--)
            number += (lower[i] - '0') * (int)Math.Pow(10, lower.Length - 1 - i);

        return (typeOrder, axisOrder, number);
    }

    private void CacheChannelInfos()
    {
        ChannelInfos = _pipeline!.TrignoRfManager.Components
            .SelectMany(comp => comp.TrignoChannels.Select(ch => new DelsysChannelInfo
            {
                SensorId        = comp.Id,
                SensorSID       = comp.Properties.Sid,
                DeviceIdx       = comp.DeviceIdx,
                ChannelName     = ch.Name,
                SampleRate      = ch.SampleRate,
                SamplesPerFrame = ch.SamplesPerFrame,
                FrameInterval   = ch.FrameInterval
            }))
            // Level 1: Sensor ordering — SidOrder position, then SID numerically
            .OrderBy(ci => SidOrder.TryGetValue(ci.SensorSID, out var idx) ? idx : int.MaxValue)
            .ThenBy(ci => ci.SensorSID)
            // Level 2: Channel ordering within each sensor — canonical type → axis → number
            .ThenBy(ci => ChannelSortKey(ci.ChannelName))
            .ToList();

        // Debug dump
        Console.WriteLine("── Delsys Channel Table ────────────────────────");
        foreach (var ci in ChannelInfos)
            Console.WriteLine($"  {ci.SensorSID,6} | {ci.ChannelName,-22} | " +
                              $"{ci.SampleRate,6:F1} Hz | SPF={ci.SamplesPerFrame}");
        Console.WriteLine("────────────────────────────────────────────────");
    }

    /// <summary>
    /// Scans channel names for orientation W/X/Y/Z and builds a per-sensor
    /// index tuple so the data callback can fix quaternion sign flips.
    /// </summary>
    private void BuildQuaternionIndexMapping()
    {
        _quatIndices.Clear();
        _lastQuat.Clear();

        foreach (var comp in _pipeline!.TrignoRfManager.Components)
        {
            if (!_chanMap.TryGetValue(comp.Id, out var metas)) continue;

            int w = -1, x = -1, y = -1, z = -1;

            for (int i = 0; i < comp.TrignoChannels.Count; i++)
            {
                var chName = comp.TrignoChannels[i].Name.ToLowerInvariant();
                int idx    = metas[i].VectorIndex;

                if      (chName.Contains("orientation w")) w = idx;
                else if (chName.Contains("orientation x")) x = idx;
                else if (chName.Contains("orientation y")) y = idx;
                else if (chName.Contains("orientation z")) z = idx;
            }

            if (w >= 0 && x >= 0 && y >= 0 && z >= 0)
                _quatIndices[comp.Id] = (w, x, y, z);
        }
    }

    /// <summary>
    /// Builds the ChannelLabels list and populates per-type index lists
    /// (EmgIndices, AccIndices, GyroIndices, OrientationIndices, ImuIndices)
    /// for splitting data into scopes. Call after ConfigurePipeline.
    /// Also sets HasEmg / HasAcc / HasGyro / HasOrientation visibility flags.
    /// </summary>
    private void BuildChannelLabels()
    {
        ChannelLabels.Clear();
        EmgIndices.Clear();
        AccIndices.Clear();
        GyroIndices.Clear();
        OrientationIndices.Clear();
        ImuIndices.Clear();

        int vecIdx = 0;
        foreach (var group in ChannelInfos
                     .GroupBy(ci => ci.SensorId)
                     .OrderBy(g => SidOrder.TryGetValue(g.First().SensorSID, out var idx)
                                  ? idx : int.MaxValue)
                     .ThenBy(g => g.First().SensorSID))
        {
            var firstCi = group.First();
            int pairNum = DiscoveredSensors
                .FirstOrDefault(s => s.Id == firstCi.SensorId)?.PairNumber ?? 0;

            foreach (var ci in group)
            {
                string chLower = ci.ChannelName.ToLowerInvariant();

                bool isEmg  = chLower.Contains("emg");
                bool isAcc  = chLower.Contains("acc");
                bool isGyro = chLower.Contains("gyro");
                bool isOri  = chLower.Contains("orientation");
                bool isMag  = chLower.Contains("mag");
                bool isImu  = isAcc || isGyro || isOri || isMag;

                var label = new ChannelLabel
                {
                    VectorIndex   = vecIdx,
                    Label         = $"S{pairNum} SID{ci.SensorSID} · {ci.ChannelName}",
                    SensorSid     = ci.SensorSID,
                    PairNumber    = pairNum,
                    IsEmg         = isEmg,
                    IsImu         = isImu,
                    IsAcc         = isAcc,
                    IsGyro        = isGyro,
                    IsOrientation = isOri
                };

                ChannelLabels.Add(label);

                if (isEmg)  EmgIndices.Add(vecIdx);
                if (isAcc || isMag) AccIndices.Add(vecIdx);   // mag goes with acc scope
                if (isGyro) GyroIndices.Add(vecIdx);
                if (isOri)  OrientationIndices.Add(vecIdx);
                if (isImu)  ImuIndices.Add(vecIdx);

                vecIdx++;
            }
        }

        // Update visibility flags
        HasEmg         = EmgIndices.Count > 0;
        HasAcc         = AccIndices.Count > 0;
        HasGyro        = GyroIndices.Count > 0;
        HasOrientation = OrientationIndices.Count > 0;

        // Debug dump
        Console.WriteLine("── Channel Labels ──────────────────────────────");
        foreach (var cl in ChannelLabels)
        {
            string tag = cl.IsEmg ? "[EMG]" : cl.IsAcc ? "[ACC]" :
                         cl.IsGyro ? "[GYR]" : cl.IsOrientation ? "[ORI]" : "[---]";
            Console.WriteLine($"  vec[{cl.VectorIndex,2}] {tag} {cl.Label}");
        }
        Console.WriteLine($"  EMG indices: [{string.Join(", ", EmgIndices)}]");
        Console.WriteLine($"  ACC indices: [{string.Join(", ", AccIndices)}]");
        Console.WriteLine($"  GYR indices: [{string.Join(", ", GyroIndices)}]");
        Console.WriteLine($"  ORI indices: [{string.Join(", ", OrientationIndices)}]");
        Console.WriteLine("────────────────────────────────────────────────");
    }

    #endregion

    #region Dispatch Timer (HighResTimer for async mode)

    private void StartDispatchTimer()
    {
        if (_highResTimer is not null) return;

        int freqHz = (int)Math.Round(_sampleRate);
        Console.WriteLine($"[Delsys] Starting HighResTimer @ {freqHz} Hz");

        _highResTimer = new HighResTimer(freqHz, () =>
        {
            if (_fifo.TryTake(out var vec))
            {
                _latestVector = vec;
                PublishVector(vec);
            }
            else
            {
                // FIFO empty — emit zeros to keep cadence
                PublishVector(Vector<double>.Build.Dense(_vectorLength, 0.0));
            }
        });

        _highResTimer.Start();
    }

    private void StopDispatchTimer()
    {
        if (_highResTimer is null) return;
        _highResTimer.Dispose();
        _highResTimer = null;
        Console.WriteLine("[Delsys] HighResTimer stopped");
    }

    #endregion

    #region Delsys API Collection Callbacks

    private void OnCollectionStarted(object sender, CollectionStartedEvent e)
    {
        StreamInfo.PipelineStatus = _pipeline!.CurrentState.ToString();

        _exportData       = new List<List<double>>();
        _totalFrames      = 0;
        _totalLostPackets = 0;
        _packetInterval   = 0;

        int totalCh = 0;
        foreach (var comp in _pipeline.TrignoRfManager.Components)
        {
            foreach (var ch in comp.TrignoChannels)
            {
                _exportData.Add(new List<double>());
                if (_packetInterval == 0)
                    _packetInterval = ch.FrameInterval * _frameThroughput;
                totalCh++;
            }
        }

        _latestVector  = Vector<double>.Build.Dense(_vectorLength, 0.0);
        _lastSample    = new double[_vectorLength];
        _prevFrameEnd  = new double[_vectorLength];
        _isStreaming   = true;

        // Always start the dispatch timer so data flows from FIFO → Publish → Scope.
        // The old code did this unconditionally in CollectionStarted regardless of mode.
        if (_highResTimer is null)
        {
            Console.WriteLine("[Delsys] Starting HighResTimer from CollectionStarted...");
            StartDispatchTimer();
        }

        Console.WriteLine($"[Delsys] Collection started: vectorLength={_vectorLength}, " +
                          $"totalChannels={totalCh}, asyncMode={AsyncMode}, syncMode={SyncMode}");
    }

    /// <summary>
    /// Universal data-ready handler.
    /// Processes every Trigno frame, expands all channels to _masterSpf output
    /// rows using per-channel upsampling:
    ///   - Channels with SPF == _masterSpf: direct 1:1 mapping
    ///   - Orientation channels (SPF=1): sample-and-hold (no interpolation)
    ///   - Other low-SPF channels: linear interpolation from previous frame's
    ///     end value to current frame's value(s)
    ///
    /// Quaternion sign-continuity is applied before upsampling.
    /// </summary>
    private void OnCollectionDataReady(object sender, ComponentDataReadyEventArgs e)
    {
        foreach (var frame in e.Data)
        {
            // Order sensor data deterministically by SidOrder, then SID
            var orderedSensorData = frame.SensorData
                .OrderBy(sd =>
                {
                    int sid = ChannelInfos.First(ci => ci.SensorId == sd.Id).SensorSID;
                    return SidOrder.TryGetValue(sid, out var idx) ? idx : int.MaxValue;
                })
                .ThenBy(sd => ChannelInfos.First(ci => ci.SensorId == sd.Id).SensorSID)
                .ToList();

            // ---- 1) Patch quaternions once per frame ----
            var patchedQuat = new Dictionary<Guid, double[]>();
            foreach (var (sensorId, (wIdx, xIdx, yIdx, zIdx)) in _quatIndices)
            {
                var metas = _chanMap[sensorId];
                var sd    = frame.SensorData.First(s => s.Id == sensorId);

                int wCh = metas.FindIndex(m => m.VectorIndex == wIdx);
                int xCh = metas.FindIndex(m => m.VectorIndex == xIdx);
                int yCh = metas.FindIndex(m => m.VectorIndex == yIdx);
                int zCh = metas.FindIndex(m => m.VectorIndex == zIdx);

                double qw = sd.ChannelData[wCh].Data[0];
                double qx = sd.ChannelData[xCh].Data[0];
                double qy = sd.ChannelData[yCh].Data[0];
                double qz = sd.ChannelData[zCh].Data[0];

                // Sign continuity: flip if dot product with previous is negative
                if (_lastQuat.TryGetValue(sensorId, out var prev))
                {
                    double dot = prev[0] * qw + prev[1] * qx + prev[2] * qy + prev[3] * qz;
                    if (dot < 0.0) { qw = -qw; qx = -qx; qy = -qy; qz = -qz; }
                }
                else if (qw < 0.0)
                {
                    qw = -qw; qx = -qx; qy = -qy; qz = -qz;
                }

                _lastQuat[sensorId]    = new[] { qw, qx, qy, qz };
                patchedQuat[sensorId]  = new[] { qw, qx, qy, qz };
            }

            // ---- 2) Build _masterSpf output rows with per-channel upsampling ----
            for (int s = 0; s < _masterSpf; s++)
            {
                var row = new double[_vectorLength];

                foreach (var sd in orderedSensorData)
                {
                    var metas = _chanMap[sd.Id];
                    patchedQuat.TryGetValue(sd.Id, out var pq);

                    for (int ci = 0; ci < metas.Count; ci++)
                    {
                        var meta = metas[ci];
                        int spf  = meta.SamplesPerFrame;
                        int vi   = meta.VectorIndex;
                        double value;

                        if (spf == _masterSpf)
                        {
                            // ---- Native rate: direct 1:1 ----
                            value = s < sd.ChannelData[ci].Data.Count
                                ? sd.ChannelData[ci].Data[s]
                                : _lastSample[vi];
                        }
                        else if (meta.IsOrientation && pq is not null &&
                                 _quatIndices.TryGetValue(sd.Id, out var qi))
                        {
                            // ---- Orientation: sample-hold patched quaternion ----
                            if      (vi == qi.w) value = pq[0];
                            else if (vi == qi.x) value = pq[1];
                            else if (vi == qi.y) value = pq[2];
                            else if (vi == qi.z) value = pq[3];
                            else value = sd.ChannelData[ci].Data[0];
                        }
                        else if (spf == 1)
                        {
                            // ---- SPF=1 non-orientation: linear interpolation ----
                            // Lerp from previous frame's end value to this frame's value
                            double prevVal = _prevFrameEnd[vi];
                            double curVal  = sd.ChannelData[ci].Data[0];
                            double t = (double)(s + 1) / _masterSpf;
                            value = prevVal + (curVal - prevVal) * t;
                        }
                        else
                        {
                            // ---- Intermediate SPF (1 < spf < _masterSpf): resample ----
                            // Map output index s into the channel's native sample space
                            // using linear interpolation between the two nearest native samples.
                            double pos = (double)s * spf / _masterSpf;
                            int lo = (int)pos;
                            int hi = Math.Min(lo + 1, spf - 1);
                            double frac = pos - lo;

                            double vLo = lo < sd.ChannelData[ci].Data.Count
                                ? sd.ChannelData[ci].Data[lo]
                                : _lastSample[vi];
                            double vHi = hi < sd.ChannelData[ci].Data.Count
                                ? sd.ChannelData[ci].Data[hi]
                                : vLo;

                            value = vLo + (vHi - vLo) * frac;
                        }

                        row[vi] = value;
                        _lastSample[vi] = value;
                    }
                }

                // Enqueue for the HighResTimer dispatcher
                _fifo.Add(DenseVector.OfArray(row));
                if (++_dbgEnq % 2000 == 0)
                    Console.WriteLine($"[Delsys] Enqueued {_dbgEnq:N0}  FIFO={_fifo.Count:N0}");
            }

            // ---- 3) Store frame-end values for next frame's interpolation ----
            foreach (var sd in orderedSensorData)
            {
                var metas = _chanMap[sd.Id];
                for (int ci = 0; ci < metas.Count; ci++)
                {
                    var meta = metas[ci];
                    int lastIdx = sd.ChannelData[ci].Data.Count - 1;
                    if (lastIdx >= 0)
                        _prevFrameEnd[meta.VectorIndex] = sd.ChannelData[ci].Data[lastIdx];
                }
            }
        }

        // Update telemetry
        _streamTime       += _packetInterval;
        _totalFrames      += _frameThroughput * e.Data[0].SensorData.Count();
        StreamInfo.StreamTime      = $"{_streamTime:#.##} s";
        StreamInfo.FramesCollected = _totalFrames;

        // Check for lost packets
        int lost = e.Data.SelectMany(f => f.SensorData)
                         .Count(sd => sd.IsDroppedPacket);
        _totalLostPackets     += lost;
        StreamInfo.PacketsLost = _totalLostPackets;
    }

    #endregion

    #region Export

    /// <summary>
    /// Exports accumulated sensor data to a timestamped CSV file.
    /// </summary>
    public string? ExportData()
    {
        if (_exportData is null || _pipeline is null) return null;

        var sb       = new StringBuilder();
        int nChannels = _exportData.Count;

        // Header row
        foreach (var comp in _pipeline.TrignoRfManager.Components
                                      .Where(c => c.State == SelectionState.Allocated))
            foreach (var ch in comp.TrignoChannels)
                sb.Append($"{comp.Properties.Sid} {ch.Name},");
        sb.AppendLine();

        // Find longest channel
        int maxLen = _exportData.Max(ch => ch.Count);

        for (int i = 0; i < maxLen; i++)
        {
            for (int j = 0; j < nChannels; j++)
                sb.Append(i < _exportData[j].Count ? $"{_exportData[j][i]}," : ",");
            sb.AppendLine();
        }

        string dir  = "./sensor_data";
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
        File.WriteAllText(path, sb.ToString());

        Console.WriteLine($"[Delsys] Exported data to {path}");
        return path;
    }

    /// <summary>Writes a diagnostic mapping file showing vector column assignments.</summary>
    private void WriteMappingFile()
    {
        string dir  = "./delsys_logs";
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"mapping_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        sw.WriteLine("# Delsys Sensor Mapping");
        sw.WriteLine($"# Created {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sw.WriteLine("# Column\tSensor-GUID\tChannelName\tSampleRate\tSPF\tFrameInterval\tPair#\tMode");

        for (int i = 0; i < ChannelInfos.Count; i++)
        {
            var ci   = ChannelInfos[i];
            var comp = _pipeline!.TrignoRfManager.Components.First(c => c.Id == ci.SensorId);
            var mode = _modeNameBySensor.TryGetValue(ci.SensorId, out var m) ? m : "n/a";

            sw.WriteLine($"{i}\t{ci.SensorId}\t{ci.ChannelName}\t{ci.SampleRate:F1}\t" +
                         $"{ci.SamplesPerFrame}\t{ci.FrameInterval:F4}\t{comp.PairNumber}\t{mode}");
        }

        Console.WriteLine($"[Delsys] Mapping file written: {path}");
    }

    #endregion

    #region Utility

    /// <summary>
    /// Copies the first sensor's selected mode index to every other sensor
    /// in <see cref="DiscoveredSensors"/>. Useful when all sensors should
    /// share the same configuration.
    /// </summary>
    public void ApplyFirstSensorModeToAll()
    {
        if (DiscoveredSensors.Count < 2) return;

        int mode = DiscoveredSensors[0].SelectedModeIndex;
        Console.WriteLine($"[Delsys] Applying mode[{mode}] from first sensor " +
                          $"(SID {DiscoveredSensors[0].Sid}) to all {DiscoveredSensors.Count} sensors");

        for (int i = 1; i < DiscoveredSensors.Count; i++)
            DiscoveredSensors[i].SelectedModeIndex = mode;
    }

    #endregion

}

/// <summary>Sensor info surfaced to the ViewModel for the picker UI.</summary>
public sealed class DelsysSensorInfo : ObservableObject
{
    public Guid   Id         { get; init; }
    public string Name       { get; init; } = string.Empty;
    public int    Sid        { get; init; }
    public int    PairNumber { get; init; }
    public IReadOnlyList<string> SampleModes { get; init; } = Array.Empty<string>();

    /// <summary>UI-bound: whether this sensor is selected for arming.</summary>
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>UI-bound: which sample mode index is chosen for this sensor.</summary>
    private int _selectedModeIndex;
    public int SelectedModeIndex
    {
        get => _selectedModeIndex;
        set => SetProperty(ref _selectedModeIndex, value);
    }
}

/// <summary>Label for a single channel in the output vector, for scope legends.</summary>
public sealed class ChannelLabel
{
    public int    VectorIndex   { get; init; }
    public string Label         { get; init; } = string.Empty;
    public int    SensorSid     { get; init; }
    public int    PairNumber    { get; init; }
    public bool   IsEmg         { get; init; }
    public bool   IsImu         { get; init; }
    public bool   IsAcc         { get; init; }
    public bool   IsGyro        { get; init; }
    public bool   IsOrientation { get; init; }
}
