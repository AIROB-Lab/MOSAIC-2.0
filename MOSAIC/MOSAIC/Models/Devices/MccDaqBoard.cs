using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using MathNet.Numerics.LinearAlgebra;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Acquires analogue input from a Measurement Computing DAQ board through the Universal Library.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock driven.</b> The board free-runs into a circular driver buffer — one <c>cbAInScan</c>
/// started with <c>Background | Continuous</c> — and each tick of the upstream <c>Clock</c> copies
/// the newest <see cref="ScansPerPacket"/> scans out of it. The board is therefore asked for
/// <c>tickRate × ScansPerPacket</c> scans per second, and the block publishes at the clock rate.
/// </para>
/// <para>
/// <b>Channels.</b> <c>cbAInScan</c> sweeps a contiguous range, so the block acquires every
/// channel from <see cref="LowChannel"/> to <see cref="HighChannel"/> inclusive. A JSON config
/// carried over from iM-Blocks may give a <c>";"</c>-separated list instead; its lowest and
/// highest entries become the range, and anything in between is acquired too.
/// </para>
/// <para>
/// <b>Output.</b> One scan per packet publishes a <see cref="Vector"/> of channels. More than one
/// publishes a <see cref="Matrix{T}"/> of rows = scans, columns = channels, the shape the other
/// multi-sample device blocks use.
/// </para>
/// <para>
/// <b>Board addressing.</b> <see cref="BoardNumber"/> is the InstaCal board number, not a serial
/// number — a board must be configured in InstaCal before the Universal Library can see it.
/// </para>
/// <para>
/// Everything that touches the vendor assembly is in the "Universal Library" region at the bottom
/// of this file, behind a single <c>ENABLE_MCCDAQ</c> guard that <c>MOSAIC.csproj</c> defines
/// wherever the MCC DAQ Software is installed. Without it the block still compiles, loads and
/// shows on the palette; it just reports the SDK as missing instead of acquiring.
/// </para>
/// </remarks>
/// <example>
/// <para>JSON pipeline configuration:</para>
/// <code language="json">
/// {
///   "Name": "Daq",
///   "Type": "MccDaq",
///   "Inputs": [ "Timer100Hz" ],
///   "Params": [ "0;1;2;3;4;5;6;7", 10, 0, "Bip10Volts", "volts" ]
/// }
/// </code>
/// </example>
public sealed partial class MccDaqBoard : BaseBlock
{
    /// <summary>Message shown when MOSAIC was built without the vendor assembly.</summary>
    private const string NotInstalled =
        "MCC Universal Library not found. Install the MCC DAQ Software (InstaCal) and rebuild.";

    /// <summary>Message shown when the driver ends the scan on its own.</summary>
    private const string ScanStoppedMessage = "Scan stopped by the driver";

    /// <summary>
    /// How many packets the driver's circular buffer holds.
    /// </summary>
    /// <remarks>
    /// The depth is the block's tolerance for a late tick — about 80 ms at the default settings.
    /// Fixed rather than configurable: there is no setting a user could pick better than this
    /// without already knowing the answer.
    /// </remarks>
    private const int BufferPackets = 8;

    /// <summary>Input ranges offered on the card. Every one is a real <c>MccDaq.Range</c> member.</summary>
    public static readonly string[] KnownRanges =
    [
        "Bip10Volts", "Bip5Volts", "Bip2Pt5Volts", "Bip2Volts", "Bip1Volts",
        "Uni10Volts", "Uni5Volts", "Uni2Volts", "Uni1Volts"
    ];

    #region Configuration

    /// <summary>InstaCal board number.</summary>
    [ObservableProperty] private int _boardNumber;

    /// <summary>First channel of the sweep.</summary>
    [ObservableProperty] private int _lowChannel;

    /// <summary>Last channel of the sweep, inclusive.</summary>
    [ObservableProperty] private int _highChannel = 7;

    /// <summary>Scans taken from the buffer on each clock tick.</summary>
    [ObservableProperty] private int _scansPerPacket = 10;

    /// <summary>Name of a <c>MccDaq.Range</c> member, e.g. <c>"Bip10Volts"</c>.</summary>
    [ObservableProperty] private string _rangeName = "Bip10Volts";

