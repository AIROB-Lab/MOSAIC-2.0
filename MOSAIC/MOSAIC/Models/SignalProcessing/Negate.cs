using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Flips the sign of every value in the incoming vector. The smallest possible block:
/// one input, one output, no parameters, no card view.
/// </summary>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Flip": {
///     "Type": "negate",
///     "Inputs": [ "SinGenerator1" ]
///   }
/// }
/// </code>
/// <para><b>Params:</b> None.</para>
/// </example>
public sealed class Negate : BaseBlock
{
    public Negate(string name, double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>Called by <see cref="MOSAIC.Components.Factory.BlockFactory"/> for the key <c>negate</c>.</summary>
    public static Negate ConfigureInput(IServiceProvider sp, JsonModel m) =>
        ActivatorUtilities.CreateInstance<Negate>(sp, m.Name ?? "Negate", m.DesiredRate ?? 0d);

    /// <inheritdoc />
    protected override string JsonTypeName => "negate";

    /// <inheritdoc />
    protected override void OnReceive(object sender, object value)
    {
        if (value is not Vector<double> v)
        {
            ReportError("Negate requires a numeric vector.");
            return;
        }

        Publish(-v);
    }
}
