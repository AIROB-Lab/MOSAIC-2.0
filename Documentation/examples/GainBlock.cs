using System;
using System.Collections.Generic;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>Multiplies every element of an incoming vector by a scalar gain.</summary>
/// <remarks>
/// Accepts one Vector&lt;double&gt; input and publishes a new vector of the same length.
/// Params[0] is the gain (default 1.0). Negative and zero gains are allowed.
/// Other payload types are ignored.
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "Amplify": {
///     "Type": "gainblock",
///     "Inputs": ["Signal"],
///     "Params": [2.5]
///   }
/// }
/// </code>
/// <para>Signal must name an upstream vector-producing block. Params[0] is the gain.</para>
/// </example>
public sealed class GainBlock : BaseBlock
{
    #region Settings

    private double _gain;

    /// <summary>The multiplier currently used by the processing code.</summary>
    public double Gain => Volatile.Read(ref _gain);

    /// <summary>Applies a finite gain. Zero and negative values are allowed.</summary>
    public void SetGain(double gain)
    {
        if (!double.IsFinite(gain))
            throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be finite.");

        Volatile.Write(ref _gain, gain);
    }

    #endregion

    #region Construction

    public GainBlock(string name, double desiredRate = 0, double gain = 1.0)
        : base(name, desiredRate)
    {
        SetGain(gain);
    }

    #endregion

    #region Processing

    protected override void OnReceive(object sender, object value)
    {
        if (value is not Vector<double> input)
            return;

        // Read once so every channel uses the same gain.
        double gain = Gain;
        var output = input.Multiply(gain);
        Publish(output);
    }

    #endregion

    #region JSON configuration

    public static GainBlock ConfigureInput(IServiceProvider services, JsonModel model)
    {
        double gain = model.Params is { Count: > 0 }
            ? JsonModel.GetDouble(model.Params[0], 1.0)
            : 1.0;

        return ActivatorUtilities.CreateInstance<GainBlock>(
            services, model.Name ?? "Gain", model.DesiredRate ?? 0d, gain);
    }

    protected override string JsonTypeName => "gainblock";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { Gain };

    #endregion
}
