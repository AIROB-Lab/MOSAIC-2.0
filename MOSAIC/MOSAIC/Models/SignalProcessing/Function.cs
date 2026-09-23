using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Applies element-wise mathematical functions to input vectors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Each element of the incoming <see cref="Vector{T}"/> is transformed
/// by the configured function and the result is published downstream. The function type
/// and parameters can be changed at runtime — the internal delegate is rebuilt automatically.
/// </para>
/// <para>
/// <b>Supported functions:</b>
/// <list type="table">
///   <listheader><term>Type</term><description>Formula</description></listheader>
///   <item><term><c>abs</c></term><description>|x|</description></item>
///   <item><term><c>add</c></term><description>x + P1</description></item>
///   <item><term><c>multiply</c></term><description>x × P1</description></item>
///   <item><term><c>power</c></term><description>x^P1</description></item>
///   <item><term><c>clip</c></term><description>clamp(x, P1, P2)</description></item>
///   <item><term><c>threshold</c></term><description>sign(x) × max(0, |x| − P1)</description></item>
/// </list>
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs the output vector via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Rectify": {
///     "Type": "Function",
///     "Inputs": [ "EMG" ],
///     "Params": [ "abs" ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>FunctionType</c> (string) — abs, add, multiply, power, clip, threshold.</description></item>
///   <item><term>1</term><description><c>Param1</c> (double, optional) — first parameter.</description></item>
///   <item><term>2</term><description><c>Param2</c> (double, optional) — second parameter (clip upper bound).</description></item>
/// </list>
/// </para>
/// </example>
public partial class Function : BaseBlock
{
    /// <summary>
    /// Optional visualization bundle. Set by the ViewModel so the block can feed
    /// data to all monitors in one call.
    /// </summary>
    private BlockVisualization? _viz;

    /// <summary>Live plot for this block. Assigned by the ViewModel, which owns the scope.</summary>
    /// <remarks>
    /// Setting this re-pushes the publish rate: the rate is normally pushed when it changes,
    /// which for these blocks happens during construction — before the ViewModel has handed
    /// over the scope — so without this the scope would never learn its time base.
    /// </remarks>
    public BlockVisualization? Viz
    {
        get => _viz;
        set { _viz = value; RefreshVisualizationRate(); }
    }

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>The compiled element-wise function delegate.</summary>
    private Func<double, double> _function;

    /// <summary>
    /// The function type string (abs, add, multiply, power, clip, threshold).
    /// </summary>
    [ObservableProperty]
    private string _functionType = "abs";

    /// <summary>First parameter (used by add, multiply, power, threshold, clip).</summary>
    [ObservableProperty]
    private double _param1;

    /// <summary>Second parameter (used by clip for the upper bound).</summary>
    [ObservableProperty]
    private double _param2;

    /// <summary>Available function type names for UI dropdowns.</summary>
    public static IReadOnlyList<string> AvailableFunctionTypes { get; } = new[]
    {
        "abs", "add", "multiply", "power", "clip", "threshold"
    };

    /// <summary>
    /// Initializes a new <see cref="Function"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited).</param>
    /// <param name="function">The element-wise function to apply.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="function"/> is <see langword="null"/>.</exception>
    public Function(
        string name,
        double desiredRate,
        Func<double, double> function) : base(name, desiredRate)
    {
        _function = function ?? throw new ArgumentNullException(nameof(function));
    }

    /// <summary>Rebuilds the function delegate when the type changes.</summary>
    partial void OnFunctionTypeChanged(string value) => RebuildFunction();
    /// <summary>Rebuilds the function delegate when Param1 changes.</summary>
    partial void OnParam1Changed(double value) => RebuildFunction();
    /// <summary>Rebuilds the function delegate when Param2 changes.</summary>
    partial void OnParam2Changed(double value) => RebuildFunction();

    /// <summary>Recompiles the element-wise delegate from current property values.</summary>
    private void RebuildFunction()
    {
        var type = FunctionType?.Trim().ToLowerInvariant() ?? "abs";
        var p1 = Param1;
        var p2 = Param2;

        _function = type switch
        {
            "add" => v => v + p1,
            "multiply" => v => v * p1,
            "abs" => Math.Abs,
            "power" => v => Math.Pow(v, p1),
            "clip" => v => Math.Clamp(v, p1, p2),
            "threshold" => v =>
            {
                var mag = Math.Abs(v) - p1;
                return Math.Sign(v) * (mag > 0 ? mag : 0);
            },
            _ => Math.Abs
        };
    }

