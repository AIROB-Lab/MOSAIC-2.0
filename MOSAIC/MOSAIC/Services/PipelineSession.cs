using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Services;

/// <summary>
/// Owns all blocks independently of their visible cards or groups. UI callers serialize edits.
/// Reload, deletion and shutdown use the same asynchronous resource-release path.
/// </summary>
public sealed class PipelineSession : IAsyncDisposable
{
    private readonly HashSet<BaseBlock> _blocks = new();
    public IReadOnlyCollection<BaseBlock> Blocks => _blocks.ToArray();

    public void Add(BaseBlock block) => _blocks.Add(block);

    /// <summary>Releases old sockets and recordings before constructing their replacements.</summary>
    public async Task<BlockGraphBuilder.BuiltGraph> ReplaceAsync(
        IReadOnlyDictionary<string, JsonModel> models, IBlockFactory factory)
    {
        await ClearAsync();
        var graph = BlockGraphBuilder.Build(models, factory);
        foreach (var block in graph.Instances.Values.OfType<BaseBlock>())
            Add(block);
        return graph;
    }

    public async Task RemoveAsync(IEnumerable<BaseBlock> blocks)
    {
        var removed = blocks.Where(_blocks.Contains).Distinct().ToArray();
        // Stop every affected receiver before waiting: feedback connections must not keep shutdown alive.
        var stopping = removed.Select(block => block.StopProcessingAsync()).ToArray();
        foreach (var producer in _blocks)
            foreach (var consumer in removed)
                producer.RemoveSubscriber(consumer);

        await Task.WhenAll(stopping);
        try
        {
            // WhenAll attempts every block even when one device fails to release its resources.
            await Task.WhenAll(removed.Select(DisposeBlockAsync));
        }
        finally
        {
            _blocks.ExceptWith(removed);
        }
    }

    private static async Task DisposeBlockAsync(BaseBlock block) => await block.DisposeAsync();

    public Task ClearAsync() => RemoveAsync(_blocks.ToArray());

    public ValueTask DisposeAsync() => new(ClearAsync());
}
