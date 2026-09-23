using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Synthetic ultrasound frame source for testing the
/// <see cref="MOSAIC.Models.MachineLearning.UltrasoundClassifier"/> pipeline
/// without real hardware connected. Generates class-conditional grayscale
/// patterns so an actual model can learn to distinguish them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> On each clock tick, the source emits one <c>Matrix&lt;double&gt;</c>
/// of shape <c>FrameHeight × FrameWidth</c> with pixel values in [0, 1]. The
/// content depends on <see cref="CurrentClass"/>, an integer in
/// <c>[0, NumClasses)</c> that you can change at runtime (e.g. via a
/// <see cref="MOSAIC.Models.FlowControl.Trigger"/> block driving the same value).
/// Each class corresponds to a deterministic spatial pattern — vertical gradient,
/// horizontal gradient, central blob, diagonal stripes, checkerboard — plus a
/// small amount of Gaussian noise so the frames aren't pixel-identical between
/// ticks.
/// </para>
/// <para>
/// <b>Source block:</b> Driven by an upstream <see cref="MOSAIC.Models.FlowControl.ClockBlock"/>
/// (the <c>tNow</c> double timestamp is used to seed the noise).
/// </para>
/// <para>
/// <b>Typical use:</b> place a <c>ClockBlock</c> at 30 Hz, this source after it,
/// the classifier downstream, and a <c>TriggerBuffer</c> alongside collecting
/// labelled segments — the same way you'd wire real ultrasound data. Cycle
/// <see cref="CurrentClass"/> through values 0..NumClasses-1 to record fake
/// training data; train the network; verify the predictor learns the patterns.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MockUltrasound": {
///     "Type": "MockUltrasoundSource",
///     "Inputs": [ "Clock" ],
///     "DesiredRate": 30,
///     "Params": [ 5, 224, 224, 0.05 ]
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>NumClasses</c> (int, default 5).</description></item>
///   <item><term>1</term><description><c>FrameHeight</c> (int, default 224) — pixel rows.</description></item>
///   <item><term>2</term><description><c>FrameWidth</c> (int, default 224) — pixel cols.</description></item>
///   <item><term>3</term><description><c>NoiseLevel</c> (double, default 0.05) — std-dev of additive Gaussian noise.</description></item>
/// </list>
/// </para>
/// </example>
public partial class MockUltrasoundSource : BaseBlock
{
    /// <summary>Visualization scope for the generated frames.</summary>
    public BlockVisualization Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Number of distinguishable synthetic classes.</summary>
    [ObservableProperty] private int _numClasses;

    /// <summary>Generated frame height in pixels.</summary>
    [ObservableProperty] private int _frameHeight;

    /// <summary>Generated frame width in pixels.</summary>
    [ObservableProperty] private int _frameWidth;

    /// <summary>Std-dev of additive Gaussian noise applied to each frame.</summary>
    [ObservableProperty] private double _noiseLevel;

    /// <summary>
    /// Index of the synthetic class to render this tick. Settable at runtime —
    /// drive this from a Trigger or UI slider to switch the "gesture" being
    /// produced. Clamped to <c>[0, NumClasses)</c>.
    /// </summary>
    [ObservableProperty] private int _currentClass;

    /// <summary>Total frames emitted so far (informational, for the card UI).</summary>
    [ObservableProperty] private long _frameCount;

    /// <summary>
    /// When <c>true</c>, frame generation blends all pattern templates weighted
    /// by the most recently received upstream activation vector
    /// (<see cref="_latestWeights"/>) instead of selecting a single template by
    /// <see cref="CurrentClass"/>. Useful for testing regression pipelines where
    /// the image must be a continuous function of the regression target — e.g.
    /// driven by a <c>SinGenerator</c> producing per-channel activations.
    /// </summary>
    /// <remarks>
    /// In weighted mode the source needs <b>two</b> inputs: a clock (for timing)
    /// and a Vector source (for weights). Wire the activation source as a second
    /// input alongside the clock; the block distinguishes them by message type
    /// (Vector → weight update; anything else → clock tick → emit frame).
    /// </remarks>
    [ObservableProperty] private bool _weightedMode;

