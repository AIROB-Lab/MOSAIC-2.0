using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MOSAIC.Components.Basics;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Factory;

/// <summary>
/// Resolves a block type's input constraints — <see cref="BaseBlock.MinInputs"/>,
/// <see cref="BaseBlock.MaxInputs"/>, <see cref="BaseBlock.AllowableBlocks"/> — directly from
/// the block class, so the palette / add-block dialog can gate which blocks may feed a new one.
/// </summary>
/// <remarks>
/// The block classes are the single source of truth: these constraints are declared as
/// <c>virtual</c> properties on each block, so we read them straight off the type instead of
/// hand-duplicating them in a catalog (drift-free) or scraping the <c>.cs</c> source (which
/// breaks in a build that ships without source).
/// <para>
/// The properties are <em>instance</em> members, so reading them needs an object — but we must
/// NOT run the constructor (device blocks open hardware there). We allocate an uninitialised
/// instance and invoke the getter. That is sound only because every override returns a literal
/// (e.g. <c>=&gt; 2</c>, <c>=&gt; ["ClockBlock"]</c>) and never touches instance state; if a future
/// getter throws, the guarded fallback returns the BaseBlock defaults. Reading a field can instead
/// silently return its uninitialised value, so field-dependent constraint getters are not supported.
/// </para>
/// Results are cached per type.
/// </remarks>
public static class BlockConstraints
{
    /// <summary>A block type's input requirements.</summary>
    /// <param name="Min">Minimum number of input edges (0 ⇒ source block).</param>
    /// <param name="Max">Maximum number of input edges (<see cref="int.MaxValue"/> ⇒ unbounded).</param>
    /// <param name="Allowable">
    /// Class names allowed as inputs; empty ⇒ any block type is accepted.
    /// </param>
    public readonly record struct Inputs(int Min, int Max, IReadOnlyList<string> Allowable);

    /// <summary>The BaseBlock defaults, used when a type can't be reflected safely.</summary>
    private static readonly Inputs Defaults = new(1, 1, Array.Empty<string>());

    private static readonly ConcurrentDictionary<Type, Inputs> Cache = new();

    /// <summary>Input constraints for <paramref name="blockType"/> (a <see cref="BaseBlock"/> subclass).</summary>
    public static Inputs For(Type blockType) => Cache.GetOrAdd(blockType, Reflect);

    /// <summary>
    /// True when the block needs no inputs and can originate a stream.
    /// </summary>
    /// <remarks>
    /// The test is on <c>Min</c>, not <c>Max</c>: originating a stream means being able to run
    /// with nothing attached, which is not the same as refusing input altogether. A block that
    /// optionally accepts one upstream — <see cref="MOSAIC.Models.Streaming.UDPClient"/>, which
    /// is receive-only until you give it something to forward — is a legitimate source.
    /// </remarks>
    public static bool CanBeSource(Type blockType) => For(blockType).Min == 0;

    /// <summary>
    /// Whether a block of type <paramref name="blockType"/> accepts a block whose runtime class is
    /// <paramref name="candidateClassName"/> as an input (honours <see cref="BaseBlock.AllowableBlocks"/>).
    /// </summary>
    public static bool Accepts(Type blockType, string candidateClassName)
    {
        var allow = For(blockType).Allowable;
        if (allow.Count == 0) return true;
        for (int i = 0; i < allow.Count; i++)
            if (string.Equals(allow[i], candidateClassName, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static Inputs Reflect(Type t)
    {
        try
        {
            // Allocate without running the constructor (no device/hardware side effects),
            // then read the literal constraint getters off the real type.
            if (RuntimeHelpers.GetUninitializedObject(t) is not BaseBlock block)
                return Defaults;

            return new Inputs(
                block.MinInputs,
                block.MaxInputs,
                block.AllowableBlocks ?? Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Log.Warn("BlockConstraints", ex, $"Could not read input constraints for '{t.Name}'; using the defaults.");
            return Defaults;
        }
    }
}