    /// <summary>
    /// Reproduce the iM-Blocks conversion — raw count ÷ 10000 — instead of converting to volts.
    /// </summary>
    /// <remarks>
    /// That conversion is not volts under any range the hardware offers: on a 16-bit board at
    /// ±10 V it spans 0 to 6.55 for an input swinging −10 to +10. It exists so a pipeline ported
    /// from iM-Blocks can still match its old recordings.
    /// </remarks>
    [ObservableProperty] private bool _useLegacyScale;

    #endregion

    #region Live State

    /// <summary>Whether a scan is running.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Connection and driver message shown separately from measured activity.</summary>
    [ObservableProperty] private string _connectionStatus = "Ready";

    /// <summary>Scans per second the board settled on, which may not be the rate requested.</summary>
    [ObservableProperty] private double _actualScanRate;

    /// <summary>
    /// Publishes the settled scan rate as this block's sample rate, to the graph and to the scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each scan yields one sample per channel, so the scan rate is the true sample rate of the
    /// signal this block emits — and it has to be the rate the board settled on rather than the one
    /// asked for, which is why this hangs off the property rather than sitting in <c>Connect</c>.
    /// </para>
    /// <para>
    /// The scope needs telling separately from <see cref="BaseBlock.SignalRate"/>: that propagates
    /// along the block graph, but nothing carries it into the local visualization. And it must be
    /// given the scan rate, not the clock rate — its x-axis advances once per row written and this
    /// block writes <see cref="ScansPerPacket"/> rows per tick. Without it the scope falls back to
    /// an assumed 200 Hz and the trace scrolls <c>ActualScanRate/200</c> times too fast.
    /// </para>
    /// </remarks>
    partial void OnActualScanRateChanged(double value)
    {
        if (value <= 0) return;

        SignalRate = value;
        Viz.UpdateSignalRate(value);
    }

    /// <summary>Packets acquired since the scan started.</summary>
    /// <remarks>
    /// Deliberately not an observable property. It advances on every tick, on the pipeline
    /// thread, and raising a change notification at the clock rate would push hundreds of binding
    /// updates a second onto the UI thread. The card samples it on its own 10 Hz refresh instead.
    /// </remarks>
    public long PacketsAcquired { get; private set; }

    #endregion

    #region Derived

    /// <summary>Channels in one scan.</summary>
    public int ChannelCount => Math.Max(1, HighChannel - LowChannel + 1);

    /// <summary>Samples in one packet.</summary>
    public int PacketSamples => ChannelCount * Math.Max(1, ScansPerPacket);

    /// <summary>Scans per second this block will ask the board for, given the clock it inherited.</summary>
    public double RequestedScanRate => (DesiredRate > 0 ? DesiredRate : InputRate) * Math.Max(1, ScansPerPacket);

    /// <summary>Live plot of the published packet, shown on the device card.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    #endregion

    #region Internal State

    // Guards the scan against Connect/Disconnect running on the UI thread while OnReceive is
    // mid-copy on the pipeline thread — which would free the driver buffer under a copy in flight.
    private readonly object _gate = new();

    private double[] _packet = [];
    private int _bufferSamples;
    private double _offset;          // counts -> engineering units: value = _offset + count * _slope
    private double _slope = 1.0;
    private object? _lastOutput;
    private bool _disposed;

    #endregion

    #region Input Constraints

    /// <inheritdoc />
    public override int MinInputs => 1;

    /// <inheritdoc />
    public override int MaxInputs => 1;

    /// <summary>
    /// The block is polled, not pushed: the clock decides when a packet leaves the driver's
    /// buffer, and it also fixes the rate the board is asked to acquire at.
    /// </summary>
    public override string[] AllowableBlocks => ["ClockBlock"];

    #endregion

    #region Construction / Factory