    /// <summary>
    /// In weighted mode, selects which input drives the per-pattern blend:
    /// upstream Vector messages (training), manual slider values (precise testing),
    /// or an internal sine-bank generator (hands-free demo).
    /// </summary>
    [ObservableProperty] private WeightSource _weightSource = WeightSource.Upstream;

    /// <summary>
    /// Frequency for the internal sweep generator (<see cref="WeightSource.Sweep"/>).
    /// Each channel uses <c>sin²(2π·f·t + i·2π/N)</c>, so the channels phase-shift
    /// around the circle and the full pattern set is exercised across one period.
    /// </summary>
    [ObservableProperty] private double _sweepFrequencyHz = 0.3;

    /// <summary>
    /// Per-channel manual weight items, bound to sliders in the card view when
    /// <see cref="WeightSource"/> is <see cref="WeightSource.Manual"/>. Resized
    /// automatically when <see cref="NumClasses"/> changes.
    /// </summary>
    public ObservableCollection<ManualChannelItem> ManualChannels { get; } = new();

    /// <summary>
    /// Cached activation vector from the upstream weight source. Held across
    /// ticks so that frame emission can blend even when the weight source runs
    /// at a different rate than the clock (hold-last behaviour).
    /// </summary>
    private MathNet.Numerics.LinearAlgebra.Vector<double>? _latestWeights;

    /// <summary>Wall-clock anchor for the internal sweep generator.</summary>
    private readonly long _sweepStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>
    /// Per-class base pattern, precomputed once at construction. Each row of
    /// this matrix is the row-major pixel buffer for one class's template;
    /// noise is added per-frame on top of the chosen template.
    /// </summary>
    private double[][] _patterns = Array.Empty<double[]>();

    /// <summary>PRNG for the per-frame noise.</summary>
    private readonly Random _rng = new();

    public MockUltrasoundSource(
        string name        = "MockUltrasound",
        double desiredRate = 30.0,
        int    numClasses  = 5,
        int    frameHeight = 224,
        int    frameWidth  = 224,
        double noiseLevel  = 0.05)
        : base(name, desiredRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numClasses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);

        _numClasses  = numClasses;
        _frameHeight = frameHeight;
        _frameWidth  = frameWidth;
        _noiseLevel  = Math.Max(0.0, noiseLevel);
        _currentClass = 0;

