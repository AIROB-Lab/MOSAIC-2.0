using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Manual slider-driven source of <see cref="DegreesOfActuation"/> values. Produces the same
/// output format as <see cref="ControlAlgorithm"/> (<c>Dictionary&lt;DegreesOfActuation, double&gt;</c>,
/// values in the 0–100 range), so it can be placed directly in front of
/// <c>UdpStreamlinedSender</c> to manually drive the Unity hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> On each tick received from an upstream source (typically a
/// <see cref="ClockBlock"/>), the block snapshots the current slider values, builds a dictionary
/// containing only the DOAs whose group <i>and</i> individual enable flag are both true, publishes
/// it downstream, and feeds its scope.
/// </para>
/// <para>
/// <b>Group gating:</b> The <see cref="FingersEnabled"/>, <see cref="WristEnabled"/> and
/// <see cref="ElbowEnabled"/> flags gate entire sections. When a group flag is false, none of
/// the DOAs in that group are published, regardless of per-DOA flags. This is the fast way to
/// shorten the downstream UDP protocol when a body segment is not in use.
/// </para>
/// <para>
/// <b>Master HOC:</b> The <see cref="HandOpenClose"/> slider acts as a master control. When
/// <see cref="LinkFingers"/> is <see langword="true"/>, moving the HOC slider sets all five
/// finger sliders to the same value, giving a single-DOF grasp control.
/// </para>
/// <para>
/// <b>Value range:</b> Sliders are in [0, 100], matching the convention used by
/// <see cref="ControlAlgorithm"/> strategies. Unity remaps to actuator space per-DOA in its Inspector.
/// </para>
/// <para>
/// <b>Source block:</b> Driven by tick events on <see cref="OnReceive"/>. The incoming data
/// value is ignored — only tick arrival matters. Wire any periodic publisher (e.g.
/// <see cref="ClockBlock"/>) as input.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs the published value vector via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration using ManualControl with a Clock upstream and UdpStreamlinedSender downstream:
/// <code>
/// {
///   "Clock":         { "Type": "Clock",               "DesiredRate": 30 },
///   "ManualControl": { "Type": "ManualControl",       "Inputs": ["Clock"] },
///   "UdpToUnity":    { "Type": "UdpStreamlinedSender","Inputs": ["ManualControl"],
///                      "Params": ["localhost", 11000] }
/// }
/// </code>
/// <para>
/// Note: <see cref="MOSAIC.Models.Streaming.UdpStreamlinedSender"/> dispatches on the upstream
/// block's type name, so its <c>OnReceive</c> switch must include a case for <c>"ManualControl"</c>
/// alongside <c>"ControlAlgorithm"</c>.
/// </para>
/// <para><b>Params:</b> None. All settings are controlled from the UI.</para>
/// </example>
public sealed partial class ManualControl : BaseBlock
{
    private readonly object _lock = new();

    // ---- Group gates ------------------------------------------------------

    /// <summary>Group gate for all six finger DOAs. When false, no finger DOA is published.</summary>
    [ObservableProperty] private bool _fingersEnabled = true;

    /// <summary>Group gate for the three wrist DOAs. When false, no wrist DOA is published.</summary>
    [ObservableProperty] private bool _wristEnabled;

    /// <summary>Group gate for the two elbow DOAs. When false, no elbow DOA is published.</summary>
    [ObservableProperty] private bool _elbowEnabled;

    // ---- Master control ---------------------------------------------------

    /// <summary>Master hand open/close slider (0–100). Drives all finger sliders when
    /// <see cref="LinkFingers"/> is true.</summary>
    [ObservableProperty] private double _handOpenClose;

    /// <summary>Include <see cref="DegreesOfActuation.HandOpenClose"/> as its own entry in the
    /// published dictionary. Independent of <see cref="LinkFingers"/>.</summary>
    [ObservableProperty] private bool _sendHandOpenClose;

    /// <summary>When true, changes to <see cref="HandOpenClose"/> propagate to all five finger
    /// sliders, giving a single-DOF grasp control.</summary>
    [ObservableProperty] private bool _linkFingers = true;

    // ---- Finger DOAs ------------------------------------------------------

    [ObservableProperty] private double _thumbFlexion;
    [ObservableProperty] private double _thumbRotation;
    [ObservableProperty] private double _index;
    [ObservableProperty] private double _middle;
    [ObservableProperty] private double _ring;
    [ObservableProperty] private double _little;

    [ObservableProperty] private bool _sendThumbFlexion   = true;
    [ObservableProperty] private bool _sendThumbRotation  = true;
    [ObservableProperty] private bool _sendIndex          = true;
    [ObservableProperty] private bool _sendMiddle         = true;
    [ObservableProperty] private bool _sendRing           = true;
    [ObservableProperty] private bool _sendLittle         = true;

    // ---- Wrist DOAs -------------------------------------------------------

    [ObservableProperty] private double _wristFlexionExtension = 50;
    [ObservableProperty] private double _wristUlnarRadial = 50;
    [ObservableProperty] private double _wristPronationSupination = 50;

