using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// A two-input switch that forwards data from the currently active input to downstream blocks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> The Switch block accepts connections from exactly two upstream blocks.
/// At any time, only one input is "active" — data from the active input is published downstream,
/// while data from the inactive input is silently dropped.
/// </para>
/// <para>
/// <b>Input identification:</b> Senders are identified by reference on first arrival.
/// The first distinct sender becomes input 0, the second becomes input 1. This assignment
/// is stable for the lifetime of the block and depends on which upstream block publishes first.
/// </para>
/// <para>
/// <b>Switching:</b> Use <see cref="Toggle"/> or <see cref="SetActiveInput"/> to change the
/// active input at runtime. The <see cref="UseSecondInput"/> property is observable for UI binding.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs whatever the active input publishes via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "InputSwitch": {
///     "Type": "Switch",
///     "Inputs": [ "SourceA", "SourceB" ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> None. Active input is controlled at runtime via the UI or programmatically.
/// </para>
/// </example>
public partial class Switch : BaseBlock
{
    /// <inheritdoc />
    public override int MinInputs => 2;
    /// <inheritdoc />
    public override int MaxInputs => 2;

    /// <summary>Reference to the first sender (input 0), assigned on first data arrival.</summary>
    private object? _input0Sender;

    /// <summary>Reference to the second sender (input 1), assigned when a distinct sender arrives.</summary>
    private object? _input1Sender;

    /// <summary>
    /// Gets or sets whether input 1 is active (<see langword="true"/>) or
    /// input 0 is active (<see langword="false"/>).
    /// </summary>
    [ObservableProperty]
    private bool _useSecondInput;

    /// <summary>
    /// Raised when the active input changes, with the new <see cref="UseSecondInput"/> value.
    /// </summary>
    public event Action<bool>? InputSwitched;
    

    /// <summary>
    /// Initializes a new <see cref="Switch"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz (typically inherited).</param>
    public Switch(string name, double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_switched.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_switched";

    /// <summary>
    /// Creates a <see cref="Switch"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model containing configuration.</param>
    /// <returns>A configured <see cref="Switch"/> instance.</returns>
    public static Switch ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var block = ActivatorUtilities.CreateInstance<Switch>(sp, m.Name ?? "Switch", m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Switch";

    #endregion

    /// <summary>
    /// Toggles between the two inputs and raises <see cref="InputSwitched"/>.
    /// </summary>
    public void Toggle()
    {
        UseSecondInput = !UseSecondInput;
        InputSwitched?.Invoke(UseSecondInput);
    }

    /// <summary>
    /// Sets which input is active by index and raises <see cref="InputSwitched"/>.
    /// </summary>
    /// <param name="inputIndex">0 for the first input, 1 for the second.</param>
    public void SetActiveInput(int inputIndex)
    {
        UseSecondInput = inputIndex == 1;
        InputSwitched?.Invoke(UseSecondInput);
    }

    /// <summary>
    /// Identifies which input the data came from and forwards it if from the active input.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="value">The data payload (any type — forwarded transparently).</param>
    /// <remarks>
    /// Sender identification is by reference: the first distinct sender becomes input 0,
    /// the second becomes input 1. Data from the inactive input is silently dropped.
    /// </remarks>
    protected override void OnReceive(object sender, object? value)
    {
        if (_input0Sender == null)
            _input0Sender = sender;
        else if (_input1Sender == null && sender != _input0Sender)
            _input1Sender = sender;

        bool isFromSecondInput = sender == _input1Sender;

        if (isFromSecondInput == UseSecondInput)
        {
            Publish(value);
        }
    }
}