        RebuildPatterns();
        RebuildManualChannels();
    }

    // ───────────────────────── property reactions ────────────────────────

    partial void OnNumClassesChanged(int value)
    {
        if (value <= 0) return;
        if (_currentClass >= value) _currentClass = 0;
        RebuildPatterns();
        RebuildManualChannels();
    }

    partial void OnFrameHeightChanged(int value)
    {
        if (value <= 0) return;
        RebuildPatterns();
    }

    partial void OnFrameWidthChanged(int value)
    {
        if (value <= 0) return;
        RebuildPatterns();
    }

    partial void OnCurrentClassChanged(int value)
    {
        if (NumClasses <= 0) return;
        if (value < 0)            _currentClass = 0;
        else if (value >= NumClasses) _currentClass = NumClasses - 1;
    }

    // ───────────────────────── pattern bank ──────────────────────────────

    /// <summary>
    /// Precompute one grayscale template per class. Six patterns are defined;
    /// if <see cref="NumClasses"/> exceeds six, additional classes are
    /// generated by varying the diagonal stripe period — they remain
    /// deterministic and distinguishable.
    /// </summary>
    private void RebuildPatterns()
    {
        int n = NumClasses, h = FrameHeight, w = FrameWidth;
        if (n <= 0 || h <= 0 || w <= 0)
        {
            _patterns = Array.Empty<double[]>();
            return;
        }

        _patterns = new double[n][];
        for (int c = 0; c < n; c++)
        {
            var buf = new double[h * w];
            FillPattern(buf, c, h, w);
            _patterns[c] = buf;
        }
    }

    /// <summary>
    /// Resize <see cref="ManualChannels"/> to match <see cref="NumClasses"/>,
    /// preserving the current values where indices overlap. New entries are
    /// added at zero so a "Manual" override doesn't suddenly flash a
    /// channel to active when NumClasses grows.
    /// </summary>
    private void RebuildManualChannels()
    {
        int n = NumClasses;
        while (ManualChannels.Count < n)
        {
            var i = ManualChannels.Count;
            ManualChannels.Add(new ManualChannelItem
            {
                Index = i,
                Label = $"Ch{i}",
                Value = 0.0
            });
        }
        while (ManualChannels.Count > n)
            ManualChannels.RemoveAt(ManualChannels.Count - 1);
    }

    /// <summary>
    /// Compute the synthetic per-channel activation vector for
    /// <see cref="WeightSource.Sweep"/> mode. Each channel uses
    /// <c>sin²(2π·f·t + i·2π/N)</c> so the bank sweeps with a complete phase
    /// rotation across the channels — every two-finger and rest-only combination
    /// appears at some point in the cycle.
    /// </summary>
    private Vector<double> ComputeSweepWeights()
    {
        int n = _patterns.Length;
        double t = (System.Diagnostics.Stopwatch.GetTimestamp() - _sweepStartTicks)
                 / (double)System.Diagnostics.Stopwatch.Frequency;
        var v = Vector<double>.Build.Dense(n);
        if (n == 0) return v;
        for (int i = 0; i < n; i++)
        {
            double phase = 2.0 * Math.PI * SweepFrequencyHz * t
                         + i * (2.0 * Math.PI / n);
            double s = Math.Sin(phase);
            v[i] = s * s; // sin² ∈ [0, 1]
        }
        return v;
    }

    /// <summary>
    /// Fill <paramref name="buf"/> (row-major, <c>h × w</c>) with the deterministic
    /// pattern for class <paramref name="cls"/>. Values are normalised to [0, 1].
    /// </summary>
    private static void FillPattern(double[] buf, int cls, int h, int w)
    {
        // Patterns chosen to be visually & spatially distinct so a real ResNet
        // can pick them up after a handful of epochs even with small data.
        switch (cls % 6)
        {
            case 0: // vertical gradient (dark top → bright bottom)
                for (int y = 0; y < h; y++)
                {
                    double v = (double)y / (h - 1);
                    for (int x = 0; x < w; x++) buf[y * w + x] = v;
                }
                break;

            case 1: // horizontal gradient (dark left → bright right)
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    buf[y * w + x] = (double)x / (w - 1);
                break;

            case 2: // central Gaussian blob
                {
                    double cy = h / 2.0, cx = w / 2.0;
                    double sigma = Math.Min(h, w) / 6.0;
                    double twoSigSq = 2.0 * sigma * sigma;
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double dy = y - cy, dx = x - cx;
                        buf[y * w + x] = Math.Exp(-(dy * dy + dx * dx) / twoSigSq);
                    }
                    break;
                }

            case 3: // diagonal stripes (period scales with cls so extra classes differ)
                {
                    double period = 16.0 + (cls / 6) * 4.0; // 16, 20, 24, …
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double phase = (x + y) / period * 2.0 * Math.PI;
                        buf[y * w + x] = 0.5 + 0.5 * Math.Sin(phase);
                    }
                    break;
                }

            case 4: // checkerboard (period scales similarly)
                {
                    int period = Math.Max(4, 16 - (cls / 6) * 2);
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        buf[y * w + x] = ((x / period + y / period) % 2 == 0) ? 0.85 : 0.15;
                    break;
                }

            default: // ring-shaped pattern
                {
                    double cy = h / 2.0, cx = w / 2.0;
                    double rTarget = Math.Min(h, w) / 4.0;
                    double sigma   = rTarget / 3.0;
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double dy = y - cy, dx = x - cx;
                        double r = Math.Sqrt(dy * dy + dx * dx);
                        double d = (r - rTarget) / sigma;
                        buf[y * w + x] = Math.Exp(-0.5 * d * d);
                    }
                    break;
                }
        }
    }

    // ───────────────────────── JSON factory ──────────────────────────────

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_frames.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_frames";

    public static MockUltrasoundSource ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var p = m.Params ?? new List<object>(0);

        var numClasses  = p.Count > 0 ? Math.Max(1, GetInt(p[0], 5)) : 5;
        var frameHeight = p.Count > 1 ? Math.Max(1, GetInt(p[1], 224)) : 224;
        var frameWidth  = p.Count > 2 ? Math.Max(1, GetInt(p[2], 224)) : 224;
        var noiseLevel  = p.Count > 3 ? GetDouble(p[3], 0.05) : 0.05;

        // Optional 5th param: 'weighted' (string), 'true' (bool/string), or 1
        // enables weighted-blend mode at construction time. Defaults to label mode.
        bool weightedMode = false;
        if (p.Count > 4)
        {
            var rawStr = p[4]?.ToString()?.Trim() ?? "";
            weightedMode = rawStr.Equals("weighted",      StringComparison.OrdinalIgnoreCase)
                        || rawStr.Equals("weighted_mode", StringComparison.OrdinalIgnoreCase)
                        || rawStr.Equals("blend",         StringComparison.OrdinalIgnoreCase)
                        || rawStr.Equals("true",          StringComparison.OrdinalIgnoreCase)
                        || rawStr.Equals("1",             StringComparison.Ordinal)
                        || (p[4] is bool b && b);
        }

        var name = m.Name ?? "MockUltrasound";
        var rate = m.DesiredRate ?? 30.0;

        var block = ActivatorUtilities.CreateInstance<MockUltrasoundSource>(
            sp, name, rate, numClasses, frameHeight, frameWidth, noiseLevel);
        block.WeightedMode = weightedMode;
        return block;
    }

    #region JSON Export

    protected override string JsonTypeName => "MockUltrasound";

    protected override IReadOnlyList<object>? GetJsonParams()
        => WeightedMode
           ? [NumClasses, FrameHeight, FrameWidth, NoiseLevel, "weighted"]
           : [NumClasses, FrameHeight, FrameWidth, NoiseLevel];

    #endregion

    // ───────────────────────── tick handler ──────────────────────────────

    /// <summary>
    /// Routes incoming messages:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>Vector&lt;double&gt;</c> → cache as the latest activation weights for
    ///     weighted mode. Does <i>not</i> trigger a frame emission — the clock
    ///     drives that. Useful when the weight source's rate doesn't match the
    ///     clock's; the cache acts as hold-last interpolation.
    ///   </description></item>
    ///   <item><description>
    ///     Anything else (typically a clock tick — <c>double</c> timestamp) →
    ///     emit one frame using the current mode's blending rule.
    ///   </description></item>
    /// </list>
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        // Weight update — cache and return without emitting a frame.
        if (data is MathNet.Numerics.LinearAlgebra.Vector<double> weights)
        {
            _latestWeights = weights;
            return;
        }

        if (_patterns.Length == 0) return;
        int h = _frameHeight, w = _frameWidth;
        if (_patterns[0].Length != h * w) return; // size mismatch — wait for RebuildPatterns

        // Build the per-pixel template for this frame, then add noise on top.
        var template = BuildTemplate(h, w);
        if (template is null) return;

        var pixels = new double[h * w];
        if (_noiseLevel > 0.0)
        {
            // Box-Muller for the noise; lighter than calling a normal-distribution
            // class per pixel and stays deterministic given the seed.
            for (int i = 0; i < pixels.Length; i++)
            {
                double u1 = 1.0 - _rng.NextDouble();
                double u2 = 1.0 - _rng.NextDouble();
                double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                pixels[i] = Math.Clamp(template[i] + _noiseLevel * z, 0.0, 1.0);
            }
        }
        else
        {
            Array.Copy(template, pixels, pixels.Length);
        }

        var frame = Matrix.Build.DenseOfRowMajor(h, w, pixels);
        Publish(frame);
        Viz?.Feed(frame);
        FrameCount++;
    }

    /// <summary>
    /// Pick or build the base template for this frame, before noise. Label mode
    /// returns the single template indexed by <see cref="CurrentClass"/>;
    /// weighted mode picks a weight source — upstream Vector, manual sliders,
    /// or internal sweep — and blends the per-class templates by those weights.
    /// Weights are clamped to <c>[0, 1]</c> so the composite stays in pixel range.
    /// </summary>
    private double[]? BuildTemplate(int h, int w)
    {
        if (!WeightedMode)
        {
            int cls = Math.Clamp(_currentClass, 0, _patterns.Length - 1);
            return _patterns[cls];
        }

        // Pick weights according to the active source. Manual and Sweep ignore
        // upstream Vector input — useful after training when the user wants to
        // test the model without an active recording driving the input.
        Vector<double>? w_vec = WeightSource switch
        {
            WeightSource.Manual => ManualWeightsAsVector(),
            WeightSource.Sweep  => ComputeSweepWeights(),
            _                   => _latestWeights, // Upstream (default)
        };

        // No weights available (Upstream mode before any vector arrives) → black
        // frame. Keeps the test honest — better to see nothing than to
        // silently pick an arbitrary class.
        if (w_vec is null) return new double[h * w];

        var buf = new double[h * w];
        int patternCount = Math.Min(_patterns.Length, w_vec.Count);
        for (int c = 0; c < patternCount; c++)
        {
            double weight = Math.Clamp(w_vec[c], 0.0, 1.0);
            if (weight == 0.0) continue;
            var pattern = _patterns[c];
            for (int i = 0; i < buf.Length; i++)
                buf[i] += weight * pattern[i];
        }
        for (int i = 0; i < buf.Length; i++)
            if (buf[i] > 1.0) buf[i] = 1.0;
        return buf;
    }

    /// <summary>Snapshot <see cref="ManualChannels"/> values as a Vector.</summary>
    private Vector<double> ManualWeightsAsVector()
    {
        int n = ManualChannels.Count;
        var v = Vector<double>.Build.Dense(n);
        for (int i = 0; i < n; i++) v[i] = ManualChannels[i].Value;
        return v;
    }

    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }
}

