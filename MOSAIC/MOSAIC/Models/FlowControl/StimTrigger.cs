using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Converts <see cref="MOSAIC.Models.Devices.Stimulus"/> output tuples into start/stop
/// commands for a downstream <see cref="Buffer"/> block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> The StimTrigger listens for <c>(string state, Vector&lt;double&gt; target)</c>
/// tuples from a Stimulus block. When the state is <c>"capture"</c>, the target vector is
/// published downstream (signalling the Buffer to start collecting and providing the target).
/// When the state transitions away from <c>"capture"</c>, <see langword="null"/> is published
/// (signalling the Buffer to stop and finalise the segment).
/// </para>
/// <para>
/// <b>State machine:</b>
/// <list type="bullet">
///   <item><description><c>"capture"</c> received → publish target vector, set <see cref="IsCapturing"/> = <see langword="true"/>.</description></item>
///   <item><description>Any other state received while capturing → publish <see langword="null"/>, set <see cref="IsCapturing"/> = <see langword="false"/>.</description></item>
///   <item><description>Non-capture states while not capturing → ignored (no publish).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs the published target vector during capture via <see cref="BaseBlock.Publish"/>.
/// The <see langword="null"/> stop signal is silently skipped by the dumper.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "StimGate": {
///     "Type": "StimTrigger",
///     "Inputs": [ "Stimulus" ],
///     "DesiredRate": 200,
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> None. Requires exactly one input (a Stimulus block).
/// </para>
/// </example>
public class StimTrigger : BaseBlock
{
    /// <summary>Backing field for <see cref="IsCapturing"/>.</summary>
    private bool _isCapturing;

    /// <summary>
    /// Gets a value indicating whether the block is currently in the capturing state
    /// (i.e., the upstream Stimulus is in its <c>"capture"</c> phase).
    /// </summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        private set => SetProperty(ref _isCapturing, value);
    }

    /// <summary>
    /// Initializes a new <see cref="StimTrigger"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz.</param>
    public StimTrigger(string name = "StimTrigger", double desiredRate = 200)
    {
        Name = name;
        DesiredRate = desiredRate;
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_trigger.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_trigger";

    /// <summary>
    /// Creates a <see cref="StimTrigger"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. Must have exactly one input (a Stimulus block).
    /// </param>
    /// <returns>A configured <see cref="StimTrigger"/> instance.</returns>
    /// <exception cref="Exception">Thrown if the input count is not exactly 1.</exception>
    public static StimTrigger ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "StimTrigger";
        var rate = m.DesiredRate ?? 200;

        if (m.Inputs.Count != 1)
            throw new Exception($"StimTrigger {m.Name} must have 1 input block.");

        var block = ActivatorUtilities.CreateInstance<StimTrigger>(sp, name, rate);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "StimTrigger";

    #endregion

    /// <summary>
    /// Handles input from the connected Stimulus block. Publishes the target vector
    /// during capture and <see langword="null"/> when capture ends.
    /// </summary>
    /// <param name="sender">The upstream Stimulus block.</param>
    /// <param name="value">
    /// Expected to be a <c>ValueTuple&lt;string, Vector&lt;double&gt;&gt;</c>
    /// where Item1 is the FSM state name and Item2 is the target vector.
    /// </param>
    protected override void OnReceive(object sender, object value)
    {
        var (state, tv) = ((string, Vector<double>))value;

        if (state == "capture")
        {
            Publish(tv);
            IsCapturing = true;
        }
        else if (_isCapturing)
        {
            Publish(null);
            IsCapturing = false;
        }
    }
}