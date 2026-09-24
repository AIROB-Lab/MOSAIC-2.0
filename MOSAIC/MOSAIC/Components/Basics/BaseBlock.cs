using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;
using MOSAIC.Services;
using MOSAIC.Visualization;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Base class for processing blocks in the MOSAIC pipeline.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="BaseBlock"/> represents one node in a processing graph. Blocks are typically created
/// and linked based on a JSON configuration (pipeline definition).
/// </para>
///
/// <para><b>Two Rate Properties:</b></para>
/// <list type="table">
///   <listheader><term>Property</term><description>Purpose</description></listheader>
///   <item>
///     <term><see cref="DesiredRate"/></term>
///     <description>
///       Emission / display rate in Hz. Determines how often a block publishes output.
///       Modified by windowing blocks (e.g. SlidingWindow: input rows/s divided by stride).
///       Used for status monitoring and tick rate display.
///     </description>
///   </item>
///   <item>
///     <term><see cref="SignalRate"/></term>
///     <description>
///       True sample rate of the underlying signal in Hz. Used for DSP coefficient design
///       (filter cutoffs, FFT bins, spectral resolution). Passes through windowing blocks
///       unchanged — a SlidingWindow with stride 100 at 2000 Hz input still outputs data
///       whose <em>samples</em> are at 2000 Hz, even though the <em>windows</em> arrive at 20 Hz.
///     </description>
///   </item>
/// </list>
///
/// <para><b>Rate Inheritance:</b> Both rates propagate downstream automatically via
/// <see cref="TryInheritRate"/>. Publication rates initially fill unset values; use
/// <see cref="UpdateAndPropagateRate"/> for runtime changes. Sample rates refresh downstream
/// unless a block declares that it derives its own rate.</para>
///
/// <para><b>Publishing:</b> The <see cref="Publish"/> method is thread-safe and non-blocking.
/// If a publish call is already in progress, the new value is dropped and the block enters a
/// "stumbling" state reflected in <see cref="Status"/>.</para>
///
/// <para><b>Automatic CSV Logging:</b> Every successful <see cref="Publish"/> call feeds
/// <see cref="Dumper"/> whenever one is open. A dumper opens either because the config named a
/// <see cref="JsonModel.Path"/> — <see cref="MOSAIC.Components.Factory.BlockFactory"/> calls
/// <see cref="InitDumper"/> for every block it builds — or because <see cref="IsRecording"/> was
/// switched on, which writes to the shared session folder instead. Either way the file is named by
/// <see cref="DumpFilePrefix"/>. A block whose <c>Path</c> means something other than a recording
/// folder overrides <see cref="PathIsDumpFolder"/>.</para>
/// </remarks>
public abstract partial class BaseBlock : ObservableObject, ISubscriber, IPublisher, IBaseBlockProps, IJsonExportable, IDisposable, IAsyncDisposable
{
    #region Private Fields

    /// <summary>Fans published values out to subscribers, one queue per subscriber.</summary>
    private readonly IOutputDispatcher _dispatcher = new OutputDispatcher();

    /// <summary>Measures publish cadence, which drives <see cref="Status"/> and <see cref="FrequencyText"/>.</summary>
    private readonly ITickTracker _tickTracker = new TickTracker();

    /// <summary>Guards <see cref="Publish"/>. Held rather than waited on, so a late value is dropped.</summary>
    private readonly object _sendLock = new();

    /// <summary>Guards <see cref="_subscribers"/> against concurrent traversal and mutation.</summary>
    private readonly object _subscriberLock = new();

    /// <summary>Downstream blocks, held weakly, used only for rate propagation.</summary>
    /// <remarks>
    /// Separate from the dispatcher's subscriber list, which is what values actually travel through.
    /// This one exists because rate inheritance needs the concrete <see cref="BaseBlock"/> rather than
    /// the <see cref="ISubscriber"/> the dispatcher stores, and it is weak so a block downstream
    /// cannot keep its publisher alive. Dead entries are pruned during traversal.
    /// </remarks>
    private readonly List<WeakReference<BaseBlock>> _subscribers = new();

    /// <summary>Set when a publish was dropped because the previous one was still dispatching.</summary>
    /// <remarks>
    /// Volatile because <see cref="Publish"/> writes it on the producing thread while the status timer
    /// reads it on another.
    /// </remarks>
    private volatile bool _isStumbling;

