using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Known-answer and validation tests for <see cref="JsonParser"/>.
///
/// The parser turns a JSON object — whose property names are canonical block names and whose values are
/// <see cref="JsonModel"/> definitions — into an immutable dictionary keyed by block name.
///
/// Contract exercised here, read directly from the source:
/// <list type="bullet">
///   <item><description><see cref="JsonParser.Parse"/> deserializes then runs <c>ValidateDictionary</c>; it does
///   NOT copy the dictionary key into <see cref="JsonModel.Name"/> (only the stream/file overloads do).</description></item>
///   <item><description>A missing <c>Type</c> property is rejected by <c>[JsonRequired]</c> during deserialization,
///   surfacing as a wrapped <see cref="FormatException"/> in the synchronous overload.</description></item>
///   <item><description>A present-but-empty <c>Type</c>, a <c>Name</c> that disagrees with the key, or a
///   case-insensitive duplicate name are rejected by <c>ValidateDictionary</c> with a <see cref="FormatException"/>.</description></item>
///   <item><description>The <c>[JsonPropertyName]</c> attributes on <see cref="JsonModel"/> pin the JSON keys to
///   PascalCase (<c>Type</c>, <c>Inputs</c>, <c>DesiredRate</c>, <c>Name</c>), overriding the camelCase policy.</description></item>
/// </list>
/// </summary>
[TestClass]
public class JsonParserTests
{
    [TestMethod]
    public void Parse_ValidSingleBlock_ReturnsModelWithParsedFields()
    {
        // One block keyed "Rectifier"; Name is present and matches the key, so it is preserved as-is.
        // Expected: dict has exactly that key, Type/Name round-trip, Inputs=["RawEmg"], DesiredRate=200.
        var json = @"{
            ""Rectifier"": {
                ""Type"": ""Blocks.Filters.Rectifier"",
                ""Name"": ""Rectifier"",
                ""Inputs"": [ ""RawEmg"" ],
                ""DesiredRate"": 200
            }
        }";

        var result = JsonParser.Parse(json);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result.ContainsKey("Rectifier"));
        var model = result["Rectifier"];
        Assert.AreEqual("Blocks.Filters.Rectifier", model.Type);
        Assert.AreEqual("Rectifier", model.Name);
        Assert.IsNotNull(model.Inputs);
        Assert.AreEqual(1, model.Inputs!.Count);
        Assert.AreEqual("RawEmg", model.Inputs![0]);
        Assert.IsNotNull(model.DesiredRate);
        Assert.AreEqual(200.0, model.DesiredRate!.Value, 1e-9); // exact integer-valued rate
    }

    [TestMethod]
    public void Parse_MultipleBlocks_ParsesAllTypesAndLeavesNamesNull()
    {
        // Two blocks, neither carrying a "Name". Parse (unlike the stream overload) does NOT copy the key
        // into Name, so both Names stay null while the keys and Types are parsed intact.
        var json = @"{
            ""RawEmg"":    { ""Type"": ""Blocks.Sources.Emg"" },
            ""Rectifier"": { ""Type"": ""Blocks.Filters.Rectifier"", ""Inputs"": [ ""RawEmg"" ] }
        }";

        var result = JsonParser.Parse(json);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("Blocks.Sources.Emg", result["RawEmg"].Type);
        Assert.AreEqual("Blocks.Filters.Rectifier", result["Rectifier"].Type);
        Assert.IsNull(result["RawEmg"].Name);    // key is NOT copied into Name by the sync overload
        Assert.IsNull(result["Rectifier"].Name);
    }

    [TestMethod]
    public void Parse_NullArgument_ThrowsArgumentNullException()
    {
        // ArgumentNullException.ThrowIfNull(json) guards the entry point before any parsing.
        Assert.ThrowsExactly<ArgumentNullException>(() => JsonParser.Parse(null!));
    }

    [TestMethod]
    public void Parse_MalformedJson_ThrowsFormatException()
    {
        // The outer object is never closed => System.Text.Json raises a JsonException, which the sync
        // overload catches and re-wraps as a FormatException.
        var json = @"{ ""A"": { ""Type"": ""T"" }";

        Assert.ThrowsExactly<FormatException>(() => JsonParser.Parse(json));
    }

    [TestMethod]
    public void Parse_JsonNullLiteral_ThrowsFormatException()
    {
        // Deserializing the literal "null" yields a null dictionary => the "?? throw" produces a
        // FormatException ("Expected a JSON object mapping block names to models.").
        Assert.ThrowsExactly<FormatException>(() => JsonParser.Parse("null"));
    }

    [TestMethod]
    public void Parse_MissingTypeProperty_ThrowsFormatException()
    {
        // "Type" is [JsonRequired] and a required member; its absence makes deserialization throw a
        // JsonException, re-wrapped as FormatException by the sync overload.
        var json = @"{ ""A"": { ""Inputs"": [ ""x"" ] } }";

        Assert.ThrowsExactly<FormatException>(() => JsonParser.Parse(json));
    }

    [TestMethod]
    public void Parse_EmptyTypeString_ThrowsFormatException()
    {
        // Type is present (so [JsonRequired] is satisfied) but empty; ValidateDictionary rejects it via
        // string.IsNullOrWhiteSpace => FormatException ("missing a non-empty 'type'").
        var json = @"{ ""A"": { ""Type"": """" } }";

        Assert.ThrowsExactly<FormatException>(() => JsonParser.Parse(json));
    }

    [TestMethod]
    public void Parse_NameMismatchesKey_ThrowsFormatException()
    {
        // Key "A" but Name "B": effectiveName ("B") != key ("A") under Ordinal comparison =>
        // ValidateDictionary throws FormatException (mismatching 'name').
        var json = @"{ ""A"": { ""Type"": ""T"", ""Name"": ""B"" } }";

        Assert.ThrowsExactly<FormatException>(() => JsonParser.Parse(json));
    }

    [TestMethod]
    public void Parse_CaseInsensitiveDuplicateNames_ThrowsFormatException()
    {
        // "Foo" and "foo" are distinct (case-sensitive) dictionary keys, so both survive deserialization,
        // but the uniqueness check uses an OrdinalIgnoreCase set => the second insert collides =>
        // FormatException ("Duplicate block name").
        var json = @"{
            ""Foo"": { ""Type"": ""T1"" },
            ""foo"": { ""Type"": ""T2"" }
        }";

        Assert.ThrowsExactly<FormatException>(() => JsonParser.Parse(json));
    }

    [TestMethod]
    public async Task ParseStreamAsync_CopiesKeyIntoName()
    {
        // The stream overload copies each dictionary key into JsonModel.Name (the distinguishing behavior
        // vs. the synchronous Parse). Input block has no "Name" => after parsing, Name equals the key.
        var json = @"{ ""RawEmg"": { ""Type"": ""Blocks.Sources.Emg"" } }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await JsonParser.ParseStreamAsync(stream);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("RawEmg", result["RawEmg"].Name); // key copied into Name by the stream overload
        Assert.AreEqual("Blocks.Sources.Emg", result["RawEmg"].Type);
    }
}
