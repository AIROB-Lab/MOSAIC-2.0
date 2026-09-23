using System;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Parses MOSAIC pipeline configuration JSON into a read-only dictionary of block definitions.
/// </summary>
/// <remarks>
/// <para>
/// A MOSAIC configuration is expected to be a JSON object where each property name is the canonical block name
/// and each property value is a <see cref="JsonModel"/> describing that block.
/// </para>
/// <para>
/// <b>Output:</b> Parsing methods return an immutable <see cref="IReadOnlyDictionary{TKey,TValue}"/> backed by a
/// <see cref="FrozenDictionary{TKey,TValue}"/> (fast lookups, safe to share across threads).
/// </para>
/// <para>
/// <b>Validation:</b> The parser enforces a minimal contract:
/// <list type="bullet">
/// <item><description>Each block must define a non-empty <see cref="JsonModel.Type"/>.</description></item>
/// <item><description>Block names must be unique (case-insensitive).</description></item>
/// <item><description>The dictionary key is treated as the canonical block name; if <see cref="JsonModel.Name"/> is present it must match the key.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Serializer:</b> Uses System.Text.Json source generation via <see cref="MosaicJsonContext"/> for speed and
/// AOT-friendliness. The configured options allow comments, trailing commas, and numbers encoded as strings.
/// </para>
/// </remarks>
public static class JsonParser
{
    /// <summary>
    /// Gets the shared <see cref="JsonSerializerOptions"/> configured on the source-generated context.
    /// </summary>
    private static JsonSerializerOptions Options => MosaicJsonContext.Default.Options;