    /// <summary>Backing field for <see cref="DesiredRate"/>.</summary>
    private double _desiredRate;

    /// <summary>Backing field for <see cref="SignalRate"/>.</summary>
    private double _signalRate;

    /// <summary>Backing field for <see cref="InputRate"/>.</summary>
    private double _inputRate;

    /// <summary>Names of the upstream blocks this one was wired to, kept for JSON round-trip.</summary>
    private IReadOnlyList<string>? _inputConnections;

    /// <summary>Guard making <see cref="Dispose(bool)"/> idempotent.</summary>
    private bool _disposed;

    #endregion

    #region Observable Properties

    /// <summary>Publication activity, refreshed by the shared 250 ms status timer.</summary>
    [ObservableProperty] private BlockStatus _status = BlockStatus.Idle;

    /// <summary>Human-readable frequency string (e.g. <c>"125.0 Hz"</c>) for the UI.</summary>
    [ObservableProperty] private string _frequencyText = "0";

    /// <summary>Display name of the block.</summary>
    [ObservableProperty] private string _name = string.Empty;

    #endregion

    #region Rate Properties

    /// <summary>
    /// Emission / display rate in Hz. Modified by windowing blocks.
    /// </summary>
    /// <remarks>
    /// If not explicitly set (0), the rate is automatically inherited from upstream blocks.
    /// Once set, it propagates to all downstream blocks that don't have a rate yet.
    /// Override <see cref="OnDesiredRateChanged"/> to react (e.g. rebuild filters).
    /// </remarks>
    public double DesiredRate
    {
        get => _desiredRate;
        set
        {
            if (Math.Abs(_desiredRate - value) < 0.001) return;
            var oldRate = _desiredRate;
            _desiredRate = value;
            OnPropertyChanged();
            RefreshVisualizationRate();

            if (value > 0)
            {
                OnDesiredRateChanged(oldRate, value);
                PropagateRatesToSubscribers(force: false);
            }
        }
    }

    /// <summary>
    /// True sample rate of the signal in Hz. Used for DSP coefficient design.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Propagates independently of <see cref="DesiredRate"/>. Windowing blocks
    /// (e.g. SlidingWindow) change <see cref="DesiredRate"/> to reflect the output
    /// window rate but leave <see cref="SignalRate"/> unchanged, because the samples
    /// <em>inside</em> each window are still at the original rate.
    /// </para>
    /// <para>
    /// For simple pipelines without windowing, <see cref="SignalRate"/> and
    /// <see cref="DesiredRate"/> will be equal. Blocks that need a sample rate for
    /// DSP (filters, FFT, adaptive filters) should prefer <see cref="SignalRate"/>
    /// and fall back to <see cref="DesiredRate"/> if it's zero:
    /// <code>double fs = SignalRate > 0 ? SignalRate : DesiredRate > 0 ? DesiredRate : 1000;</code>
    /// </para>
    /// <para>
    /// Source blocks (device drivers) should set this to the true hardware sample rate
    /// in their connect/start method.
    /// </para>
    /// </remarks>
    public double SignalRate
    {
        get => _signalRate;
        set
        {
            if (Math.Abs(_signalRate - value) < 0.001) return;
            _signalRate = value;
            OnPropertyChanged();
            OnSignalRateChanged(value);
            PropagateSignalRateToSubscribers();
        }
    }

    /// <summary>
    /// Input rate received from upstream blocks. Set automatically by <see cref="TryInheritRate"/>.
    /// </summary>
    /// <remarks>
    /// Useful for blocks that calculate their output rate from the input rate
    /// (e.g. SlidingWindow: <c>InputRate / Stride</c>).
    /// </remarks>
    public double InputRate
    {
        get => _inputRate;
        protected set
        {
            if (Math.Abs(_inputRate - value) < 0.001) return;
            _inputRate = value;
            OnPropertyChanged();
            RefreshVisualizationRate();
            OnInputRateChanged(value);
        }
    }

    /// <summary>
    /// Effective sample rate for DSP coefficient design. Prefers <see cref="SignalRate"/>,
    /// falls back to <see cref="DesiredRate"/>, then 1000 Hz as a safe default.
    /// </summary>
    /// <remarks>
    /// Convenience property for blocks that need a sample rate (filters, FFT, etc.).
    /// Avoids repeating the fallback chain in every DSP block.
    /// </remarks>
    public double EffectiveSampleRate =>
        SignalRate > 0 ? SignalRate :
        DesiredRate > 0 ? DesiredRate : 1000;

