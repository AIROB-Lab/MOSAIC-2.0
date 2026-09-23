using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.Models;

/// <summary>Pass-through vector template with the standard visualization bundle.</summary>
/// <remarks>
/// Copy and rename this class, then add factory and catalogue entries before loading it.
/// Bind a VisualizationPanel to Viz. The panel manages visibility; this model owns disposal.
/// The default input constraints require one input. No custom parameters are needed.
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "Display": { "Type": "templateviz", "Inputs": ["Signal"] }
/// }
/// </code>
/// <para>This fragment requires a registered templateviz factory key and a vector-producing Signal block.</para>
/// </example>
public class TemplateViz : BaseBlock
{
    /// <summary>Standard Scope, Spider and Heatmap bundle, fed with the published output.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>Scope alias for callers that use the individual monitor; null in headless tests.</summary>
    public ScopeMonitor? Scope => Viz.Scope;

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Creates a template instance; a zero rate is inherited from upstream.</summary>
    public TemplateViz(string name = "TemplateViz", double desiredRate = 0)
        : base(name, desiredRate) { }

    /// <summary>Reads common JSON fields; recording is attached by BlockFactory.Create.</summary>
    public static TemplateViz ConfigureInput(IServiceProvider sp, JsonModel m) =>
        ActivatorUtilities.CreateInstance<TemplateViz>(sp, m.Name ?? "TemplateViz", m.DesiredRate ?? 0d);

    /// <inheritdoc />
    protected override string JsonTypeName => "templateviz";

    /// <inheritdoc />
    protected override void OnReceive(object sender, object value)
    {
        if (value is not Vector<double> input)
            return;

        // Replace this copy with your processing; never modify another block's input.
        var output = input.Clone();
        Publish(output);
        Viz.Feed(output);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) Viz.Dispose();
        base.Dispose(disposing);
    }
}
