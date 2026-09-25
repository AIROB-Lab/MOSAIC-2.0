using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Custom JSON converter that preserves <see cref="JsonElement"/> instances for mixed-type arrays.
/// </summary>
/// <remarks>
/// <para>
/// This converter is applied to <see cref="JsonModel.Params"/> to support heterogeneous parameter lists
/// (numbers, strings, booleans, nested objects, etc.) without losing type information during deserialization.
/// </para>
/// <para>
/// On read, each array element is parsed as a <see cref="JsonElement"/> and stored in the list.
/// On write, <see cref="JsonElement"/> values are written directly; all other types fall back to
/// <see cref="JsonSerializer.Serialize(Utf8JsonWriter, object?, Type, JsonSerializerOptions)"/>.
/// </para>
/// </remarks>
public class ObjectListConverter : JsonConverter<IReadOnlyList<object>?>
{
    /// <summary>
    /// Reads a JSON array and deserializes each element as a <see cref="JsonElement"/>.
    /// </summary>
    /// <param name="reader">The UTF-8 JSON reader to consume tokens from.</param>
    /// <param name="typeToConvert">The target type (always <see cref="IReadOnlyList{Object}"/>).</param>
    /// <param name="options">Serializer options forwarded by the framework.</param>
    /// <returns>
    /// A list of <see cref="JsonElement"/> values, or <see langword="null"/> if the JSON token is <c>null</c>.
    /// </returns>
    /// <exception cref="JsonException">
    /// Thrown when the current token is neither <see cref="JsonTokenType.Null"/> nor <see cref="JsonTokenType.StartArray"/>.
    /// </exception>
    public override IReadOnlyList<object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected array but got {reader.TokenType}");

        var list = new List<object>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            var element = JsonElement.ParseValue(ref reader);
            list.Add(element);
        }

        return list;
    }

    /// <summary>
    /// Writes a list of objects as a JSON array, preserving <see cref="JsonElement"/> values natively.
    /// </summary>
    /// <param name="writer">The UTF-8 JSON writer to emit tokens to.</param>
    /// <param name="value">The list to serialize, or <see langword="null"/> to emit a JSON <c>null</c>.</param>
    /// <param name="options">Serializer options forwarded by the framework.</param>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<object>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (item is JsonElement je)
            {
                je.WriteTo(writer);
            }
            else
            {
                JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(object), options);
            }
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Represents the JSON configuration model for a single MOSAIC pipeline component (block).
/// </summary>
/// <remarks>
/// <para>
/// A MOSAIC configuration typically consists of a dictionary where each entry maps a block name (key)
/// to a <see cref="JsonModel"/> instance (value). The dictionary key is treated as the canonical block
/// identifier in the pipeline graph (e.g., used to connect inputs).
/// </para>
/// <para>
/// <b>Serialization:</b> Properties are annotated with <see cref="JsonPropertyNameAttribute"/> to define the
/// JSON contract. Optional properties are omitted from JSON output when they are <see langword="null"/>.
/// </para>
/// <para>
/// <b>Validation:</b> This type supports lightweight validation via <see cref="Validate"/>. Some fields also
/// include <see cref="System.ComponentModel.DataAnnotations"/> attributes (e.g., <see cref="MinLengthAttribute"/> and
/// <see cref="RangeAttribute"/>) that can be used by external validation frameworks.
/// </para>
/// <para>
/// <b>Parameters:</b> The <see cref="Params"/> collection is intentionally untyped (<see cref="object"/>)
/// to support flexible block-specific arguments. Block factories should interpret and validate
/// parameters based on <see cref="Type"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// {
///   "Rectifier": {
///     "Type": "Blocks.Filters.Rectifier",
///     "Inputs": [ "RawEmg" ],
///     "Params": [ 0.02, "double", true ],
///     "DesiredRate": 200,
///     "Path": null
///   }
/// }
/// </code>
/// </example>
public sealed record JsonModel
{
    /// <summary>
    /// Gets the schema version for this JSON contract.
    /// </summary>
    /// <remarks>
    /// Can be used to support migrations as the configuration format evolves.
    /// </remarks>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Gets the component type identifier used to construct the block (e.g., "Blocks.Timers.ScheduledTimer").
    /// </summary>
    /// <remarks>
    /// This value is required and is typically resolved to a concrete runtime type by a factory/DI container.
    /// </remarks>
    [JsonPropertyName("Type")]
    [JsonRequired]
    [MinLength(1)]
    public required string Type { get; init; }

