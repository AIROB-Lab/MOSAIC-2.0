using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Extracts the data payload from a labelled tuple, discarding the label string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Several blocks publish <c>(string label, data)</c> tuples.
/// The Selector strips the label and forwards only the data, allowing downstream
/// blocks that expect a bare <see cref="Vector{T}"/> or <see cref="Matrix{T}"/>
/// to consume it without modification.
/// </para>
/// <para>
/// <b>Accepted input types:</b>
/// <list type="bullet">
///   <item><description><c>ValueTuple&lt;string, Vector&lt;double&gt;&gt;</c> — forwards the vector.</description></item>
///   <item><description><c>ValueTuple&lt;string, Matrix&lt;double&gt;&gt;</c> — forwards the matrix.</description></item>
///   <item><description><c>Tuple&lt;string, Vector&lt;double&gt;&gt;</c> — legacy class tuple; forwards the vector.</description></item>
///   <item><description><c>Tuple&lt;string, Matrix&lt;double&gt;&gt;</c> — legacy class tuple; forwards the matrix.</description></item>
///   <item><description><c>ValueTuple&lt;string, object&gt;</c> — forwards <c>Item2</c> if it is a Vector or Matrix.</description></item>
/// </list>
/// All other types are rejected and reported through <see cref="BaseBlock.ReportError"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "LDAOutput": {
///     "Type": "Selector",
///     "Inputs": [ "OnlineLDA" ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// </example>
public class Selector : BaseBlock
{
    public Selector(string name, double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_vec.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_vec";

    public static Selector ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var block = ActivatorUtilities.CreateInstance<Selector>(sp, m.Name ?? "Selector", m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Selector";

    #endregion

    protected override void OnReceive(object sender, object? value)
    {
        switch (value)
        {
            // ValueTuple with typed Vector
            case ValueTuple<string, Vector<double>> vecTuple:
                Publish(vecTuple.Item2);
                break;

            // ValueTuple with typed Matrix
            case ValueTuple<string, Matrix<double>> matTuple:
                Publish(matTuple.Item2);
                break;

            // ValueTuple with object payload (runtime-typed)
            case ValueTuple<string, object> objTuple when objTuple.Item2 is Vector<double> or Matrix<double>:
                Publish(objTuple.Item2);
                break;

            // Legacy class Tuple with Vector
            case Tuple<string, Vector<double>> classicVec:
                Publish(classicVec.Item2);
                break;

            // Legacy class Tuple with Matrix
            case Tuple<string, Matrix<double>> classicMat:
                Publish(classicMat.Item2);
                break;

            default:
                ReportError("Expected a labelled vector or matrix.");
                break;
        }
    }
}