    #endregion

    #region Rate Hooks

    /// <summary>
    /// This block's visualization, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blocks own their scope as a public <c>Viz</c> property rather than inheriting one, so the
    /// base class has no way to reach it. Overriding this is what lets the rate below be wired up
    /// automatically. With correct publication metadata, the bundle derives the time axis for
    /// one vector per publication or fixed-size contiguous sample packets from the feed shape.
    /// </para>
    /// <para>
    /// When a plot needs a rate that publication frequency and feed shape cannot express (for
    /// example, variable-sized packets or overlapping windows), declare it with
    /// <c>Viz.UpdateSignalRate</c>. An explicit declaration wins over the derived rate. A block
    /// with several bundles must configure each one; this hook exposes only one bundle.
    /// </para>
    /// </remarks>
    protected virtual BlockVisualization? Visualization => null;

    /// <summary>
    /// Keeps <see cref="Visualization"/> told how often this block publishes.
    /// Called automatically on every rate change; a block whose visualization is assigned
    /// from outside (its ViewModel owns the scope) must also call this from that setter,
    /// because the rate was pushed before the scope existed.
    /// </summary>
    /// <remarks>
    /// The scope needs rows per second, which is this rate times the rows each feed carries — a
    /// count only the visualization can see, so it does the multiplication. Doing it here means a
    /// block gets a correct time axis without knowing the scope exists: before this, 30 of the 37
    /// blocks that plot never declared any rate and every one of them was drawn against a fixed
    /// 5 ms period that had nothing to do with their signal.
    /// </remarks>
    protected void RefreshVisualizationRate()
    {
        var rate = _desiredRate > 0 ? _desiredRate : _inputRate;
        if (rate > 0) Visualization?.UpdatePublishRate(rate);
    }

    /// <summary>Called when <see cref="DesiredRate"/> changes. Override to react (e.g. rebuild filters).</summary>
    /// <param name="oldRate">The previous rate in Hz; <c>0</c> when none had been set.</param>
    /// <param name="newRate">The new rate in Hz. Always greater than <c>0</c>.</param>
    protected virtual void OnDesiredRateChanged(double oldRate, double newRate) { }

    /// <summary>Called when <see cref="SignalRate"/> changes. Override to rebuild DSP coefficients.</summary>
    /// <param name="newRate">The new true sample rate in Hz.</param>
    protected virtual void OnSignalRateChanged(double newRate) { }

    /// <summary>Called when <see cref="InputRate"/> changes. Override to recalculate output rate.</summary>
    /// <param name="newInputRate">The rate in Hz at which the upstream block now publishes.</param>
    protected virtual void OnInputRateChanged(double newInputRate) { }

    /// <summary>True when this block computes its own output cadence from its input.</summary>
    protected virtual bool TransformsPublicationRate => false;

    /// <summary>True when this block produces a new sample spacing (for example resampling).</summary>
    protected virtual bool TransformsSignalRate => false;

    #endregion

    #region Connection Properties

    /// <summary>
    /// Input connections from JSON configuration. Used for round-trip JSON export.
    /// </summary>
    public IReadOnlyList<string>? Inputs
    {
        get => _inputConnections;
        set => _inputConnections = value;
    }

    /// <summary>
    /// Gets the minimum number of input connections required for this block.
    /// </summary>
    /// <remarks>
    /// Override in derived classes to specify input requirements.
    /// Default is 1 (most blocks require exactly one input).
    /// Source blocks (devices, generators) should return 0.
    /// </remarks>
    public virtual int MinInputs => 1;

    /// <summary>
    /// Gets the maximum number of input connections allowed for this block.
    /// </summary>
    /// <remarks>
    /// Override in derived classes to specify input requirements.
    /// Default is 1 (most blocks accept exactly one input).
    /// Use <see cref="int.MaxValue"/> for blocks that accept unlimited inputs (e.g., Joiner).
    /// </remarks>
    public virtual int MaxInputs => 1;

    /// <summary>
    /// Type names this block accepts as inputs, or an empty array when it accepts any.
    /// </summary>
    /// <value>Upstream runtime class names, as checked by BlockConstraints. Empty means unrestricted.</value>
    /// <remarks>
    /// Advisory metadata for the pipeline builder, which uses it to grey out incompatible sources
    /// while a connection is being dragged. Not enforced at run time — a config that wires an
    /// unlisted block in still builds.
    /// </remarks>
    public virtual string[] AllowableBlocks => [];

