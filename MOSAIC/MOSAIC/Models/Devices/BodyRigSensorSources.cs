using System;
using System.Collections.Generic;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.Models.Devices;

/// <summary>
/// What one positional sensor slot on a <see cref="BodyRig"/> actually is: which physical
/// peripheral feeds it, and which block in the pipeline it arrived through.
/// </summary>
/// <param name="Slot">
/// Index of the quaternion group in the joined input vector — the number a segment's
/// <c>SensorIndex</c> refers to, shown on the card as <c>S0</c>, <c>S1</c>, …
/// </param>
/// <param name="PeripheralId">
/// The number set on the ESP node itself, or <see langword="null"/> when the pipeline does not
/// make it knowable (no UDP source upstream, or a port outside the BodyRig range).
/// </param>
/// <param name="SourceName">Name of the block that contributed this group.</param>
public sealed record BodyRigSensorSource(int Slot, int? PeripheralId, string SourceName)
{
    /// <summary>
    /// Slot with the peripheral number appended when it is known, e.g. <c>S1 · #92</c>.
    /// </summary>
    /// <remarks>
    /// Both halves are kept deliberately. The slot is what the binding and the calibration file
    /// mean; the peripheral is what is physically strapped to the participant and printed in the
    /// lab protocol. Showing only one of them forces the reader to hold the other in their head.
    /// </remarks>
    public string Label => PeripheralId is int id ? $"S{Slot} · #{id}" : $"S{Slot}";

    /// <summary>The UDP port this peripheral transmits on, when it is known.</summary>
    public int? UdpPort => PeripheralId is int id ? BodyRigSensorSources.PortBase + id : null;
}

/// <summary>
/// Recovers the peripheral number behind each of a <see cref="BodyRig"/>'s positional sensor
/// slots by walking the pipeline graph back to the sockets the packets arrive on.
/// </summary>
/// <remarks>
/// <para>
/// A rig fed from upstream sees only a concatenated vector of quaternions; which ESP node
/// produced group <em>k</em> is not in the data. It <em>is</em> in the configuration, because the
/// firmware derives each node's UDP port from its sensor number — "11" followed by the number,
/// so node 18 transmits on 11018 and node 71 on 11071. Reading the port back is therefore exact,
/// not a convention this code invents.
/// </para>
/// <para>
/// Ordering comes from the <see cref="Joiner"/>'s declared inputs, which is the same list the
/// Joiner concatenates by, so slot <em>k</em> here is the slot the rig will actually bind.
/// </para>
/// <para>
/// Display only. Nothing in the kinematics consults this, and a slot it cannot name simply shows
/// without a peripheral rather than guessing one.
/// </para>
/// </remarks>
public static class BodyRigSensorSources
{
    /// <summary>Port prefix the firmware prepends to the sensor number.</summary>
    public const int PortBase = 11000;

    /// <summary>Largest node number that still yields a port in the documented range.</summary>
    private const int MaxPeripheral = 999;

    /// <summary>Values one sensor contributes: <c>w, x, y, z</c>.</summary>
    private const int ValuesPerSensor = 4;

    /// <summary>
    /// Resolves <paramref name="rig"/>'s sensor slots against the blocks of the pipeline it
    /// belongs to. Returns an empty list when the rig has no resolvable input.
    /// </summary>
    /// <param name="rig">The rig whose slots to describe.</param>
    /// <param name="byName">Every block in the pipeline, keyed by <c>Name</c>.</param>
    public static IReadOnlyList<BodyRigSensorSource> Resolve(
        BodyRig rig, IReadOnlyDictionary<string, BaseBlock> byName)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(byName);

        var head = FirstInput(rig, byName);
        if (head is null) return [];

        // A Joiner fans several nodes into the one vector the rig accepts; anything else is a
        // single source feeding the rig directly.
        var sources = head is Joiner joiner ? OrderedInputs(joiner, byName) : [head];

        var slots = new List<BodyRigSensorSource>();
        int slot = 0;

        foreach (var source in sources)
        {
            int? peripheral = PeripheralBehind(source, byName);

            // A source carrying more than one quaternion occupies more than one slot. Every one
            // of them came off the same socket, so they all carry the same peripheral number.
            int groups = Math.Max(1, WidthOf(source) / ValuesPerSensor);
            for (int g = 0; g < groups; g++)
                slots.Add(new BodyRigSensorSource(slot++, peripheral, source.Name));
        }

        return slots;
    }

    /// <summary>Resolves a block's first declared input to an instance, if it has one.</summary>
    private static BaseBlock? FirstInput(BaseBlock block, IReadOnlyDictionary<string, BaseBlock> byName)
    {
        var names = block.Inputs;
        if (names is null || names.Count == 0) return null;

        return byName.TryGetValue(names[0], out var input) ? input : null;
    }

    /// <summary>The Joiner's inputs in declared order — the order it concatenates in.</summary>
    private static List<BaseBlock> OrderedInputs(Joiner joiner, IReadOnlyDictionary<string, BaseBlock> byName)
    {
        var inputs = new List<BaseBlock>();
        foreach (var name in joiner.Inputs ?? [])
            if (byName.TryGetValue(name, out var input)) inputs.Add(input);

        return inputs;
    }

    /// <summary>
    /// How many values a source contributes. A <see cref="Projector"/> states it exactly; for
    /// anything else one quaternion is the only defensible assumption.
    /// </summary>
    private static int WidthOf(BaseBlock block) =>
        block is Projector { OutputChannels: > 0 } projector ? projector.OutputChannels : ValuesPerSensor;

    /// <summary>
    /// Walks upstream from <paramref name="block"/> to the socket the packets arrive on and
    /// converts its port back to a node number.
    /// </summary>
    private static int? PeripheralBehind(BaseBlock block, IReadOnlyDictionary<string, BaseBlock> byName)
    {
        // A malformed config can cycle; visiting each block once keeps this terminating rather
        // than hanging the UI thread that calls it.
        var visited = new HashSet<BaseBlock>();

        for (var current = block; current is not null && visited.Add(current);
             current = FirstInput(current, byName))
        {
            if (current is not IReceivePort socket) continue;

            int node = socket.ReceivePort - PortBase;
            return node is > 0 and <= MaxPeripheral ? node : null;
        }

        return null;
    }
}