    /// <summary>Initialises a new <see cref="MccDaqBoard"/> block.</summary>
    /// <param name="name">Display name for this block.</param>
    /// <param name="desiredRate">Tick rate in Hz. 0 inherits the upstream clock's rate.</param>
    public MccDaqBoard(string name = "MccDaq", double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_out.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_out";

    /// <summary>
    /// Creates an <see cref="MccDaqBoard"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">The application's dependency injection service provider.</param>
    /// <param name="m">
    /// JSON model. <c>Params</c> are positional and all optional:
    /// <c>[channels, scansPerPacket, boardNumber, rangeName, scale]</c>. The first two match the
    /// iM-Blocks parameter order, so an old config loads unchanged.
    /// </param>
    public static MccDaqBoard ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "MccDaq";
        var block = ActivatorUtilities.CreateInstance<MccDaqBoard>(sp, name, m.DesiredRate ?? 0);

        var ps = m.Params;
        if (ps is { Count: > 0 }) block.SetChannels(JsonModel.GetString(ps[0], "0;1;2;3;4;5;6;7")!);
        if (ps is { Count: > 1 }) block.ScansPerPacket = Math.Max(1, JsonModel.GetInt(ps[1], 10));
        if (ps is { Count: > 2 }) block.BoardNumber    = JsonModel.GetInt(ps[2], 0);
        if (ps is { Count: > 3 }) block.RangeName      = JsonModel.GetString(ps[3], "Bip10Volts")!;
        if (ps is { Count: > 4 })
            block.UseLegacyScale = JsonModel.GetString(ps[4], "volts")!
                                            .StartsWith("legacy", StringComparison.OrdinalIgnoreCase);

        return block;
    }

    /// <summary>
    /// Sets the sweep from a channel list such as <c>"0;1;2;3"</c> or <c>"2;5"</c>.
    /// </summary>
    /// <remarks>
    /// Only the lowest and highest entries matter, because the hardware sweeps a contiguous range.
    /// The iM-Blocks driver parsed this same list and then scanned <c>0 .. count-1</c>, so the
    /// numbers in it never reached the board — <c>"2;3;5"</c> silently acquired channels 0, 1, 2.
    /// </remarks>
    public void SetChannels(string list)
    {
        var numbers = list.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => int.TryParse(t, out int c) ? c : -1)
                          .Where(c => c is >= 0 and <= 255)
                          .ToArray();

        if (numbers.Length == 0) return;