    #endregion

    #region CSV Logging

    /// <summary>
    /// Optional CSV dumper for automatic publish logging. Non-null exactly while the block is
    /// recording; <see cref="Publish"/> feeds it on every value.
    /// </summary>
    protected CsvDumper? Dumper { get; set; }

    /// <summary>Where a recording goes. Supplied by the factory; null for blocks built outside it.</summary>
    private IRecordingDestination? _recordingDestination;

    /// <summary>A close still in flight. Held so a reopen can wait for the file handle to be released.</summary>
    private Task? _dumperClosing;

    /// <summary>
    /// Folder and file name an explicit <see cref="JsonModel.Path"/> opened this block's dumper with,
    /// or null when the block records to the shared session folder.
    /// </summary>
    /// <remarks>
    /// Held so the toggle reopens the file the config named. Without it, switching a Path-configured
    /// block off and back on silently relocated the rest of the session into the shared folder under
    /// a different file name, splitting one block's data across two places.
    /// </remarks>
    private string? _explicitDumpFolder;

    /// <summary>
    /// File name (without extension) this block records under. Defaults to <see cref="Name"/>, which
    /// is unique within a pipeline; override only where a block writes something the block name does
    /// not describe.
    /// </summary>
    /// <remarks>
    /// This is the single file-name authority for configuration-driven and UI-driven recording,
    /// ensuring both paths write the block to the same destination.
    /// </remarks>
    protected virtual string DumpFilePrefix => Name;

    /// <summary>
    /// Whether <see cref="JsonModel.Path"/> means "record CSV here" for this block. True for every
    /// block but the handful that read <c>Path</c> as something else.
    /// </summary>
    /// <remarks>
    /// The explicit opt-out makes alternate <c>Path</c> semantics visible to callers and reviewers.
    /// Override this to <see langword="false"/> only where <c>Path</c> addresses something else,
    /// such as the model directory of a predictor.
    /// </remarks>
    protected virtual bool PathIsDumpFolder => true;

    /// <summary>
    /// True when a recording destination has been chosen, so <see cref="IsRecording"/> can actually
    /// be turned on. Bind menu items to this rather than offering a toggle that silently fails.
    /// </summary>
    public bool CanRecord => _recordingDestination?.IsConfigured ?? false;

    /// <summary>
    /// Whether this block is writing every published value to CSV. Safe to flip while the pipeline
    /// is running: turning it on opens a file in the current session folder, turning it off flushes
    /// and closes it.
    /// </summary>
    /// <remarks>
    /// Setting this to <see langword="true"/> with no destination configured leaves it
    /// <see langword="false"/> — a block must never appear to be recording when nothing is on disk.
    /// </remarks>
    [ObservableProperty] private bool _isRecording;

    /// <summary>
    /// Opens this block's dumper on an explicit <see cref="JsonModel.Path"/>, if the config named one
    /// and <see cref="PathIsDumpFolder"/> allows it.
    /// </summary>
    /// <param name="sp">Service provider, passed through to the dumper.</param>
    /// <param name="path">The config's <c>Path</c>. Null or empty means the block does not record.</param>
    /// <remarks>
    /// <para>
    /// Called once by <see cref="MOSAIC.Components.Factory.BlockFactory"/> for every block, so that
    /// "a <c>Path</c> in the config means the block records there" holds structurally rather than by
    /// each <c>ConfigureInput</c> remembering to ask for it. It is deliberately not
    /// <see langword="public"/>: blocks declare intent by overriding <see cref="PathIsDumpFolder"/>
    /// and <see cref="DumpFilePrefix"/>, not by calling this.
    /// </para>
    /// <para>
    /// This is also what marks the block as having a per-block destination, so the toggle and the
    /// JSON round-trip both keep pointing at the same folder.
    /// </para>
    /// </remarks>
    protected internal void InitDumper(IServiceProvider sp, string? path)
    {
        if (string.IsNullOrEmpty(path) || !PathIsDumpFolder) return;

        _explicitDumpFolder = path;

        Dumper = CsvDumper.ConfigureInput(sp, path, DumpFilePrefix);
    }