    /// <summary>
    /// Parses a MOSAIC config file into a dictionary mapping block name to <see cref="JsonModel"/>.
    /// </summary>
    /// <param name="filePath">Path to the JSON configuration file.</param>
    /// <param name="ct">Optional cancellation token used to cancel reading and parsing.</param>
    /// <returns>
    /// An immutable dictionary mapping canonical block names to their corresponding <see cref="JsonModel"/> instances.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="filePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown if the JSON is invalid or does not match the expected contract.</exception>
    /// <exception cref="IOException">Thrown if the file cannot be read.</exception>
    /// <remarks>
    /// <para>
    /// The file is read asynchronously using a sequential-scan file stream with a 128 KB buffer for efficient
    /// streaming of large configurations.
    /// </para>
    /// <para>
    /// JSON parsing errors are wrapped into a <see cref="FormatException"/> and, when available, include line and byte position
    /// information from the underlying <see cref="JsonException"/>.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, JsonModel>> ParseFileAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
        }
        catch (JsonException jx)
        {
            var loc = jx.LineNumber is { } ln && jx.BytePositionInLine is { } bp
                ? $" (line {ln}, byte {bp})" : string.Empty;
            Log.Error("JsonParser", jx, $"Invalid JSON in '{filePath}'{loc}.");
            throw new FormatException($"Invalid JSON in '{filePath}'{loc}: {jx.Message}", jx);
        }
        catch (IOException ioex)
        {
            throw new IOException($"Failed to read '{filePath}': {ioex.Message}", ioex);
        }
    }

    /// <summary>
    /// Parses a MOSAIC config from a stream into a dictionary mapping block name to <see cref="JsonModel"/>.
    /// </summary>
    /// <param name="stream">Input stream containing a JSON object mapping block names to models.</param>
    /// <param name="ct">Optional cancellation token used to cancel reading and parsing.</param>
    /// <returns>
    /// An immutable dictionary mapping canonical block names to their corresponding <see cref="JsonModel"/> instances.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown if the JSON is invalid or does not match the expected contract.</exception>
    /// <remarks>
    /// <para>
    /// For each entry, the dictionary key is treated as the canonical name and is copied into <see cref="JsonModel.Name"/>.
    /// </para>
    /// <para>
    /// The returned dictionary is a <see cref="FrozenDictionary{TKey,TValue}"/> to provide fast, immutable lookups.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, JsonModel>> ParseStreamAsync(
        Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var dict = await JsonSerializer.DeserializeAsync(
                       stream,
                       MosaicJsonContext.Default.DictionaryStringJsonModel,
                       ct
                   ).ConfigureAwait(false)
                   ?? throw new FormatException("Expected a JSON object mapping block names to models.");

        foreach (var (key, value) in dict)
            value.Name = key;

        ValidateDictionary(dict);
        return dict.ToFrozenDictionary();
    }

    /// <summary>
    /// Parses a MOSAIC config from a JSON string.
    /// </summary>
    /// <param name="json">JSON string containing a configuration object.</param>
    /// <returns>
    /// An immutable dictionary mapping canonical block names to their corresponding <see cref="JsonModel"/> instances.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown if the JSON is invalid or does not match the expected contract.</exception>
    /// <remarks>
    /// <para>
    /// This synchronous overload is useful for unit tests and small configurations where streaming is unnecessary.
    /// </para>
    /// <para>
    /// Unlike <see cref="ParseStreamAsync"/>, this method does not copy the dictionary key into
    /// <see cref="JsonModel.Name"/>. Callers should set names explicitly if needed.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, JsonModel> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var dict = JsonSerializer.Deserialize(
                           json,
                           MosaicJsonContext.Default.DictionaryStringJsonModel
                       ) ?? throw new FormatException("Expected a JSON object mapping block names to models.");

            ValidateDictionary(dict);
            return dict.ToFrozenDictionary();
        }
        catch (JsonException jx)
        {
            var loc = jx.LineNumber is { } ln && jx.BytePositionInLine is { } bp
                ? $" (line {ln}, byte {bp})" : string.Empty;
            Log.Error("JsonParser", jx, $"Invalid JSON{loc}.");
            throw new FormatException($"Invalid JSON{loc}: {jx.Message}", jx);
        }
    }

    /// <summary>
    /// Validates the deserialized configuration dictionary.
    /// </summary>
    /// <param name="dict">Mutable dictionary produced by System.Text.Json deserialization.</param>
    /// <exception cref="FormatException">
    /// Thrown if required fields are missing, names are inconsistent, or duplicates are found.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Validation rules:
    /// <list type="number">
    /// <item><description>Each block must provide a non-empty <see cref="JsonModel.Type"/>.</description></item>
    /// <item><description>Block names must be unique (case-insensitive check).</description></item>
    /// <item><description>If <see cref="JsonModel.Name"/> is present, it must exactly match the dictionary key.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private static void ValidateDictionary(Dictionary<string, JsonModel> dict)
    {
        foreach (var (key, model) in dict)
        {
            if (string.IsNullOrWhiteSpace(model.Type))
            {
                Log.Error("JsonParser", $"Block '{key}' is missing a non-empty 'type'.");
                throw new FormatException($"Block '{key}' is missing a non-empty 'type'.");
            }
        }

        var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, model) in dict)
        {
            var effectiveName = string.IsNullOrWhiteSpace(model.Name) ? key : model.Name!;
            if (!string.Equals(effectiveName, key, StringComparison.Ordinal))
            {
                Log.Error("JsonParser", $"Block key '{key}' has mismatching 'name'='{model.Name}'.");
                throw new FormatException(
                    $"Block key '{key}' has mismatching 'name'='{model.Name}'. " +
                    "Use the key as the canonical name, or make them match.");
            }
            if (!nameSet.Add(effectiveName))
            {
                Log.Error("JsonParser", $"Duplicate block name '{effectiveName}'. Names must be unique.");
                throw new FormatException($"Duplicate block name '{effectiveName}'. Names must be unique.");
            }
        }
    }
}

/// <summary>
/// System.Text.Json source-generation context for MOSAIC configuration types.
/// </summary>
/// <remarks>
/// <para>
/// This context enables fast, reflection-free serialization and deserialization and is suitable for
/// ahead-of-time (AOT) compilation scenarios.
/// </para>
/// <para>
/// Current options:
/// <list type="bullet">
/// <item><description>camelCase property naming</description></item>
/// <item><description>skip comments</description></item>
/// <item><description>allow trailing commas</description></item>
/// <item><description>allow numeric values encoded as strings</description></item>
/// <item><description>omit nulls when writing</description></item>
/// </list>
/// </para>
/// <para>
/// Adjust the <see cref="JsonSourceGenerationOptionsAttribute"/> on this type to control the overall JSON contract.
/// Add additional <see cref="JsonSerializableAttribute"/> entries for any new types that need serialization support.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(JsonModel))]
[JsonSerializable(typeof(Dictionary<string, JsonModel>))]
internal partial class MosaicJsonContext : JsonSerializerContext
{
}