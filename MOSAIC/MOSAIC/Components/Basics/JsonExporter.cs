using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Serializes MOSAIC pipeline configurations to JSON files.
/// </summary>
/// <remarks>
/// <para>
/// This class provides the inverse operation of <see cref="JsonParser"/> — it takes a collection
/// of blocks and serializes them back to the standard JSON configuration format.
/// </para>
/// <para>
/// All blocks that inherit from <see cref="BaseBlock"/> automatically support JSON export via
/// <see cref="IJsonExportable"/>. Derived blocks override <see cref="BaseBlock.GetJsonParams"/>
/// to provide their specific parameters.
/// </para>
/// <para>
/// Blocks that do not implement <see cref="IJsonExportable"/> are silently skipped during export.
/// </para>
/// <para>
/// <b>Property ordering:</b> The exported JSON for each block follows a fixed property order:
/// <c>Type</c>, <c>DesiredRate</c>, <c>Inputs</c>, <c>Params</c>, <c>Path</c>.
/// <c>SchemaVersion</c> and <c>Name</c> are excluded because the block name serves as the dictionary key.
/// </para>
/// </remarks>
public static class JsonExporter
{
    /// <summary>
    /// Shared serializer options used for all JSON export operations.
    /// </summary>
    /// <remarks>
    /// Configured with indented output, null-omission, no property name policy override,
    /// and a <see cref="JsonStringEnumConverter"/> for human-readable enum values.
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serializes a collection of blocks to a JSON configuration file.
    /// </summary>
    /// <param name="blocks">
    /// The block instances to export. Only blocks implementing <see cref="IJsonExportable"/> are included.
    /// </param>
    /// <param name="filePath">Destination file path. The file is overwritten if it already exists.</param>
    /// <param name="ct">Optional cancellation token forwarded to the file write operation.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="blocks"/> or <paramref name="filePath"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="IOException">Thrown if the file cannot be written.</exception>
    public static async Task SaveToFileAsync(
        IEnumerable<object> blocks,
        string filePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(filePath);

        var dict = BuildDictionary(blocks);

        var json = JsonSerializer.Serialize(dict, WriteOptions);

        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);

        Log.Info("JsonExporter", $"Saved {dict.Count} blocks to '{filePath}'");
    }

    /// <summary>
    /// Serializes a collection of blocks to a JSON string.
    /// </summary>
    /// <param name="blocks">
    /// The block instances to export. Only blocks implementing <see cref="IJsonExportable"/> are included.
    /// </param>
    /// <returns>A JSON string representing the pipeline configuration.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="blocks"/> is <see langword="null"/>.
    /// </exception>
    public static string Serialize(IEnumerable<object> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var dict = BuildDictionary(blocks);
        return JsonSerializer.Serialize(dict, WriteOptions);
    }

    /// <summary>
    /// Builds an ordered export dictionary from a collection of blocks.
    /// </summary>
    /// <param name="blocks">The block instances to export.</param>
    /// <returns>
    /// A dictionary mapping block names to their serializable property bags, with properties ordered as:
    /// <c>Type</c>, <c>DesiredRate</c>, <c>Inputs</c>, <c>Params</c>, <c>Path</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>SchemaVersion</c> and <c>Name</c> are excluded — the block name is used as the dictionary key.
    /// </para>
    /// <para>
    /// Blocks that do not implement <see cref="IJsonExportable"/> or that have a null/empty
    /// <see cref="JsonModel.Name"/> are silently skipped.
    /// </para>
    /// </remarks>
    private static Dictionary<string, object> BuildDictionary(IEnumerable<object> blocks)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var block in blocks)
        {
            if (block is IJsonExportable exportable)
            {
                var model = exportable.ToJsonModel();
                var name = model.Name;

                if (!string.IsNullOrEmpty(name))
                {
                    var exportObj = new Dictionary<string, object?>();

                    exportObj["Type"] = model.Type;

                    if (model.DesiredRate.HasValue)
                        exportObj["DesiredRate"] = model.DesiredRate.Value;

                    if (model.Inputs is { Count: > 0 })
                        exportObj["Inputs"] = model.Inputs;

                    if (model.Params is { Count: > 0 })
                        exportObj["Params"] = model.Params;

                    if (!string.IsNullOrEmpty(model.Path))
                        exportObj["Path"] = model.Path;

                    dict[name] = exportObj;
                }
            }
        }

        return dict;
    }
}