    /// <summary>
    /// Wires the block to the shared recording destination. Called by the factory once
    /// <c>ConfigureInput</c> has run.
    /// </summary>
    /// <param name="destination">The shared destination, or <see langword="null"/> to disable recording.</param>
    /// <remarks>
    /// <para>
    /// The config says nothing about recording beyond the long-standing <see cref="JsonModel.Path"/>,
    /// so the starting state is read from whether <c>ConfigureInput</c> actually opened a dumper. That
    /// is deliberately not read from <c>Path</c> itself: for the predictor blocks <c>Path</c> is where
    /// a trained model is saved, and treating it as a dump folder would drop CSV into it.
    /// </para>
    /// <para>
    /// A block whose dumper came from <c>Path</c> keeps that folder for the rest of the session: the
    /// toggle stops and resumes the same file rather than relocating it.
    /// </para>
    /// </remarks>
    public void AttachRecording(IRecordingDestination? destination)
    {
        _recordingDestination = destination;
        OnPropertyChanged(nameof(CanRecord));

        // ConfigureInput opened one from Path, so the block is already recording — the switch has to
        // show that rather than contradict it.
        if (Dumper is null) return;

        _isRecording = true;
        OnPropertyChanged(nameof(IsRecording));
    }

    /// <summary>Opens or closes the dumper when <see cref="IsRecording"/> is set.</summary>
    /// <param name="value">
    /// The new switch state. <see langword="true"/> opens a file, <see langword="false"/> flushes and
    /// closes the current one.
    /// </param>
    /// <remarks>
    /// Generated by the MVVM Toolkit as the partial hook for the <c>IsRecording</c> property. Turning
    /// the switch on with nowhere to write puts it straight back to <see langword="false"/>, so the
    /// UI can never show a block as recording while nothing reaches disk.
    /// </remarks>
    partial void OnIsRecordingChanged(bool value)
    {
        if (!value) { CloseDumper(); return; }
        if (OpenDumper()) return;

        // No destination, or the folder could not be opened. Fall back rather than leaving the block
        // showing as recording while nothing is being written.
        _isRecording = false;
        OnPropertyChanged(nameof(IsRecording));
    }