    [ObservableProperty] private bool _sendWristFlexionExtension;
    [ObservableProperty] private bool _sendWristUlnarRadial;
    [ObservableProperty] private bool _sendWristPronationSupination;

    // ---- Elbow DOAs -------------------------------------------------------

    /// <summary>Elbow flexion activation (0–100, 0 = no activation).</summary>
    [ObservableProperty] private double _elbowFlexion;

    /// <summary>Elbow extension activation (0–100, 0 = no activation).</summary>
    [ObservableProperty] private double _elbowExtension;

    [ObservableProperty] private bool _sendElbowFlexion;
    [ObservableProperty] private bool _sendElbowExtension;

    // ---- Visualization ----------------------------------------------------

    /// <summary>Visualization bundle for the card UI (scope of published values).</summary>
    public BlockVisualization Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    // ---- Construction -----------------------------------------------------

    /// <summary>
    /// Initializes a new <see cref="ManualControl"/> block.
    /// </summary>
    public ManualControl(string name = "ManualControl", double desiredRate = 0)
        : base(name, desiredRate)
    {
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_dof.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_dof";

    /// <summary>Creates a <see cref="ManualControl"/> from a JSON pipeline definition.</summary>
    public static ManualControl ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "ManualControl";
        var rate = m.DesiredRate ?? 0;

        var block = ActivatorUtilities.CreateInstance<ManualControl>(sp, name, rate);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "ManualControl";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams() => null;

    #endregion

    // ---- Link propagation -------------------------------------------------

    partial void OnHandOpenCloseChanged(double value)
    {
        if (!LinkFingers) return;

        lock (_lock)
        {
            ThumbFlexion  = value;
            ThumbRotation = value;
            Index         = value;
            Middle        = value;
            Ring          = value;
            Little        = value;
        }
    }

    // ---- Convenience actions ---------------------------------------------

    /// <summary>Resets fingers and elbow to 0, wrists to 50, and HOC to 0.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            HandOpenClose = 0;
            ThumbFlexion  = 0;
            ThumbRotation = 0;
            Index         = 0;
            Middle        = 0;
            Ring          = 0;
            Little        = 0;
            WristFlexionExtension    = 50;
            WristUlnarRadial         = 50;
            WristPronationSupination = 50;
            ElbowFlexion   = 0;
            ElbowExtension = 0;
        }
    }

    /// <summary>Sets all DOAs to the neutral mid-point (50).</summary>
    public void Neutral()
    {
        lock (_lock)
        {
            HandOpenClose = 50;
            ThumbFlexion  = 50;
            ThumbRotation = 50;
            Index         = 50;
            Middle        = 50;
            Ring          = 50;
            Little        = 50;
            WristFlexionExtension    = 50;
            WristUlnarRadial         = 50;
            WristPronationSupination = 50;
            ElbowFlexion   = 50;
            ElbowExtension = 50;
        }
    }

    // ---- Publishing -------------------------------------------------------

    /// <summary>
    /// On each tick from the upstream source, snapshot the enabled DOAs and publish the
    /// dictionary. The incoming data value is ignored.
    /// </summary>
    protected override void OnReceive(object sender, object value)
    {
        Dictionary<DegreesOfActuation, double> dict;

        lock (_lock)
        {
            dict = new Dictionary<DegreesOfActuation, double>(capacity: 12);

            if (FingersEnabled)
            {
                if (SendThumbFlexion)  dict[DegreesOfActuation.ThumbFlexion]  = ThumbFlexion;
                if (SendThumbRotation) dict[DegreesOfActuation.ThumbRotation] = ThumbRotation;
                if (SendIndex)         dict[DegreesOfActuation.Index]         = Index;
                if (SendMiddle)        dict[DegreesOfActuation.Middle]        = Middle;
                if (SendRing)          dict[DegreesOfActuation.Ring]          = Ring;
                if (SendLittle)        dict[DegreesOfActuation.Little]        = Little;
            }

            if (WristEnabled)
            {
                if (SendWristFlexionExtension)    dict[DegreesOfActuation.WristFlexionExtension]    = WristFlexionExtension;
                if (SendWristUlnarRadial)         dict[DegreesOfActuation.WristUlnarRadial]         = WristUlnarRadial;
                if (SendWristPronationSupination) dict[DegreesOfActuation.WristPronationSupination] = WristPronationSupination;
            }

            if (ElbowEnabled)
            {
                if (SendElbowFlexion)   dict[DegreesOfActuation.ElbowFlexion]   = ElbowFlexion;
                if (SendElbowExtension) dict[DegreesOfActuation.ElbowExtension] = ElbowExtension;
            }

            if (SendHandOpenClose) dict[DegreesOfActuation.HandOpenClose] = HandOpenClose;
        }

        if (dict.Count == 0) return;

        // Publish the dict downstream — UdpStreamlinedSender will serialise and send it.
        Publish(dict);

        if (Viz is not null)
        {
            var v = Vector<double>.Build.Dense(dict.Count);
            int i = 0;
            foreach (var kv in dict) v[i++] = kv.Value;
            Viz.Feed(v);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }
}