/// <summary>
/// Source of per-channel weights when <see cref="MockUltrasoundSource.WeightedMode"/>
/// is on. Lets the same block drive recording (Upstream) and post-training
/// testing (Manual / Sweep) without rewiring the pipeline.
/// </summary>
public enum WeightSource
{
    /// <summary>Use the most recent <see cref="MathNet.Numerics.LinearAlgebra.Vector{double}"/>
    /// from an upstream block (e.g. <c>Trigger</c> in oscillation mode, <c>SinGenerator</c>,
    /// or a future dataglove block). Default — what training uses.</summary>
    Upstream,

    /// <summary>Use values from <see cref="MockUltrasoundSource.ManualChannels"/>;
    /// the card view binds per-channel sliders to those values for precise
    /// testing of specific activation patterns ("does the model recognise
    /// PINCH = THUMB + INDEX?").</summary>
    Manual,

    /// <summary>Use an internal sin²-bank generator at
    /// <see cref="MockUltrasoundSource.SweepFrequencyHz"/>. Hands-free
    /// demonstration — the bank rotates through every combination and the
    /// model's predictions should track.</summary>
    Sweep
}

/// <summary>
/// One row in <see cref="MockUltrasoundSource.ManualChannels"/>. Bound to a slider
/// in the card view; the block reads <see cref="Value"/> on every frame when
/// <see cref="MockUltrasoundSource.WeightSource"/> is <see cref="WeightSource.Manual"/>.
/// </summary>
public partial class ManualChannelItem : ObservableObject
{
    public int    Index { get; init; }
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private double _value;
}