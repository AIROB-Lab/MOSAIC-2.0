using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models;

/// <summary>
/// Minimal vector block template: receives data and publishes it downstream.
/// </summary>
/// <remarks>
/// <para>Copy and rename this class, then add factory and catalogue entries.
/// This template is not registered as a loadable block in the shipped application.</para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified in the JSON config,
/// <see cref="BaseBlock.Dumper"/> automatically logs every value passed to
/// <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "MyBlock": {
///     "Type": "TemplateMinimal",
///     "Inputs": [ "Upstream" ],
///     "Params": [ ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// </example>
public class TemplateMinimal : BaseBlock
{
    /// <summary>
    /// Initializes a new <see cref="TemplateMinimal"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited).</param>
    public TemplateMinimal(string name = "TemplateMinimal", double desiredRate = 0)
        : base(name, desiredRate)
    {
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_out.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_out";

    /// <summary>
    /// Creates a <see cref="TemplateMinimal"/> from a JSON pipeline definition.
    /// </summary>
    public static TemplateMinimal ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "TemplateMinimal";
        var rate = m.DesiredRate ?? 0;
        // var p = m.Params;  // parse parameters here if needed

        var block = ActivatorUtilities.CreateInstance<TemplateMinimal>(sp, name, rate);
        return block;
    }

    /// <inheritdoc />
    protected override string JsonTypeName => "TemplateMinimal";

    /// <summary>
    /// Processes incoming data and publishes the result.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        if (data is not Vector<double> snapshot) return;

        // ── your processing logic here ──

        Publish(snapshot);
    }
}