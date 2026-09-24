using System;
using System.Collections.Generic;
using System.Linq;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Factory;

/// <summary>
/// Builds a directed graph of interconnected blocks from a set of JSON-based model definitions.
/// Each model is instantiated via an <see cref="IBlockFactory"/> and wired together
/// according to the declared input references.
/// </summary>
public static class BlockGraphBuilder
{
    /// <summary>One block, or one connection, that a build could not honour.</summary>
    /// <param name="Key">The node key of the block the failure belongs to.</param>
    /// <param name="Type">Its declared <see cref="JsonModel.Type"/>, if any.</param>
    /// <param name="Reason">What went wrong, in the words of the exception or check that caught it.</param>
    public sealed record BuildFailure(string Key, string? Type, string Reason);

    /// <summary>
    /// The result of a graph build: every block that could be created, keyed by node name, and
    /// everything that could not.
    /// </summary>
    /// <param name="Instances">A read-only dictionary mapping node keys to their created block instances.</param>
    /// <param name="Failures">
    /// Blocks that could not be created and connections that could not be made, in build order.
    /// Empty when the whole graph loaded.
    /// </param>
    public sealed record BuiltGraph(
        IReadOnlyDictionary<string, object> Instances,
        IReadOnlyList<BuildFailure> Failures);