    /// <summary>
    /// Sets function type and parameters programmatically and notifies the UI.
    /// </summary>
    /// <param name="type">Function type string.</param>
    /// <param name="param1">First parameter.</param>
    /// <param name="param2">Second parameter.</param>
    public void SetFunction(string type, double param1 = 0, double param2 = 0)
    {
        _functionType = type;
        _param1 = param1;
        _param2 = param2;

        RebuildFunction();

        OnPropertyChanged(nameof(FunctionType));
        OnPropertyChanged(nameof(Param1));
        OnPropertyChanged(nameof(Param2));
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_fn.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_fn";

    /// <summary>
    /// Creates a <see cref="Function"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="Function"/> instance.</returns>
    public static Function ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Function";
        var rate = m.DesiredRate ?? 0;

        var (function, fnType, p1, p2) = BuildFunction(m);

        var instance = ActivatorUtilities.CreateInstance<Function>(sp, name, rate, function);

        instance._functionType = fnType;
        instance._param1 = p1;
        instance._param2 = p2;
        instance.OnPropertyChanged(nameof(FunctionType));
        instance.OnPropertyChanged(nameof(Param1));
        instance.OnPropertyChanged(nameof(Param2));

        return instance;
    }

    /// <summary>
    /// Parses the JSON params and builds the initial function delegate.
    /// </summary>
    private static (Func<double, double> function, string type, double p1, double p2) BuildFunction(JsonModel m)
    {
        var p = m.Params ?? new List<object>(0);

        string fnName = (p.Count >= 1 ? p[0]?.ToString() : null)?.Trim().ToLowerInvariant()
                        ?? "abs";

        static bool TryNum(object? o, out double d)
        {
            if (o is null) { d = 0; return false; }
            if (o is double dd) { d = dd; return true; }
            if (o is float ff) { d = ff; return true; }
            if (o is int ii) { d = ii; return true; }
            if (o is long ll) { d = ll; return true; }
            var s = o.ToString();
            return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                                   CultureInfo.InvariantCulture, out d)
                || double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                                   CultureInfo.CurrentCulture, out d);
        }

        double GetParam(int idx, double defaultVal = 0)
        {
            if (idx < p.Count && TryNum(p[idx], out var v))
                return v;
            return defaultVal;
        }

        double param1 = GetParam(1, 0);
        double param2 = GetParam(2, 0);

        Func<double, double> function = fnName switch
        {
            "add" => v => v + param1,
            "multiply" => v => v * param1,
            "abs" => Math.Abs,
            "power" => v => Math.Pow(v, param1),
            "clip" => p.Count >= 3
                ? v => Math.Clamp(v, param1, param2)
                : v => Math.Clamp(v, 0.0, param1),
            "threshold" => v =>
            {
                var mag = Math.Abs(v) - param1;
                return Math.Sign(v) * (mag > 0 ? mag : 0);
            },
            _ => Math.Abs
        };

        if (fnName == "clip" && p.Count < 3)
        {
            param2 = param1;
            param1 = 0;
        }

        return (function, fnName, param1, param2);
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Function";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
    {
        var type = FunctionType?.ToLowerInvariant() ?? "abs";
        return type switch
        {
            "abs" => new List<object> { type },
            "add" or "multiply" or "power" or "threshold" => new List<object> { type, Param1 },
            "clip" => new List<object> { type, Param1, Param2 },
            _ => new List<object> { type }
        };
    }

    #endregion

    /// <summary>
    /// Applies the configured function element-wise to the input vector and publishes the result.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// Expected to be a <see cref="Vector{T}"/> of <see cref="double"/>.
    /// Non-vector inputs are silently ignored.
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        if (data is Vector<double> snapshot)
        {
            var output = snapshot.Map(_function);
            Publish(output);
            Viz?.Feed(output);
        }
        else if (data is Matrix<double> mat)
        {
            var output = mat.Map(_function);
            Publish(output);
            Viz?.Feed(output);
        }
    }

    /// <summary>
    /// Releases visualization and base class resources (including CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }
}