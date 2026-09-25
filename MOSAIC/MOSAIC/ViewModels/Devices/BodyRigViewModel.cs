using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.BodyRig;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.Devices;

/// <summary>One selectable parent for a segment. <see cref="ToString"/> is what the ComboBox shows.</summary>
public sealed record ParentOption(int Index, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel for the <see cref="BodyRig"/> device card.
/// </summary>
/// <remarks>
/// <para>
/// The card is organised around the kinematic chain so the segment hierarchy and each segment's
/// assigned IMU can be read together.
/// </para>
/// <para>
/// <b>Live values are mirrored, never bound directly.</b> <see cref="BodyRig.Info"/> is mutated
/// from the serial stream thread, and its <c>Flag</c> is overwritten with "Streaming" on every
/// received chunk — so an error state survives about one frame and a poll would essentially never
/// see it. <see cref="RefreshLive"/> samples the durable signals (monotonic counters) on the UI
/// thread and everything the card shows is derived from those mirrors.
/// </para>
/// <para>
/// <b>Write-back is suppressed while reading.</b> Every editable field writes through to the
/// block on change, so filling the fields from the block would otherwise push half-updated values
/// back into it — see <see cref="_suppressWriteBack"/>.
/// </para>
/// </remarks>
public partial class BodyRigViewModel : ObservableObject
{
    private readonly Models.Devices.BodyRig? _block;

    /// <summary>True while the ViewModel is filling its fields from the block.</summary>
    private bool _suppressWriteBack;

    private const float Rad2Deg = 180f / MathF.PI;

    /// <summary>A sensor counted as live if it delivered a packet within this window.</summary>
    private static readonly TimeSpan LiveGrace = TimeSpan.FromMilliseconds(750);

    /// <summary>Parser errors within this window raise the fault banner.</summary>
    private static readonly TimeSpan ErrorWindow = TimeSpan.FromSeconds(3);

    // Liveness bookkeeping, all UI-thread.
    private readonly Dictionary<int, int> _lastSensorCounts = new();
    private readonly Dictionary<int, DateTime> _lastSensorSeen = new();
    private int _lastFrameCount;
    private DateTime _lastRateSample = DateTime.UtcNow;
    private int _lastErrorCount;
    private DateTime _lastErrorAt = DateTime.MinValue;

    /// <summary>The underlying block, for binding Name, FrequencyText and other base members.</summary>
    public Models.Devices.BodyRig? Block => _block;

    #region Connection

    /// <summary>Full free-text serial port name, e.g. "COM5" or "/dev/ttyUSB0".</summary>
    [ObservableProperty] private string _portName = string.Empty;

    /// <summary>Whether the serial port is actually open, mirrored from the block.</summary>
    [ObservableProperty] private bool _isPortOpen;

    /// <summary>Status or error message displayed in the UI.</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Whether the block is being driven by a clock (serial) rather than upstream data.</summary>
    [ObservableProperty] private bool _isSerialMode;

    /// <summary>SERIAL / UPSTREAM / IDLE.</summary>
    [ObservableProperty] private string _modeLabel = "IDLE";

    /// <summary>One-line summary under the title, e.g. "10 segments · 2 chains · 100 Hz".</summary>
    [ObservableProperty] private string _subtitleText = string.Empty;

    #endregion

    #region Health

    /// <summary>Published frames per second, smoothed.</summary>
    [ObservableProperty] private double _frameRateHz;

    /// <summary>Whether frames are arriving at all.</summary>
    [ObservableProperty] private bool _isReceiving;

    /// <summary>Mirror of the block's processed-frame counter.</summary>
    [ObservableProperty] private int _framesProcessed;

    /// <summary>Mirror of the block's parser-error counter.</summary>
    [ObservableProperty] private int _frameErrors;

    /// <summary>Whether any parser error has ever been recorded.</summary>
    [ObservableProperty] private bool _hasFrameErrors;

    /// <summary>Health pill states. Exactly one is true.</summary>
    [ObservableProperty] private bool _healthOk;

    /// <inheritdoc cref="HealthOk"/>
    [ObservableProperty] private bool _healthWarn;

    /// <inheritdoc cref="HealthOk"/>
    [ObservableProperty] private bool _healthBad;

    /// <summary>Health pill text.</summary>
    [ObservableProperty] private string _healthText = "Idle";

    /// <summary>Whether the fault banner is shown.</summary>
    [ObservableProperty] private bool _hasFault;

    /// <summary>Fault banner text, naming a specific and actionable failure.</summary>
    [ObservableProperty] private string _faultText = string.Empty;

    /// <summary>Distinct sensor ids currently delivering packets.</summary>
    [ObservableProperty] private int _sensorsArriving;

    /// <summary>Distinct sensor ids bound to at least one segment.</summary>
    [ObservableProperty] private int _boundSensorCount;

    /// <summary>Bound sensors that are not delivering.</summary>
    [ObservableProperty] private int _boundSensorsDark;

    /// <summary>e.g. "6 arriving · 7 bound · 1 dark".</summary>
    [ObservableProperty] private string _sensorCoverage = string.Empty;

    #endregion

    #region Chain

    /// <summary>The chain strip, one row per segment in tree order.</summary>
    public ObservableCollection<ChainNodeViewModel> Chain { get; } = [];

    /// <summary>e.g. "2 chains · 4 loose".</summary>
    [ObservableProperty] private string _chainSummary = string.Empty;

    /// <summary>Explains a rejected or side-effecting topology edit; <see cref="MOSAIC.Models.Devices.BodyRig.SetParent"/> is silent.</summary>
    [ObservableProperty] private string _topologyWarning = string.Empty;

    /// <summary>Whether <see cref="TopologyWarning"/> has anything to say.</summary>
    [ObservableProperty] private bool _hasTopologyWarning;

    /// <summary>Currently selected segment index for editing.</summary>
    [ObservableProperty] private int _selectedSegment;

    /// <summary>Maximum segment index (segment count − 1).</summary>
    [ObservableProperty] private int _maxSegment;

    /// <summary>Selects a segment from a chain-strip row.</summary>
    [RelayCommand]
    private void SelectSegment(int index) => SelectedSegment = index;

    #endregion

    #region Selected segment

    /// <summary>Name of the selected segment.</summary>
    [ObservableProperty] private string _segmentName = string.Empty;

    /// <summary>Labels for the sensor picker: "—" then S0, S1, …</summary>
    public ObservableCollection<string> SensorChoiceLabels { get; } = [];

    /// <summary>
    /// Selected index in <see cref="SensorChoiceLabels"/>, which is <c>SensorIndex + 1</c>.
    /// </summary>
    /// <remarks>
    /// The offset makes the model's <c>-1</c> "passive, no sensor" value selectable without
    /// changing the stored sensor index.
    /// </remarks>
    [ObservableProperty] private int _sensorChoice;

    /// <summary>e.g. "S3 · live" / "S9 · never seen" / "passive — no sensor".</summary>
    [ObservableProperty] private string _sensorStatusText = string.Empty;

    /// <summary>Whether the selected segment's sensor is delivering.</summary>
    [ObservableProperty] private bool _sensorIsLive;

    /// <summary>Legal parents for the selected segment.</summary>
    public ObservableCollection<ParentOption> ParentOptions { get; } = [];

    /// <summary>Selected parent; assigning re-parents the segment.</summary>
    [ObservableProperty] private ParentOption? _selectedParentOption;

    /// <summary>Link length X in cm.</summary>
    [ObservableProperty] private float _linkX;

    /// <summary>Link length Y in cm.</summary>
    [ObservableProperty] private float _linkY;

    /// <summary>Link length Z in cm.</summary>
    [ObservableProperty] private float _linkZ;

    /// <summary>DH roll about X, degrees.</summary>
    [ObservableProperty] private float _dhRoll;

    /// <summary>DH pitch about Y, degrees.</summary>
    [ObservableProperty] private float _dhPitch;

    /// <summary>DH yaw about Z, degrees.</summary>
    [ObservableProperty] private float _dhYaw;

    /// <summary>The DH mapping as a quaternion — the lossless form a calibration file stores.</summary>
    [ObservableProperty] private string _selectedDhQuaternionText = string.Empty;

    #endregion

    #region Joint readout

    /// <summary>Joint roll relative to the parent, degrees.</summary>
    [ObservableProperty] private float _jointRoll;

    /// <summary>Joint pitch relative to the parent, degrees.</summary>
    [ObservableProperty] private float _jointPitch;

    /// <summary>Joint yaw relative to the parent, degrees.</summary>
    [ObservableProperty] private float _jointYaw;

    /// <summary>Which frame the joint angles are expressed in.</summary>
    [ObservableProperty] private string _jointFrameLabel = string.Empty;

    /// <summary>Distance of the segment tip from the origin, cm.</summary>
    [ObservableProperty] private float _reachCm;

    /// <summary>A plain-language note about implausible geometry, or empty.</summary>
    [ObservableProperty] private string _sanityHint = string.Empty;

    #endregion

    #region Chain setup

    /// <summary>Staged segment count; applied by <see cref="ApplySegmentCountCommand"/>.</summary>
    /// <remarks>
    /// Staged rather than live because the block rebuilds its chain on every assignment. Bound
    /// directly, typing "10" over "1" ran the destructive rebuild twice on the way through "1".
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentCountDirty))]
    private int _pendingSegmentCount = 10;

    /// <summary>Whether the staged count differs from the block's actual chain.</summary>
    public bool SegmentCountDirty => _block is not null && PendingSegmentCount != _block.SegmentCount;

    #endregion

    #region Calibration

    /// <summary>Directory scanned for calibration profiles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoreTargetHint))]
    private string _calibrationDirectory = string.Empty;

    /// <summary>Discovered profile paths.</summary>
    public ObservableCollection<string> Profiles { get; } = [];

    /// <summary>The profile Load and the arrows act on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoreTargetHint))]
    private string? _selectedProfile;

    /// <summary>e.g. "3 / 7", or a note that no directory is set.</summary>
    [ObservableProperty] private string _profileCounter = "no profile directory";

    /// <summary>Whether any profiles were found.</summary>
    [ObservableProperty] private bool _hasProfiles;

    /// <summary>
    /// Folder a card falls back to when its pipeline names none: the calibration profiles
    /// deployed with the app under <c>Assets\BodyRigCalibrationFiles</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those profiles are deployed as a real folder rather than embedded resources, because the
    /// card lists them with <c>Directory.GetFiles</c> and Store writes new ones back — neither
    /// works against a resource compiled into the assembly.
    /// </para>
    /// <para>
    /// Settable, and nulled by the test bootstrap, so the suite never depends on whether those
    /// files happened to be copied next to the test runner. Same reasoning as
    /// <c>BlockVisualization.Enabled</c>. Read once when a card is built, so assigning it does
    /// not disturb a card that is already open.
    /// </para>
    /// </remarks>
    public static string? DefaultProfileDirectory { get; set; } = FindShippedProfiles();

    /// <summary>Locates the shipped profile folder, or null when it was not deployed.</summary>
    private static string? FindShippedProfiles()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "BodyRigCalibrationFiles");
            return Directory.Exists(dir) ? dir : null;
        }
        catch
        {
            // A base directory this process cannot inspect must not stop a card being built.
            return null;
        }
    }

    /// <summary>
    /// Name for a profile Store should create. Blank means Store overwrites
    /// <see cref="SelectedProfile"/> instead.
    /// </summary>
    /// <remarks>
    /// The card had no such field, and that is precisely why the first profile in a directory
    /// could not be created from inside the app: with nothing selected there was no name to
    /// write to, so Store returned early and said nothing.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoreTargetHint))]
    private string _newProfileName = string.Empty;

    /// <summary>
    /// What Store will do if pressed right now: the file it would write, or what is missing.
    /// </summary>
    /// <remarks>
    /// The folder, the selected profile and the new-name box interact — a name wins over the
    /// selection, and either needs a folder — which is not something a reader should have to
    /// infer from three placeholder strings. Shown next to the button it describes.
    /// </remarks>
    public string StoreTargetHint
    {
        get
        {
            if (_block is null) return string.Empty;

            var target = ResolveStoreTarget(out var problem);
            return target is null ? problem : $"Store → {Path.GetFileName(target)}";
        }
    }

    #endregion

    /// <summary>Observable info from the underlying block.</summary>
    public BodyRigInfo? Info => _block?.Info;

    /// <summary>Live plot of the published pose.</summary>
    public BlockVisualization? Viz => _block?.Viz;

    /// <summary>Label above the pose plot, naming its real channel count.</summary>
    [ObservableProperty] private string _poseStreamLabel = "POSE STREAM";

    #region Construction

    /// <summary>
    /// Initialises the ViewModel. If a block is provided (from JSON config or the palette),
    /// its settings populate the card.
    /// </summary>
    public BodyRigViewModel(Models.Devices.BodyRig? block = null)
    {
        if (block is not null)
        {
            _block = block;
            _portName = block.PortNumber;
            _pendingSegmentCount = block.SegmentCount;
            _maxSegment = Math.Max(0, block.SegmentCount - 1);
            _calibrationDirectory = block.CalibrationDirectory ?? string.Empty;

            // A pipeline that names no folder opens on the shipped profiles instead of an empty
            // box the user has to find a path for. Only ever fills a blank: a folder set in the
            // pipeline wins, and pushing it to the block makes the scan happen before the
            // selection is read below.
            if (string.IsNullOrWhiteSpace(_calibrationDirectory) &&
                DefaultProfileDirectory is { } shipped)
            {
                _calibrationDirectory = shipped;
                block.CalibrationDirectory = shipped;
            }

            _selectedProfile = block.CalibrationName;
        }

        RebuildSensorChoices();
        RebuildChain();
        RefreshProfiles();
        PopulateSegmentValues();
        RefreshLive();
    }

    #endregion

    #region Live refresh

    /// <summary>
    /// Samples every live signal and recomputes the derived state. Called from the view on a
    /// shared 10 Hz tick; see the card's code-behind.
    /// </summary>
    public void RefreshLive()
    {
        if (_block is null) return;

        var now = DateTime.UtcNow;

        // ── frame rate, from a monotonic counter rather than a transient flag ──
        int frames = _block.Info.FramesProcessed;
        double dt = (now - _lastRateSample).TotalSeconds;
        if (dt >= 0.05)
        {
            double instant = Math.Max(0, frames - _lastFrameCount) / dt;
            FrameRateHz = FrameRateHz <= 0 ? instant : (0.3 * instant) + (0.7 * FrameRateHz);
            _lastFrameCount = frames;
            _lastRateSample = now;
        }

        FramesProcessed = frames;
        IsReceiving = FrameRateHz >= 0.5;

        int errors = _block.Info.FrameErrors;
        if (errors > _lastErrorCount) _lastErrorAt = now;
        _lastErrorCount = errors;
        FrameErrors = errors;
        HasFrameErrors = errors > 0;

        // ── per-sensor liveness, from the block's arrival counters ──
        var bound = new HashSet<int>();
        for (int i = 0; i < _block.SegmentCount; i++)
        {
            int s = _block.GetSensorIndex(i);
            if (s >= 0) bound.Add(s);
        }

        int arriving = 0;
        var liveSensors = new HashSet<int>();
        for (int s = 0; s < 64; s++)
        {
            int count = _block.GetSensorPacketCount(s);
            if (count == 0) continue;

            if (!_lastSensorCounts.TryGetValue(s, out int previous) || count > previous)
                _lastSensorSeen[s] = now;
            _lastSensorCounts[s] = count;

            if (_lastSensorSeen.TryGetValue(s, out var seen) && now - seen <= LiveGrace)
            {
                arriving++;
                liveSensors.Add(s);
            }
        }

        SensorsArriving = arriving;
        BoundSensorCount = bound.Count;
        BoundSensorsDark = bound.Count(s => !liveSensors.Contains(s));
        SensorCoverage = $"{arriving} arriving · {bound.Count} bound · {BoundSensorsDark} dark";

        // ── mode / port ──
        IsPortOpen = _block.IsPortOpen;
        IsSerialMode = _block.Mode != BodyRigMode.Upstream;
        ModeLabel = _block.Mode switch
        {
            BodyRigMode.Serial => "SERIAL",
            BodyRigMode.Upstream => "UPSTREAM",
            _ => "IDLE"
        };

        int chains = CountChains();
        SubtitleText = $"{_block.SegmentCount} segments · {chains} chain{(chains == 1 ? "" : "s")}"
                     + $" · {FrameRateHz:F0} Hz";
        PoseStreamLabel = $"POSE STREAM · {_block.SegmentCount} SEGMENTS × 7 = {_block.SegmentCount * 7} CH";

        // ── push liveness onto existing rows; never rebuild at tick rate ──
        if (Chain.Count != _block.SegmentCount)
        {
            RebuildChain();
        }
        else
        {
            foreach (var node in Chain)
            {
                int s = _block.GetSensorIndex(node.Index);
                node.IsLive = s >= 0 && liveSensors.Contains(s);
            }
        }

        UpdateJointReadout(liveSensors);
        UpdateHealth(now, bound.Count);
    }

    /// <summary>Recomputes the health pill and the fault banner from durable signals only.</summary>
    private void UpdateHealth(DateTime now, int boundCount)
    {
        if (_block is null) return;

        bool errorsRecent = _lastErrorAt != DateTime.MinValue && now - _lastErrorAt <= ErrorWindow;

        string? fault = null;

        if (IsSerialMode && _block.Mode == BodyRigMode.Serial && !IsPortOpen)
            fault = $"Port is not open — {_block.Info.Flag}";
        else if (IsReceiving && SensorsArriving == 0)
            fault = "Frames arriving but no sensor id matches any segment — check the sensor bindings";
        else if (errorsRecent)
            fault = $"Parser errors in the last {ErrorWindow.TotalSeconds:F0} s — overflow or frame desync";
        else if (IsReceiving && BoundSensorsDark > 0)
            fault = $"{BoundSensorsDark} of {boundCount} bound sensors are silent";
        else if (_block.Mode == BodyRigMode.Serial && IsPortOpen && !IsReceiving)
            fault = "Port open but no frames — check the dongle, baud rate and wiring";

        FaultText = fault ?? string.Empty;
        HasFault = fault is not null;

        (HealthOk, HealthWarn, HealthBad, HealthText) = (_block.Mode, IsReceiving, fault) switch
        {
            (BodyRigMode.Idle, _, _)   => (false, false, false, "Idle"),
            (_, true, null)            => (true, false, false, "Streaming"),
            (_, true, _)               => (false, true, false, "Degraded"),
            (_, false, _)              => (false, false, true, "No data")
        };
    }

    /// <summary>Number of distinct root-led chains (segments with no parent).</summary>
    private int CountChains()
    {
        if (_block is null) return 0;
        int roots = 0;
        for (int i = 0; i < _block.SegmentCount; i++)
            if (_block.GetParentIndex(i) < 0) roots++;
        return roots;
    }

    #endregion

    #region Chain strip

    /// <summary>
    /// Rebuilds the strip from the block's parent map: roots first, each followed by its
    /// descendants depth-first, then anything unreachable appended as "loose".
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Models.Devices.BodyRig.GetParentIndex"/> alone. Walking
    /// <c>BodySegment.Child</c> would mean re-deriving each segment's array index by reference
    /// identity, which the block does not expose.
    /// </remarks>
    public void RebuildChain()
    {
        Chain.Clear();
        if (_block is null) return;

        int n = _block.SegmentCount;
        var parents = new int[n];
        var children = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            parents[i] = _block.GetParentIndex(i);
            children[i] = [];
        }
        for (int i = 0; i < n; i++)
            if (parents[i] >= 0 && parents[i] < n)
                children[parents[i]].Add(i);

        var visited = new bool[n];

        void Walk(int index, int depth)
        {
            if (visited[index]) return;      // guards against a cycle
            visited[index] = true;
            Chain.Add(MakeNode(index, depth, depth == 0 ? "●" : "└"));
            foreach (int child in children[index]) Walk(child, depth + 1);
        }

        for (int i = 0; i < n; i++)
            if (parents[i] < 0) Walk(i, 0);

        int loose = 0;
        for (int i = 0; i < n; i++)
        {
            if (visited[i]) continue;
            loose++;
            Chain.Add(MakeNode(i, 0, "⚠"));
        }

        int roots = Chain.Count(c => c.Glyph == "●");
        ChainSummary = loose > 0
            ? $"{roots} chain{(roots == 1 ? "" : "s")} · {loose} loose"
            : $"{roots} chain{(roots == 1 ? "" : "s")}";

        SyncSelection();
    }

    private ChainNodeViewModel MakeNode(int index, int depth, string glyph)
    {
        var node = new ChainNodeViewModel(index, depth, glyph);
        UpdateNodeLabels(node);
        node.IsSelected = index == SelectedSegment;
        return node;
    }

    private void UpdateNodeLabels(ChainNodeViewModel node)
    {
        if (_block is null) return;

        var segment = _block.GetSegment(node.Index);
        node.DisplayName = string.IsNullOrWhiteSpace(segment?.Name)
            ? $"segment {node.Index}"
            : segment!.Name!;

        int sensor = _block.GetSensorIndex(node.Index);
        node.SensorLabel = sensor >= 0 ? SlotLabel(sensor) : "—";
        node.SensorTooltip = sensor >= 0
            ? DescribeSlot(sensor)
            : "Passive — no sensor bound, this segment never moves on its own";
    }

    private void SyncSelection()
    {
        foreach (var node in Chain) node.IsSelected = node.Index == SelectedSegment;
    }

    #endregion

    #region Selection & segment editing

    partial void OnSelectedSegmentChanged(int value)
    {
        if (value < 0) { SelectedSegment = 0; return; }
        if (MaxSegment >= 0 && value > MaxSegment) { SelectedSegment = MaxSegment; return; }
        SyncSelection();
        PopulateSegmentValues();
    }

    /// <summary>
    /// Reads the selected segment's properties from the block into the editor fields, without
    /// letting the resulting change notifications write back into the block.
    /// </summary>
    private void PopulateSegmentValues()
    {
        if (_block is null) return;

        _suppressWriteBack = true;
        try
        {
            int seg = SelectedSegment;

            var segment = _block.GetSegment(seg);
            SegmentName = segment?.Name ?? string.Empty;

            // Link lengths are metres in the model, centimetres on the card.
            Vector3F link = _block.GetLinkLengths(seg);
            LinkX = link.X * 100f;
            LinkY = link.Y * 100f;
            LinkZ = link.Z * 100f;

            // DH angles come back as (roll, pitch, yaw) in radians.
            Vector3F dh = _block.GetDhAngles(seg);
            DhRoll = dh.X * Rad2Deg;
            DhPitch = dh.Y * Rad2Deg;
            DhYaw = dh.Z * Rad2Deg;

            var q = segment?.DhRelativeToGyro ?? QuaternionF.Identity;
            SelectedDhQuaternionText = $"q  w {q.W:F4}   x {q.X:F4}   y {q.Y:F4}   z {q.Z:F4}";

            SensorChoice = _block.GetSensorIndex(seg) + 1;

            RebuildParentOptions();
        }
        finally
        {
            _suppressWriteBack = false;
        }
    }

    /// <summary>Rebuilds the legal-parent list for the selected segment.</summary>
    private void RebuildParentOptions()
    {
        if (_block is null) return;

        ParentOptions.Clear();
        ParentOptions.Add(new ParentOption(-1, "— root —"));

        for (int i = 0; i < _block.SegmentCount; i++)
        {
            if (i == SelectedSegment) continue;
            var segment = _block.GetSegment(i);
            string name = string.IsNullOrWhiteSpace(segment?.Name) ? $"segment {i}" : segment!.Name!;
            ParentOptions.Add(new ParentOption(i, $"{i} · {name}"));
        }

        int current = _block.GetParentIndex(SelectedSegment);
        SelectedParentOption = ParentOptions.FirstOrDefault(o => o.Index == current) ?? ParentOptions[0];
    }

    /// <summary>Rebuilds the sensor picker labels, sized to the chain and to ids actually seen.</summary>
    /// <summary>
    /// Slot label carrying the peripheral number when the pipeline resolved one, e.g.
    /// <c>S1 · #92</c>; the bare slot when it did not.
    /// </summary>
    /// <remarks>
    /// Both halves earn their place. The slot is what a binding and the calibration file mean;
    /// the peripheral is the number set on the node that is physically strapped to the
    /// participant. Showing only the slot is why the lab protocol has people copy the mapping
    /// onto paper before they can read the card at all.
    /// </remarks>
    private string SlotLabel(int slot)
    {
        if (slot < 0) return "—";

        return _block?.PeripheralForSlot(slot) is int id ? $"S{slot} · #{id}" : $"S{slot}";
    }

    /// <summary>Hover text naming the slot, the node behind it, and the socket it arrives on.</summary>
    private string DescribeSlot(int slot)
    {
        var source = _block?.SensorSources.FirstOrDefault(s => s.Slot == slot);
        if (source is null) return $"Driven by slot S{slot}";

        return source.PeripheralId is not null
            ? $"Slot S{slot} — peripheral #{source.PeripheralId} on UDP {source.UdpPort}, via {source.SourceName}"
            : $"Slot S{slot} — via {source.SourceName}; this pipeline does not say which peripheral";
    }

    private void RebuildSensorChoices()
    {
        int highest = 15;
        if (_block is not null)
        {
            highest = Math.Max(highest, _block.SegmentCount - 1);
            for (int s = 0; s < 64; s++)
                if (_block.GetSensorPacketCount(s) > 0) highest = Math.Max(highest, s);

            // Every slot the pipeline actually resolved has to be offerable, even one that has
            // not delivered a packet yet - that is the case where a user most needs to see it.
            highest = Math.Max(highest, _block.SensorSources.Count - 1);
        }

        SensorChoiceLabels.Clear();
        SensorChoiceLabels.Add("—");
        for (int s = 0; s <= highest; s++) SensorChoiceLabels.Add(SlotLabel(s));
    }

    partial void OnSegmentNameChanged(string value)
    {
        if (_suppressWriteBack || _block is null) return;
        var segment = _block.GetSegment(SelectedSegment);
        if (segment is null) return;
        segment.Name = string.IsNullOrWhiteSpace(value) ? null : value;

        var node = Chain.FirstOrDefault(c => c.Index == SelectedSegment);
        if (node is not null) UpdateNodeLabels(node);
    }

    partial void OnSensorChoiceChanged(int value)
    {
        if (_suppressWriteBack || _block is null || value < 0) return;
        _block.SetSensorIndex(SelectedSegment, value - 1);

        var node = Chain.FirstOrDefault(c => c.Index == SelectedSegment);
        if (node is not null) UpdateNodeLabels(node);
    }

    partial void OnSelectedParentOptionChanged(ParentOption? value)
    {
        if (_suppressWriteBack || _block is null || value is null) return;

        int requested = value.Index;
        int before = _block.GetParentIndex(SelectedSegment);

        _block.SetParent(SelectedSegment, requested);

        int after = _block.GetParentIndex(SelectedSegment);
        if (after != requested)
        {
            // SetParent rejects self-parenting and cycles silently; say so.
            SetTopologyWarning(requested == SelectedSegment
                ? "A segment cannot be its own parent."
                : $"Rejected — attaching to {requested} would create a cycle.");
            _suppressWriteBack = true;
            try { SelectedParentOption = ParentOptions.FirstOrDefault(o => o.Index == before) ?? ParentOptions[0]; }
            finally { _suppressWriteBack = false; }
        }
        else
        {
            SetTopologyWarning(null);
        }

        RebuildChain();
    }

    private void SetTopologyWarning(string? text)
    {
        TopologyWarning = text ?? string.Empty;
        HasTopologyWarning = text is not null;
    }

    partial void OnLinkXChanged(float value) => ApplyLinkLength();
    partial void OnLinkYChanged(float value) => ApplyLinkLength();
    partial void OnLinkZChanged(float value) => ApplyLinkLength();

    private void ApplyLinkLength()
    {
        if (_suppressWriteBack) return;
        _block?.SetLinkLength(SelectedSegment, LinkX / 100f, LinkY / 100f, LinkZ / 100f);
    }

    partial void OnDhRollChanged(float value) => ApplyDhAngles();
    partial void OnDhPitchChanged(float value) => ApplyDhAngles();
    partial void OnDhYawChanged(float value) => ApplyDhAngles();

    private void ApplyDhAngles()
    {
        if (_suppressWriteBack || _block is null) return;
        _block.SetDhAngles(SelectedSegment, DhRoll, DhPitch, DhYaw);

        var q = _block.GetSegment(SelectedSegment)?.DhRelativeToGyro ?? QuaternionF.Identity;
        SelectedDhQuaternionText = $"q  w {q.W:F4}   x {q.X:F4}   y {q.Y:F4}   z {q.Z:F4}";
    }

    /// <summary>Recomputes the joint angles, reach and sanity note for the selected segment.</summary>
    private void UpdateJointReadout(HashSet<int> liveSensors)
    {
        if (_block is null) return;

        var segment = _block.GetSegment(SelectedSegment);
        if (segment is null) return;

        float[] angles = segment.InverseKinematicsArm();
        JointRoll = angles[0];
        JointPitch = angles[1];
        JointYaw = angles[2];

        int parent = _block.GetParentIndex(SelectedSegment);
        if (parent >= 0)
        {
            var p = _block.GetSegment(parent);
            string name = string.IsNullOrWhiteSpace(p?.Name) ? $"segment {parent}" : p!.Name!;
            JointFrameLabel = $"relative to {parent} · {name}";
        }
        else
        {
            JointFrameLabel = "world frame (root segment)";
        }

        ReachCm = segment.Position.Magnitude() * 100f;

        var link = segment.LinkLength;
        SanityHint = link is { X: 1f, Y: 0f, Z: 0f }
            ? "Link length is still the 1 m default — set a real segment length"
            : string.Empty;

        int sensor = _block.GetSensorIndex(SelectedSegment);
        SensorIsLive = sensor >= 0 && liveSensors.Contains(sensor);
        SensorStatusText = sensor < 0
            ? "passive — no sensor"
            : SensorIsLive ? $"{SlotLabel(sensor)} · live"
            : _block.GetSensorPacketCount(sensor) > 0
                ? $"{SlotLabel(sensor)} · silent"
                : $"{SlotLabel(sensor)} · never seen";
    }

    #endregion

    #region Connect / Disconnect

    /// <summary>Connects or disconnects the serial port.</summary>
    [RelayCommand]
    private void ToggleConnection()
    {
        if (_block is null) return;

        if (_block.IsPortOpen)
        {
            _block.Disconnect();
            StatusMessage = "Disconnected";
        }
        else
        {
            _block.PortNumber = PortName;
            try
            {
                _block.Connect();
                StatusMessage = _block.IsPortOpen ? $"Connected on {PortName}" : _block.Info.Flag;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed: {ex.Message}";
            }
        }

        IsPortOpen = _block.IsPortOpen;
        OnPropertyChanged(nameof(Info));
    }

    #endregion

    #region Chain setup

    /// <summary>Rebuilds the chain to <see cref="PendingSegmentCount"/>, discarding all topology.</summary>
    [RelayCommand]
    private void ApplySegmentCount()
    {
        if (_block is null) return;

        _block.NumOfChannels = Math.Max(1, PendingSegmentCount);
        PendingSegmentCount = _block.SegmentCount;
        MaxSegment = Math.Max(0, _block.SegmentCount - 1);
        if (SelectedSegment > MaxSegment) SelectedSegment = MaxSegment;

        RebuildSensorChoices();
        RebuildChain();
        PopulateSegmentValues();
        SetTopologyWarning(null);
        StatusMessage = $"Chain rebuilt with {_block.SegmentCount} segments";
        OnPropertyChanged(nameof(SegmentCountDirty));
    }

    /// <summary>Chains every segment head to tail: 0 → 1 → 2 → …</summary>
    [RelayCommand]
    private void ChainSequentially()
    {
        if (_block is null) return;
        for (int i = 1; i < _block.SegmentCount; i++) _block.SetParent(i, i - 1);
        RebuildChain();
        PopulateSegmentValues();
        SetTopologyWarning(null);
    }

    /// <summary>Detaches every segment, leaving a flat list of roots.</summary>
    [RelayCommand]
    private void ClearParents()
    {
        if (_block is null) return;
        for (int i = 0; i < _block.SegmentCount; i++) _block.SetParent(i, -1);
        RebuildChain();
        PopulateSegmentValues();
        SetTopologyWarning(null);
    }

    #endregion

    #region Calibration

    partial void OnCalibrationDirectoryChanged(string value)
    {
        if (_suppressWriteBack || _block is null) return;
        _block.CalibrationDirectory = value;
        RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        if (_block is null) return;

        foreach (var profile in _block.Profiles) Profiles.Add(profile);
        HasProfiles = Profiles.Count > 0;

        _suppressWriteBack = true;
        try { SelectedProfile = _block.CalibrationName; }
        finally { _suppressWriteBack = false; }

        // The nearby hint reports directory state; this label shows only profile position.
        int index = SelectedProfile is null ? -1 : Profiles.IndexOf(SelectedProfile);
        ProfileCounter = HasProfiles ? $"{Math.Max(index, 0) + 1} / {Profiles.Count}" : string.Empty;
    }

    /// <summary>Loads the selected profile, or the block's current one if nothing is selected.</summary>
    [RelayCommand]
    private void LoadCalibration()
    {
        if (_block is null) return;

        var target = !string.IsNullOrWhiteSpace(SelectedProfile)
            ? SelectedProfile
            : _block.CalibrationName;

        // Saying "Calibration loaded" with nothing to load is how a rig still sitting on its
        // constructor defaults could read as restored.
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusMessage = HasProfiles ? "Select a profile to load" : "No profile to load";
            return;
        }

        _block.LoadCalibration(target!);

        RebuildChain();
        PopulateSegmentValues();
        RefreshProfiles();
        StatusMessage = $"Loaded {Path.GetFileName(target!)}";
    }

    /// <summary>
    /// Writes the current chain to a profile: the one named in <see cref="NewProfileName"/> if
    /// there is one, otherwise over <see cref="SelectedProfile"/>.
    /// </summary>
    [RelayCommand]
    private void StoreCalibration()
    {
        if (_block is null) return;

        var target = ResolveStoreTarget(out var problem);
        if (target is null)
        {
            StatusMessage = problem;
            return;
        }

        // Checked, not assumed. The block swallows its own IO errors so a failed store cannot
        // take down the pipeline, which makes its return value the only honest source for what
        // the card is about to claim.
        if (!_block.StoreCalibration(target))
        {
            StatusMessage = $"Could not write {Path.GetFileName(target)}";
            return;
        }

        // The block has already rescanned and made the new file current; RefreshProfiles picks
        // that up. Rescanning again here would snap the selection back to the first profile.
        NewProfileName = string.Empty;
        RefreshProfiles();
        StatusMessage = $"Stored {Path.GetFileName(target)}";
    }

    /// <summary>
    /// Resolves the file Store should write, or returns <see langword="null"/> and sets
    /// <paramref name="problem"/> to the reason it cannot.
    /// </summary>
    private string? ResolveStoreTarget(out string problem)
    {
        problem = string.Empty;

        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            // With no new name, overwrite the selected profile.
            if (!string.IsNullOrWhiteSpace(SelectedProfile)) return SelectedProfile;

            problem = string.IsNullOrWhiteSpace(CalibrationDirectory)
                ? "Set a profile directory first"
                : "Name the profile to store";
            return null;
        }

        if (string.IsNullOrWhiteSpace(CalibrationDirectory))
        {
            problem = "Set a profile directory first";
            return null;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problem = "Profile name has invalid characters";
            return null;
        }

        // Default extension: the profile list shows every file in the directory, and a pile of
        // extensionless names is hard to tell apart from anything else living there.
        if (!Path.HasExtension(name)) name += ".txt";

        return Path.Combine(CalibrationDirectory.Trim(), name);
    }

    #endregion
}