    /// <summary>Opens the dumper in this block's recording folder.</summary>
    /// <returns>
    /// <see langword="true"/> if a dumper is open when this returns, whether it was already open or
    /// newly created; <see langword="false"/> if no folder is available or it could not be opened.
    /// </returns>
    private bool OpenDumper()
    {
        if (Dumper is not null) return true;

        // An explicit per-block Path wins over the shared folder, so resuming a legacy recording
        // continues the file the config named instead of starting a second one elsewhere.
        var folder = _explicitDumpFolder ?? _recordingDestination?.SessionFolder;
        if (string.IsNullOrWhiteSpace(folder)) return false;

        WaitForPendingClose();

        try
        {
            Dumper = new CsvDumper(folder, DumpFilePrefix);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, Name, ex, $"Could not start recording in '{folder}'.");
            return false;
        }
    }

    /// <summary>Detaches the dumper from the publish path, then flushes and closes it in the background.</summary>
    private void CloseDumper()
    {
        var dumper = Dumper;
        if (dumper is not null) _lastRecording = dumper;
        Dumper = null;   // cleared first: Publish must stop feeding it before it starts closing

        // Rows racing in after this point hit a completed channel and are dropped, not thrown.
        if (dumper is not null)
        {
            _dumperClosing = dumper.DisposeAsync().AsTask();
            TrackCleanup(_dumperClosing);
        }
    }

    /// <summary>
    /// Blocks until a close started earlier has released the file, so a reopen can take the handle.
    /// </summary>
    /// <remarks>
    /// Switching a recording off is deliberately non-blocking. The dumper owns its
    /// <see cref="System.IO.FileStream"/> until asynchronous disposal finishes, so reopening the same
    /// file must wait for that handle. The wait occurs only during an immediate user-driven reopen,
    /// not on the data path.
    /// </remarks>
    private void WaitForPendingClose()
    {
        if (_dumperClosing is not { } closing) return;
        _dumperClosing = null;

        try
        {
            if (!closing.Wait(TimeSpan.FromSeconds(5)))
                Log.Warn(GetType().Name, Name, "Previous recording did not close within 5 s; reopening anyway.");
        }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, Name, ex, "Previous recording failed to close.");
        }
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Registers publication-based status monitoring with the shared 250 ms timer.
    /// </summary>
    protected BaseBlock()
    {
        BlockStatusTimer.Instance.Subscribe(RefreshStatus);
    }


    private void RefreshStatus()
    {
        if (_stopping) return;
        Status = _tickTracker.GetStatus(DesiredRate, _isStumbling);
        RefreshDiagnostics();
        FrequencyText = $"{_tickTracker.CurrentRate:F1} Hz";
    }

    /// <summary>
    /// Initializes a new <see cref="BaseBlock"/> with the specified name and optional desired rate.
    /// </summary>
    protected BaseBlock(string name, double desiredRate = 0) : this()
    {
        Name = name;
        if (desiredRate > 0)
            DesiredRate = desiredRate;
    }

    #endregion

    #region JSON Export

    /// <summary>Block type identifier for JSON serialization. Override for a custom name.</summary>
    protected virtual string JsonTypeName => GetType().Name;

    /// <summary>Block-specific parameters for JSON. Override to provide custom parameters.</summary>
    /// <returns>
    /// The values for <see cref="JsonModel.Params"/>, in the order this block's <c>ConfigureInput</c>
    /// reads them, or <see langword="null"/> when the block takes no parameters.
    /// </returns>
    protected virtual IReadOnlyList<object>? GetJsonParams() => null;

    /// <summary>Overrides what this block writes to <see cref="JsonModel.Path"/>.</summary>
    /// <returns>
    /// The path to export, or <see langword="null"/> to fall back to the block's own recording folder.
    /// </returns>
    /// <remarks>
    /// Only for blocks that read <c>Path</c> as something other than a recording folder — a
    /// predictor's model directory, say. Such a block also overrides
    /// <see cref="PathIsDumpFolder"/>; without this override it would export <see langword="null"/>
    /// and saving the pipeline would silently drop the directory.
    /// </remarks>
    protected virtual string? GetJsonPath() => null;

    /// <summary>Exports the block configuration to a <see cref="JsonModel"/>.</summary>
    /// <returns>A model equivalent to the config entry this block was built from.</returns>
    public virtual JsonModel ToJsonModel()
    {
        return new JsonModel
        {
            Type = JsonTypeName,
            Name = Name,
            Inputs = _inputConnections,
            DesiredRate = DesiredRate > 0 ? DesiredRate : null,
            Params = GetJsonParams(),

            // A per-block dump folder is part of the config and has to survive a save, or reloading
            // would quietly move the recording to the shared folder. GetJsonPath wins because a block
            // that overrides it is using Path for something else entirely.
            Path = GetJsonPath() ?? _explicitDumpFolder
        };
    }

    /// <summary>
    /// Stores the original input connections for later JSON export.
    /// Called during pipeline construction.
    /// </summary>
    /// <param name="model">The config entry this block was built from.</param>
    public void SetInputsFromConfig(JsonModel model) => _inputConnections = model.Inputs;

    #endregion

    #region Rate Inheritance and Propagation

    /// <summary>
    /// Refreshes inherited sample metadata and fills an unset publication rate.
    /// Also updates <see cref="InputRate"/> unconditionally for tracking.
    /// </summary>
    /// <param name="source">The upstream block that delivered the value. Ignored when it has no rate.</param>
    private void TryInheritRate(BaseBlock source)
    {
        if (source == null || source.DesiredRate <= 0) return;

        InputRate = source.DesiredRate;

        // Refresh sample spacing unless the receiver defines its own output spacing.
        if (!TransformsSignalRate && source._signalRate > 0)
            SignalRate = source._signalRate;

        // Inherit DesiredRate if we don't have one (latch on first assignment)
        if (_desiredRate <= 0)
            DesiredRate = source.DesiredRate;
    }

    /// <summary>
    /// Propagates both <see cref="DesiredRate"/> and <see cref="SignalRate"/> to downstream
    /// subscribers. Cleans up stale weak references during traversal.
    /// </summary>
    /// <param name="force">
    /// When <see langword="true"/>, overrides existing <see cref="DesiredRate"/> on downstream blocks.
    /// Sample-rate metadata refreshes unless the receiver derives its own sample rate.
    /// </param>
    private void PropagateRatesToSubscribers(bool force = false)
    {
        if (DesiredRate <= 0) return;

        lock (_subscriberLock)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (_subscribers[i].TryGetTarget(out var block))
                {
                    // Preserve sample spacing through ordinary blocks and rate boundaries at transformers.
                    if (_signalRate > 0 && !block.TransformsSignalRate)
                        block.SignalRate = _signalRate;

                    block.InputRate = DesiredRate;

                    if (force || block._desiredRate <= 0)
                    {
                        Debug.WriteLine($"[{Name}] Propagating rate {DesiredRate:F0} Hz to {block.Name}");
                        block.SetRateAndPropagate(DesiredRate, force);
                    }
                }
                else
                {
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Propagates <see cref="SignalRate"/> to downstream subscribers that do not derive their own rate.
    /// Called from the <see cref="SignalRate"/> setter.
    /// </summary>
    private void PropagateSignalRateToSubscribers()
    {
        if (_signalRate <= 0) return;

        lock (_subscriberLock)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (_subscribers[i].TryGetTarget(out var sub))
                {
                    if (!sub.TransformsSignalRate)
                        sub.SignalRate = _signalRate;
                }
                else
                {
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Sets this block's sample rate and refreshes downstream sample metadata. Propagation
    /// stops at blocks that derive their own output sample spacing.
    /// </summary>
    /// <param name="newRate">The new true sample rate in Hz. Values of <c>0</c> or less are ignored.</param>
    protected void UpdateAndPropagateSignalRate(double newRate)
    {
        if (newRate <= 0 || Math.Abs(_signalRate - newRate) < 0.001) return;

        _signalRate = newRate;
        OnPropertyChanged(nameof(SignalRate));
        OnSignalRateChanged(newRate);

        lock (_subscriberLock)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (_subscribers[i].TryGetTarget(out var sub))
                {
                    if (!sub.TransformsSignalRate) sub.UpdateAndPropagateSignalRate(newRate);
                }
                else
                    _subscribers.RemoveAt(i);
            }
        }
    }

    /// <summary>Sets this block's rate and continues propagation down the subscriber chain.</summary>
    /// <param name="newRate">The rate in Hz to adopt.</param>
    /// <param name="force">
    /// Passed straight on, so the whole chain propagates the same way. See
    /// <see cref="PropagateRatesToSubscribers"/>.
    /// </param>
    private void SetRateAndPropagate(double newRate, bool force)
    {
        InputRate = newRate;
        if (TransformsPublicationRate) return;
        if (Math.Abs(_desiredRate - newRate) < 0.001) return;

        var oldRate = _desiredRate;
        _desiredRate = newRate;
        OnPropertyChanged(nameof(DesiredRate));
        RefreshVisualizationRate();
        OnDesiredRateChanged(oldRate, newRate);

        PropagateRatesToSubscribers(force);
    }

    /// <summary>
    /// Forces propagation of a new <see cref="DesiredRate"/> to the entire downstream chain.
    /// </summary>
    /// <remarks>
    /// Use when the block's output rate changes dynamically (e.g. SlidingWindow stride change).
    /// Unlike normal propagation, this overrides latched rates on all downstream blocks.
    /// The current <see cref="SignalRate"/> propagates alongside, respecting sample-rate transformers.
    /// </remarks>
    /// <param name="newRate">The new emission rate in Hz. Values of <c>0</c> or less are ignored.</param>
    /// <param name="force">
    /// <see langword="true"/> to overwrite rates downstream blocks have already latched.
    /// </param>
    protected void UpdateAndPropagateRate(double newRate, bool force = true)
    {
        if (newRate <= 0) return;

        var oldRate = _desiredRate;
        _desiredRate = newRate;
        OnPropertyChanged(nameof(DesiredRate));
        RefreshVisualizationRate();
        OnDesiredRateChanged(oldRate, newRate);

        PropagateRatesToSubscribers(force);
    }

    /// <summary>
    /// Force-propagates this block's current <see cref="DesiredRate"/> to the entire downstream
    /// chain, overriding rates that downstream blocks latched on a previous run. Use from a source
    /// block whose rate is changed at runtime so the change reaches already-initialised consumers
    /// (the default <see cref="DesiredRate"/> setter propagates softly and a latched downstream rate
    /// would otherwise block it).
    /// </summary>
    protected void ForcePropagateDesiredRate() => PropagateRatesToSubscribers(force: true);

    #endregion

    #region IPublisher

    /// <inheritdoc />
    public void AddSubscriber(ISubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        _dispatcher.AddSubscriber(subscriber);

        if (subscriber is BaseBlock block)
        {
            lock (_subscriberLock)
            {
                // Match OutputDispatcher's idempotent registration. Otherwise removing one
                // connection can leave a duplicate behind that still receives rate changes.
                foreach (var reference in _subscribers)
                    if (reference.TryGetTarget(out var existing) && ReferenceEquals(existing, block))
                        return;

                _subscribers.Add(new WeakReference<BaseBlock>(block));
            }

            // Eagerly propagate both rates on connection
            if (_signalRate > 0 && !block.TransformsSignalRate)
                block.SignalRate = _signalRate;

            block.InputRate = _desiredRate;

            if (_desiredRate > 0 && block._desiredRate <= 0)
                block.DesiredRate = _desiredRate;
        }
    }

    /// <inheritdoc />
    public void RemoveSubscriber(ISubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        _dispatcher.RemoveSubscriber(subscriber);

        if (subscriber is BaseBlock block)
        {
            lock (_subscriberLock)
            {
                for (int i = _subscribers.Count - 1; i >= 0; i--)
                {
                    if (!_subscribers[i].TryGetTarget(out var target) || ReferenceEquals(target, block))
                    {
                        _subscribers.RemoveAt(i);
                    }
                }
            }
        }
    }

    #endregion

    #region ISubscriber

    /// <inheritdoc />
    public void ReceiveInput(object sender, object value)
    {
        lock (_lifetimeLock)
        {
            if (_stopping) return;
            _activeReceivers++;
        }
        try
        {
            if (sender is BaseBlock source)
                TryInheritRate(source);
            OnReceive(sender, value);
        }
        finally
        {
            lock (_lifetimeLock)
            {
                if (--_activeReceivers == 0)
                    _receiversStopped?.TrySetResult();
            }
        }
    }

    #endregion

    #region Abstract Methods

    /// <summary>
    /// Processes incoming data from an upstream block.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="sender">The publishing block, or whatever else delivered the value.</param>
    /// <param name="value">
    /// The payload. Blocks are wired by config rather than by type, so an implementation has to check
    /// the run-time type and reject or handle a shape it cannot use. Status is updated automatically
    /// from publication activity; it is not a per-value validation result.
    /// </param>
    /// <remarks>Called on the dispatching thread, not the UI thread.</remarks>
    protected abstract void OnReceive(object sender, object value);

    #endregion

    #region Publishing

    /// <summary>
    /// Publishes an output value to all registered subscribers.
    /// Thread-safe and non-blocking — drops the value if the previous dispatch is still running.
    /// </summary>
    /// <param name="value">The payload to hand downstream, and to the CSV recording when one is open.</param>
    /// <remarks>
    /// The producer must never mutate a published payload again. Consumers must treat it as read-only;
    /// a consumer that needs to change it must make its own copy. Delivery queues retain references.
    /// Dropping rather than queueing is deliberate: a block that cannot keep up should shed load, not
    /// accumulate latency. A dropped value puts the block into <see cref="BlockStatus.Stumbling"/>,
    /// which is what the card's status light reports.
    /// </remarks>
    public void Publish(object value)
    {
        if (!Monitor.TryEnter(_sendLock))
        {
            _isStumbling = true;
            return;
        }

        try
        {
            _isStumbling = false;
            if (_stopping) return;
            Dumper?.Enqueue(TickTracker.Now(), value);
            _dispatcher.Dispatch(this, value);
        }
        finally
        {
            Monitor.Exit(_sendLock);
            _tickTracker.RecordTick();
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public virtual void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed resources: status subscription, output dispatcher, CSV dumper, subscriber references.
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> when called from <see cref="Dispose()"/>. There is no finalizer, so a
    /// <see langword="false"/> pass has nothing to release.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) ReleaseBaseResources();
    }

    private void ReleaseBaseResources()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _ = StopProcessingAsync();
        if (Disposing is { } handlers)
        {
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { Log.Error(GetType().Name, Name, ex, "Disposal notification failed."); }
            }
            Disposing = null;
        }
        BlockStatusTimer.Instance.Unsubscribe(RefreshStatus);

        CloseDumper();

        foreach (var resource in _ownedResources.Values)
        {
            try { (resource as IDisposable)?.Dispose(); }
            catch (Exception ex) { Log.Error(GetType().Name, Name, ex, "Owned resource disposal failed."); }
        }
        _ownedResources.Clear();

        lock (_subscriberLock)
        {
            _subscribers.Clear();
        }

    }

    #endregion
}