    /// <summary>
    /// Gets the list of input block names/ids, if any.
    /// </summary>
    /// <remarks>
    /// Each entry should correspond to the key/name of another configured block that publishes values
    /// consumed by this component.
    /// </remarks>
    [JsonPropertyName("Inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Inputs { get; init; }

    /// <summary>
    /// Gets the block-specific parameter list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The semantics of this collection depend on <see cref="Type"/>. Values are stored as <see cref="object"/>
    /// to support heterogeneous parameter sets.
    /// </para>
    /// <para>
    /// When deserialized, values will be <see cref="JsonElement"/> instances. Use the static helper methods
    /// (<see cref="GetString"/>, <see cref="GetInt"/>, <see cref="GetDouble"/>, <see cref="GetBool"/>)
    /// or check <see cref="JsonElement.ValueKind"/> to extract the actual values.
    /// </para>
    /// </remarks>
    [JsonPropertyName("Params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(ObjectListConverter))]
    public IReadOnlyList<object>? Params { get; init; }

    /// <summary>
    /// Gets the desired update/output rate in Hertz.
    /// </summary>
    /// <remarks>
    /// This value is optional. When present, it is typically used as a reference for runtime monitoring
    /// and status classification (e.g., idle/lagging detection).
    /// </remarks>
    [JsonPropertyName("DesiredRate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Range(0, double.MaxValue)]
    public double? DesiredRate { get; init; }

    /// <summary>
    /// Gets or sets the human-readable name of the component for logging or UI display.
    /// </summary>
    /// <remarks>
    /// In many configurations, the dictionary key acts as the canonical name. Some loaders may copy that key
    /// into this property to keep the model self-contained.
    /// </remarks>
    [JsonPropertyName("Name")]
    [MinLength(1)]
    public string? Name { get; set; }

    /// <summary>
    /// Gets an optional file path or URI associated with the component.
    /// </summary>
    /// <remarks>
    /// Used for blocks that require external resources (e.g., calibration data, models, or media files).
    /// </remarks>
    [JsonPropertyName("Path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>
    /// Performs lightweight runtime validation after deserialization.
    /// </summary>
    /// <exception cref="ValidationException">
    /// Thrown if <see cref="Type"/> is null/empty or if <see cref="DesiredRate"/> is negative.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Type))
            throw new ValidationException("Type is required.");
        if (DesiredRate is < 0)
            throw new ValidationException("DesiredRate must be >= 0.");
    }

    #region Static Helper Methods for Parameter Extraction

    /// <summary>
    /// Extracts a <see cref="string"/> from a parameter object, handling <see cref="JsonElement"/> transparently.
    /// </summary>
    /// <param name="obj">The parameter value, typically a <see cref="JsonElement"/> from <see cref="Params"/>.</param>
    /// <param name="defaultValue">Value returned when <paramref name="obj"/> is null, undefined, or not convertible.</param>
    /// <returns>The extracted string, or <paramref name="defaultValue"/> if extraction fails.</returns>
    /// <remarks>
    /// Numbers and booleans stored as <see cref="JsonElement"/> are converted to their string representation.
    /// </remarks>
    public static string? GetString(object? obj, string? defaultValue = null) => obj switch
    {
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? defaultValue,
            JsonValueKind.Number => je.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => defaultValue,
            JsonValueKind.Undefined => defaultValue,
            _ => je.ToString()
        },
        string s => string.IsNullOrWhiteSpace(s) ? defaultValue : s,
        null => defaultValue,
        _ => obj.ToString() ?? defaultValue
    };

    /// <summary>
    /// Extracts an <see cref="int"/> from a parameter object, handling <see cref="JsonElement"/> transparently.
    /// </summary>
    /// <param name="obj">The parameter value, typically a <see cref="JsonElement"/> from <see cref="Params"/>.</param>
    /// <param name="defaultValue">Value returned when <paramref name="obj"/> is null or not convertible.</param>
    /// <returns>The extracted integer, or <paramref name="defaultValue"/> if extraction fails.</returns>
    /// <remarks>
    /// Floating-point values are truncated (cast to <see cref="int"/>). String values are parsed via
    /// <see cref="int.TryParse(string?, out int)"/>.
    /// </remarks>
    public static int GetInt(object? obj, int defaultValue = 0) => obj switch
    {
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.Number => je.TryGetInt32(out var i) ? i : (int)je.GetDouble(),
            JsonValueKind.String => int.TryParse(je.GetString(), out var i) ? i : defaultValue,
            _ => defaultValue
        },
        int i => i,
        long l => (int)l,
        double d => (int)d,
        float f => (int)f,
        string s => int.TryParse(s, out var i) ? i : defaultValue,
        null => defaultValue,
        _ => defaultValue
    };

    /// <summary>
    /// Extracts a <see cref="double"/> from a parameter object, handling <see cref="JsonElement"/> transparently.
    /// </summary>
    /// <param name="obj">The parameter value, typically a <see cref="JsonElement"/> from <see cref="Params"/>.</param>
    /// <param name="defaultValue">Value returned when <paramref name="obj"/> is null or not convertible.</param>
    /// <returns>The extracted double, or <paramref name="defaultValue"/> if extraction fails.</returns>
    /// <remarks>
    /// Integer and float values are widened to <see cref="double"/>. String values use invariant
    /// JSON number formatting first, then the current culture as a fallback for user-entered values.
    /// </remarks>
    public static double GetDouble(object? obj, double defaultValue = 0.0) => obj switch
    {
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.Number => je.GetDouble(),
            JsonValueKind.String => TryParseDouble(je.GetString(), out var d) ? d : defaultValue,
            _ => defaultValue
        },
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        string s => TryParseDouble(s, out var d) ? d : defaultValue,
        null => defaultValue,
        _ => defaultValue
    };

    private static bool TryParseDouble(string? value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
           || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);

    /// <summary>
    /// Extracts a <see cref="bool"/> from a parameter object, handling <see cref="JsonElement"/> transparently.
    /// </summary>
    /// <param name="obj">The parameter value, typically a <see cref="JsonElement"/> from <see cref="Params"/>.</param>
    /// <param name="defaultValue">Value returned when <paramref name="obj"/> is null or not convertible.</param>
    /// <returns>The extracted boolean, or <paramref name="defaultValue"/> if extraction fails.</returns>
    /// <remarks>
    /// Numeric values are treated as truthy when non-zero. String values are parsed via
    /// <see cref="bool.TryParse(string?, out bool)"/>.
    /// </remarks>
    public static bool GetBool(object? obj, bool defaultValue = false) => obj switch
    {
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(je.GetString(), out var b) ? b : defaultValue,
            JsonValueKind.Number => je.GetInt32() != 0,
            _ => defaultValue
        },
        bool b => b,
        int i => i != 0,
        string s => bool.TryParse(s, out var b) ? b : defaultValue,
        null => defaultValue,
        _ => defaultValue
    };

    #endregion
}