        LowChannel  = numbers.Min();
        HighChannel = numbers.Max();
    }

    #endregion

    #region Acquisition

    /// <summary>
    /// Starts a continuous background scan sized to the pipeline's clock rate.
    /// </summary>
    /// <remarks>
    /// Failures are reported through <see cref="ConnectionStatus"/> rather than thrown, so a missing board
    /// cannot tear down the pipeline that owns this block.
    /// </remarks>
    public void Connect()
    {
        lock (_gate)
        {
            if (IsConnected) return;

            // The board's rate comes from the clock, so there is nothing sensible to ask for
            // until the clock has propagated one.
            double scanRate = RequestedScanRate;
            if (scanRate <= 0)
            {
                ConnectionStatus = "No clock rate yet — load the pipeline, then connect";
                return;
            }

            _packet        = new double[PacketSamples];
            _bufferSamples = PacketSamples * BufferPackets;

            if (!StartScan(scanRate)) return;

            PacketsAcquired = 0;
            IsConnected     = true;

            ConnectionStatus = Math.Abs(ActualScanRate - scanRate) <= 0.01 * scanRate
                ? "Scanning"
                : $"Scanning at {ActualScanRate:F0} scans/s, not the {scanRate:F0} requested";

            Console.WriteLine($"[{Name}] Board {BoardNumber} ch {LowChannel}-{HighChannel} " +
                              $"@ {ActualScanRate:F0} scans/s, {ScansPerPacket} scans/tick, {RangeName}");
        }
    }

    /// <summary>Stops the scan and releases the driver buffer.</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (!IsConnected) return;

            StopScan();
            IsConnected = false;
            ConnectionStatus = "Stopped";

            Console.WriteLine($"[{Name}] Stopped.");
        }
    }

    /// <summary>Stops and restarts the scan, picking up changed configuration.</summary>
    public void Reconnect()
    {
        Disconnect();
        Connect();
    }

    /// <summary>
    /// Copies the newest packet out of the driver buffer on each clock tick and publishes it.
    /// </summary>
    /// <param name="sender">The upstream clock.</param>
    /// <param name="value">The tick timestamp; unused.</param>
    protected override void OnReceive(object sender, object value)
    {
        object? output = null;

        lock (_gate)
        {
            if (IsConnected && ReadNewestPacket())
            {
                output = BuildOutput(_packet);
                _lastOutput = output;
                PacketsAcquired++;
            }
        }

        // Published unconditionally: a tick that finds no complete packet republishes the previous
        // one, so the downstream rate stays at the clock rate instead of stuttering.
        output ??= _lastOutput;
        if (output is null) return;

        switch (output)
        {
            case Matrix<double> matrix: Viz.Feed(matrix); break;
            case Vector vector:         Viz.Feed(vector); break;
        }

        Publish(output);
    }

    /// <summary>
    /// Turns a packet of raw counts into the shape this block publishes.
    /// </summary>
    /// <param name="rawCounts">
    /// Interleaved raw counts, <c>ScansPerPacket × ChannelCount</c> of them.
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>cbAInScan</c> interleaves by channel — one scan is every channel in order, and scans
    /// follow end to end — so a packet is already a row-major scans × channels matrix. The
    /// iM-Blocks driver flattened the whole thing into one long vector instead, so a ten-scan
    /// packet over eight channels reached downstream as an eighty-channel signal that changed
    /// identity every eight elements.
    /// </para>
    /// <para>
    /// Public, and taking the counts as a parameter, so the interleave and the scale can be
    /// checked without a board attached — the same reason <see cref="DlrAdcBt.Ingest"/> is public.
    /// </para>
    /// </remarks>
    public object BuildOutput(ReadOnlySpan<double> rawCounts)
    {
        int channels = ChannelCount;

        if (ScansPerPacket == 1)
        {
            var vector = Vector.Build.Dense(channels);
            for (int c = 0; c < channels; c++) vector[c] = _offset + rawCounts[c] * _slope;
            return vector;
        }

        var matrix = Matrix<double>.Build.Dense(ScansPerPacket, channels);
        for (int scan = 0, k = 0; scan < ScansPerPacket; scan++)
            for (int c = 0; c < channels; c++, k++)
                matrix[scan, c] = _offset + rawCounts[k] * _slope;

        return matrix;
    }

    /// <summary>
    /// Works out where the newest whole packet sits in the driver's circular buffer.
    /// </summary>
    /// <param name="samplesWritten">The driver's running total of samples written.</param>
    /// <param name="channelCount">Channels in one scan.</param>
    /// <param name="packetSamples">Samples in one packet.</param>
    /// <param name="bufferSamples">Capacity of the circular buffer, in samples.</param>
    /// <param name="start">Sample offset the packet begins at.</param>
    /// <param name="firstRun">
    /// Samples available before the end of the buffer. Less than <paramref name="packetSamples"/>
    /// means the packet straddles the wrap point and the remainder continues at offset 0.
    /// </param>
    /// <returns><see langword="false"/> when a whole packet has not been written yet.</returns>
    /// <remarks>
    /// Pure arithmetic, deliberately outside the vendor guard: this is the part a wrong offset
    /// corrupts silently. A cursor off by one sample rotates every channel in the output by one
    /// position — a fault that looks like miswired hardware rather than like a software bug — so
    /// it needs to be checkable on a machine with no board and no SDK.
    /// </remarks>
    public static bool TryLocatePacket(long samplesWritten, int channelCount, int packetSamples,
                                       int bufferSamples, out int start, out int firstRun)
    {
        start = 0;
        firstRun = 0;

        if (channelCount <= 0 || packetSamples <= 0 || bufferSamples < packetSamples) return false;
        if (samplesWritten < packetSamples) return false;

        // Align down to a scan boundary. The driver's count can sit mid-scan, and taking it at
        // face value would rotate every channel in every packet from here on.
        long end = samplesWritten - samplesWritten % channelCount;

        start    = (int)((end - packetSamples) % bufferSamples);
        firstRun = Math.Min(packetSamples, bufferSamples - start);
        return true;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "MccDaq";

    /// <inheritdoc />
    /// <remarks>
    /// The channel list is written back normalised to its endpoints, so a config saying
    /// <c>"0;2;5"</c> is re-saved as <c>"0;5"</c>. Nothing is lost — the hardware sweeps a
    /// contiguous range, so both spell the same acquisition — but the file does change, and it is
    /// better that the saved config says plainly what the board will do.
    /// </remarks>
    protected override IReadOnlyList<object>? GetJsonParams() =>
    [
        $"{LowChannel};{HighChannel}", ScansPerPacket, BoardNumber, RangeName,
        UseLegacyScale ? "legacy" : "volts"
    ];

    #endregion

    #region Dispose

    /// <summary>Stops the scan, frees the driver buffer and releases the visualization.</summary>
    public override void Dispose()
    {
        if (_disposed) { base.Dispose(); return; }
        _disposed = true;

        Disconnect();

        try { Viz.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[{Name}] Viz dispose failed: {ex.Message}"); }

        base.Dispose();
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────────────────────
    #region Universal Library
    //
    // The only code in MOSAIC that touches MccDaq.dll. One guard, one region: the vendor
    // assembly ships with the MCC DAQ Software installer and is not on NuGet, so it is absent
    // from any machine without a board — CI included. Keeping the calls together means the block
    // above stays readable and the rest of the app never has to be conditioned in step.
    //
    // Buffer width follows the board's resolution: 16 bits or fewer is a 16-bit UL buffer, more
    // is a 32-bit one. The iM-Blocks driver allocated a 32-bit buffer and then read 16-bit values
    // out of it, so it consumed the low half and read every other half-word as a sample.
    // ─────────────────────────────────────────────────────────────────────────────────────────

#if ENABLE_MCCDAQ

    private MccDaq.MccBoard? _board;
    private IntPtr _buffer = IntPtr.Zero;
    private ushort[] _narrow = [];      // 16-bit boards
    private uint[]   _wide   = [];      // > 16-bit boards
    private bool _isWide;

    private static bool _nativeSearchPathPrepared;

    /// <summary>
    /// Where the MCC DAQ Software puts the native Universal Library.
    /// </summary>
    private static readonly string[] NativeSearchDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Measurement Computing", "DAQ"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Measurement Computing", "DAQ"),
    ];

    /// <summary>
    /// Loads the native Universal Library before the first call into the wrapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MccDaq.dll</c> is a managed shim over <c>cbw64.dll</c>, and it does not reach it through
    /// <c>DllImport</c> — the only native import it declares is <c>kernel32</c>, so it loads the
    /// library itself, by bare name, at first use. Nothing about that consults the assembly's own
    /// folder; it depends entirely on the process's library search path.
    /// </para>
    /// <para>
    /// The installer puts <c>cbw64.dll</c> in its own program folder and adds that folder to the
    /// <em>machine</em> PATH. So a process started before the SDK was installed — an IDE, a
    /// terminal open since this morning — inherits a PATH without it and cannot load the driver,
    /// while a freshly started one can. The board then appears to work or not depending on how
    /// the app happened to be launched, and the failure surfaces as a bare
    /// <see cref="NullReferenceException"/> from inside the wrapper rather than as anything that
    /// names a missing DLL.
    /// </para>
    /// <para>
    /// Setting PATH at runtime does <em>not</em> fix it — measured, not assumed. Loading the file
    /// by absolute path does: the module is then in the process, and the wrapper's later load by
    /// bare name resolves to it. The PATH entry is added as well, for the siblings the UL pulls in
    /// beside it (<c>DaqLib64.dll</c> and friends). Both touch this process only.
    /// </para>
    /// </remarks>
    private static void PrepareNativeLibrary()
    {
        if (_nativeSearchPathPrepared) return;
        _nativeSearchPathPrepared = true;

        // Bitness must match the host process; the folder holds both, and loading the wrong one
        // fails with BadImageFormatException.
        string file = Environment.Is64BitProcess ? "cbw64.dll" : "cbw32.dll";

        foreach (var dir in NativeSearchDirs)
        {
            var full = Path.Combine(dir, file);
            if (!File.Exists(full)) continue;
            if (!NativeLibrary.TryLoad(full, out _)) continue;

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            bool already = path.Split(Path.PathSeparator)
                               .Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), dir,
                                                       StringComparison.OrdinalIgnoreCase));

            if (!already)
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + path);

            return;
        }
    }

    /// <summary>Boards InstaCal has configured, for the card's picker.</summary>
    public static string[] AvailableBoards()
    {
        var found = new List<string>();
        try
        {
            PrepareNativeLibrary();

            for (int number = 0; number < 16; number++)
            {
                string name = new MccDaq.MccBoard(number).BoardName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) &&
                    !name.StartsWith("Board #", StringComparison.OrdinalIgnoreCase))
                    found.Add($"{number}: {name}");
            }
        }
        catch (Exception) { /* No usable driver; an empty picker says so well enough. */ }

        return [.. found];
    }

    /// <summary>Allocates the buffer, derives the scale and starts the scan.</summary>
    /// <returns><see langword="false"/> when the driver refused; <see cref="ConnectionStatus"/> says why.</returns>
    private bool StartScan(double scanRate)
    {
        if (!Enum.TryParse<MccDaq.Range>(RangeName, ignoreCase: true, out var range))
        {
            ConnectionStatus = $"'{RangeName}' is not a Universal Library range";
            return false;
        }

        try
        {
            PrepareNativeLibrary();

            // By default the UL reports errors with a modal message box and can terminate the
            // process — behaviour meant for its single-purpose sample programs. Every error has
            // to come back as a return code this block can put on the card instead.
            MccDaq.MccService.ErrHandling(MccDaq.ErrorReporting.DontPrint, MccDaq.ErrorHandling.DontStop);

            _board = new MccDaq.MccBoard(BoardNumber);

            int resolution = 16;
            if (_board.BoardConfig.GetAdResolution(out int bits).Value == MccDaq.ErrorInfo.ErrorCode.NoErrors && bits > 0)
                resolution = bits;

            _isWide = resolution > 16;
            _narrow = _isWide ? [] : new ushort[PacketSamples];
            _wide   = _isWide ? new uint[PacketSamples] : [];

            _buffer = _isWide
                ? MccDaq.MccService.WinBufAlloc32Ex(_bufferSamples)
                : MccDaq.MccService.WinBufAllocEx(_bufferSamples);

            if (_buffer == IntPtr.Zero)
            {
                ConnectionStatus = $"Could not allocate a {_bufferSamples}-sample buffer";
                return false;
            }

            DeriveScale(range, resolution);

            // cbAInScan takes the rate in SCANS per second — one scan covers every channel — and
            // writes back what the board's clock divider could actually produce. The iM-Blocks
            // driver passed tickRate * packetSize * channelCount, multiplying by the channel
            // count a second time, so the board was asked for N times the samples consumed.
            int rate = Math.Max(1, (int)Math.Round(scanRate));

            var error = _board.AInScan(LowChannel, HighChannel, _bufferSamples, ref rate, range, _buffer,
                                       MccDaq.ScanOptions.Background | MccDaq.ScanOptions.Continuous);

            if (error.Value != MccDaq.ErrorInfo.ErrorCode.NoErrors)
            {
                ConnectionStatus = $"AInScan failed: {error.Message}";
                FreeBuffer();
                return false;
            }

            ActualScanRate = rate;
            return true;
        }
        catch (Exception ex)
        {
            // Compiling against the managed wrapper does not mean the native driver is there.
            // The common causes are worth naming: cbw64.dll absent (the MCC DAQ Software was
            // never installed, only the device driver), or a bitness mismatch.
            // A missing cbw64.dll does not surface as DllNotFoundException - the wrapper
            // swallows the failed load and then dereferences null - so the NRE is named too.
            ConnectionStatus = ex is DllNotFoundException or NullReferenceException
                ? "cbw64.dll could not be loaded. Install the full MCC DAQ Software (not just the " +
                  "device driver); it provides the native Universal Library this needs."
                : $"Universal Library unavailable: {ex.Message}";
            FreeBuffer();
            return false;
        }
    }

    /// <summary>
    /// Derives <see cref="_offset"/> and <see cref="_slope"/> by asking the driver to convert two
    /// known counts.
    /// </summary>
    /// <remarks>
    /// Reading the mapping off the driver rather than a hard-coded range table keeps every range
    /// and resolution correct without this code enumerating them, and costs two calls per scan
    /// rather than one per sample.
    /// </remarks>
    private void DeriveScale(MccDaq.Range range, int resolution)
    {
        if (UseLegacyScale) { _offset = 0.0; _slope = 1.0 / 10000.0; return; }

        _offset = 0.0;
        _slope  = 1.0;                      // raw counts, if the driver will not say otherwise
        if (_board is null) return;

        int fullScale = (1 << Math.Clamp(resolution, 1, 31)) - 1;

        double low, high;
        MccDaq.ErrorInfo e1, e2;

        if (_isWide)
        {
            // ToEngUnits32 takes no resolution argument and answers in double.
            e1 = _board.ToEngUnits32(range, 0, out low);
            e2 = _board.ToEngUnits32(range, fullScale, out high);
        }
        else
        {
            // The 16-bit overload answers in float, and comes in both ushort and short flavours,
            // so the count must be cast — a bare 0 is ambiguous between them.
            e1 = _board.ToEngUnits(range, (ushort)0, out float lowVolts);
            e2 = _board.ToEngUnits(range, (ushort)fullScale, out float highVolts);
            low  = lowVolts;
            high = highVolts;
        }

        if (e1.Value != MccDaq.ErrorInfo.ErrorCode.NoErrors ||
            e2.Value != MccDaq.ErrorInfo.ErrorCode.NoErrors) return;

        _offset = low;
        _slope  = (high - low) / fullScale;
    }

    /// <summary>
    /// Copies the newest whole packet out of the circular buffer into <see cref="_packet"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the freshest data rather than tracking a read cursor. That is the same thing the
    /// iM-Blocks driver meant to do, minus its two faults: it copied the buffer <em>before</em>
    /// ever starting a scan, so the first packet was the uninitialised allocation, and it then
    /// restarted a finite scan on every tick and read while one was in flight.
    /// </para>
    /// <para>
    /// The consequence of taking the newest is that consecutive packets overlap or skip when the
    /// clock and the scan rate disagree. With the rate derived from the clock they agree, and if
    /// they drift the card shows it: the board's settled rate is on display next to the clock's.
    /// </para>
    /// </remarks>
    private bool ReadNewestPacket()
    {
        if (_board is null || _buffer == IntPtr.Zero) return false;

        var status = _board.GetStatus(out short running, out int curCount, out int _,
                                      MccDaq.FunctionType.AiFunction);

        if (status.Value != MccDaq.ErrorInfo.ErrorCode.NoErrors) return false;

        if (running == 0)
        {
            // Set once, not on every tick: this is the pipeline thread, and a property change
            // raised at the clock rate would be a binding update per tick for a state that is
            // not going to change back on its own.
            if (ConnectionStatus != ScanStoppedMessage) ConnectionStatus = ScanStoppedMessage;
            return false;
        }

        int packet = _packet.Length;
        if (!TryLocatePacket(curCount, ChannelCount, packet, _bufferSamples, out int start, out int first))
            return false;

        // A packet straddling the end of the buffer comes back as two runs.
        return CopyRun(start, first, 0) && (first == packet || CopyRun(0, packet - first, first));
    }

    /// <summary>Copies one contiguous run of samples out of the driver buffer.</summary>
    private bool CopyRun(int firstSample, int count, int destIndex)
    {
        if (count <= 0) return true;

        if (_isWide)
        {
            var error = MccDaq.MccService.WinBufToArray32(_buffer, _wide, firstSample, count);
            if (error.Value != MccDaq.ErrorInfo.ErrorCode.NoErrors) return false;

            for (int i = 0; i < count; i++) _packet[destIndex + i] = _wide[i];
        }
        else
        {
            // The unsigned overload, deliberately: analogue input counts are unsigned, and the
            // signed one would fold the upper half of every range onto negative volts.
            var error = MccDaq.MccService.WinBufToArray(_buffer, _narrow, firstSample, count);
            if (error.Value != MccDaq.ErrorInfo.ErrorCode.NoErrors) return false;

            for (int i = 0; i < count; i++) _packet[destIndex + i] = _narrow[i];
        }

        return true;
    }

    /// <summary>Stops the scan and frees the driver buffer.</summary>
    private void StopScan()
    {
        try { _board?.StopBackground(MccDaq.FunctionType.AiFunction); }
        catch (Exception) { /* Stopping a board that has already gone away is not a failure. */ }

        FreeBuffer();
        _board = null;
    }

    /// <summary>Releases the driver buffer. The UL allocated it, so the UL must free it.</summary>
    private void FreeBuffer()
    {
        if (_buffer == IntPtr.Zero) return;

        try { MccDaq.MccService.WinBufFreeEx(_buffer); }
        catch (Exception) { /* Nothing useful remains to be done about a failed free. */ }

        _buffer = IntPtr.Zero;
    }

#else

    /// <summary>No boards are visible without the vendor assembly.</summary>
    public static string[] AvailableBoards() => [];

    private bool StartScan(double scanRate)
    {
        ConnectionStatus = NotInstalled;
        return false;
    }

    private bool ReadNewestPacket() => false;

    private void StopScan() { }

#endif

    #endregion
}