    /// <summary>
    /// Builds and wires a block graph from the provided model definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The build process consists of two phases:
    /// <list type="number">
    ///   <item><description>
    ///     <b>Instantiation</b> – Each <see cref="JsonModel"/> is passed to the
    ///     <paramref name="factory"/> to create a concrete block instance. If the instance
    ///     derives from <see cref="BaseBlock"/>, its input configuration is stored for
    ///     later JSON export.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Wiring</b> – For every model that declares <see cref="JsonModel.Inputs"/>,
    ///     the referenced publisher nodes are connected to the current subscriber node
    ///     via the <see cref="IPublisher"/>/<see cref="ISubscriber"/> contract.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// A block that fails to build, or an input that cannot be connected, is recorded in
    /// <see cref="BuiltGraph.Failures"/> and skipped rather than thrown, allowing valid blocks and
    /// wires to load. The failed block's name remains in downstream input lists so saving the graph
    /// preserves the unresolved reference.
    /// </para>
    /// </remarks>
    /// <param name="models">
    /// A read-only dictionary of node keys to their corresponding <see cref="JsonModel"/> definitions.
    /// </param>
    /// <param name="factory">
    /// The <see cref="IBlockFactory"/> responsible for creating block instances from model definitions.
    /// </param>
    /// <returns>
    /// A <see cref="BuiltGraph"/> with every block that could be built and connected, and the list
    /// of what could not.
    /// </returns>
    public static BuiltGraph Build(
        IReadOnlyDictionary<string, JsonModel> models,
        IBlockFactory factory)
    {
        var instances = new Dictionary<string, object>(models.Count);
        var failures = new List<BuildFailure>();
        var invalid = GraphValidation.Validate(models, factory);

        foreach (var (key, definition) in models)
        {
            if (invalid.TryGetValue(key, out var reason))
            {
                failures.Add(new BuildFailure(key, definition.Type, reason));
                Log.Error("GraphBuilder", $"'{key}': {reason}");
                continue;
            }
            try
            {
                instances[key] = factory.Create(definition);
            }
            catch (Exception ex)
            {
                Log.Error("GraphBuilder", ex, $"Creating block '{key}' of type '{definition.Type}' failed; the graph loads without it.");
                failures.Add(new BuildFailure(key, definition.Type, ex.Message));
            }
        }

        // Check every constructed instance before making any connections. Custom factories
        // may not expose type metadata, and dictionary order must not affect validation.
        foreach (var (nodeKey, model) in models)
        {
            if (!instances.TryGetValue(nodeKey, out var instance)) continue;

            if (instance is BaseBlock candidate)
            {
                var inputs = model.Inputs ?? Array.Empty<string>();
                var reason = GraphValidation.CheckInputs(inputs.Count,
                    new BlockConstraints.Inputs(candidate.MinInputs, candidate.MaxInputs, candidate.AllowableBlocks),
                    inputs.Where(instances.ContainsKey).Select(input => instances[input].GetType()));
                if (reason is not null)
                {
                    failures.Add(new BuildFailure(nodeKey, model.Type, reason));
                    candidate.Dispose();
                    instances.Remove(nodeKey);
                    continue;
                }
            }
        }

        // Keep recoverable blocks available for editing, but do not run a partial input set
        // after a constructor fails. Missing dependencies also disable their downstream chain.
        var unavailable = models.Keys.Where(key => !instances.ContainsKey(key)).ToHashSet();
        var blocked = new Dictionary<string, string>();
        bool changed;
        do
        {
            changed = false;
            foreach (var (key, model) in models)
            {
                if (unavailable.Contains(key)) continue;
                var missing = model.Inputs?.FirstOrDefault(unavailable.Contains);
                if (missing is null) continue;
                blocked[key] = $"Input '{missing}' is unavailable; this block remains disconnected.";
                changed |= unavailable.Add(key);
            }
        } while (changed);

        foreach (var (nodeKey, model) in models)
        {
            if (!instances.TryGetValue(nodeKey, out var instance)) continue;

            if (instance is BaseBlock baseBlock)
            {
                baseBlock.SetInputsFromConfig(model);
            }

            if (blocked.TryGetValue(nodeKey, out var blockedReason))
            {
                failures.Add(new BuildFailure(nodeKey, model.Type, blockedReason));
                if (instance is BaseBlock block) block.ReportError(blockedReason);
                continue;
            }

            if (model.Inputs is null || model.Inputs.Count == 0) continue;

            foreach (var sourceKey in model.Inputs)
            {
                if (!instances.TryGetValue(sourceKey, out var publisher))
                {
                    var reason = models.ContainsKey(sourceKey)
                        ? $"input '{sourceKey}' could not be built"
                        : $"input '{sourceKey}' does not exist";
                    Log.Error("GraphBuilder", $"'{nodeKey}': {reason}; the connection is skipped.");
                    failures.Add(new BuildFailure(nodeKey, model.Type, reason));
                    continue;
                }

                try
                {
                    Connect(publisher, instance);
                }
                catch (InvalidOperationException ex)
                {
                    Log.Error("GraphBuilder", ex, $"'{nodeKey}': connecting input '{sourceKey}' failed; the connection is skipped.");
                    failures.Add(new BuildFailure(nodeKey, model.Type, ex.Message));
                }
            }
        }

        Log.Info("GraphBuilder", $"Built {instances.Count} of {models.Count} block(s) with {models.Values.Sum(m => m.Inputs?.Count ?? 0)} declared edge(s); {failures.Count} failure(s).");
        return new BuiltGraph(instances, failures);
    }

    /// <summary>
    /// Connects a publisher block to a subscriber block by registering the subscriber
    /// with the publisher's notification list.
    /// </summary>
    /// <param name="publisher">The source object that must implement <see cref="IPublisher"/>.</param>
    /// <param name="subscriber">The target object that must implement <see cref="ISubscriber"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="publisher"/> does not implement <see cref="IPublisher"/>
    /// or <paramref name="subscriber"/> does not implement <see cref="ISubscriber"/>.
    /// </exception>
    private static void Connect(object publisher, object subscriber)
    {
        if (publisher is not IPublisher pub)
        {
            throw new InvalidOperationException(
                $"Publisher {publisher.GetType().Name} does not implement {nameof(IPublisher)}.");
        }

        if (subscriber is not ISubscriber sub)
        {
            throw new InvalidOperationException(
                $"Subscriber {subscriber.GetType().Name} does not implement {nameof(ISubscriber)}.");
        }

        pub.AddSubscriber(sub);
    }
}
