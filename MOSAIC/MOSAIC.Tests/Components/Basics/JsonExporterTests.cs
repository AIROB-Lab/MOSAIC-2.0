using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Known-answer tests for <see cref="JsonExporter"/>, which serializes a collection of blocks to the
/// MOSAIC pipeline JSON format. Only objects implementing <see cref="IJsonExportable"/> participate;
/// each contributes a <see cref="JsonModel"/> via <see cref="IJsonExportable.ToJsonModel"/>, keyed in the
/// output by its <see cref="JsonModel.Name"/>.
///
/// Contract exercised here, taken directly from the source:
///   * Each block becomes one top-level property keyed by its Name; SchemaVersion and Name are NOT
///     re-emitted inside the value (the key IS the name).
///   * Per-block property order is fixed: Type, DesiredRate, Inputs, Params, Path.
///   * DesiredRate is emitted whenever it HasValue (including 0.0); Inputs/Params only when non-empty;
///     Path only when non-empty. Null/empty optional fields are omitted.
///   * Blocks with a null/empty Name, and objects that don't implement IJsonExportable, are silently skipped.
///   * A null blocks argument throws ArgumentNullException.
///
/// The JSON is asserted by parsing it back with System.Text.Json (<see cref="JsonDocument"/>), and a
/// full round-trip is verified through <see cref="JsonParser"/>. Rather than build a real block graph,
/// a tiny test-only <see cref="StubExportable"/> returns a hand-constructed model.
/// </summary>
[TestClass]
public class JsonExporterTests
{
    /// <summary>Minimal <see cref="IJsonExportable"/> that hands back a caller-supplied model.</summary>
    private sealed class StubExportable : IJsonExportable
    {
        private readonly JsonModel _model;
        public StubExportable(JsonModel model) => _model = model;
        public JsonModel ToJsonModel() => _model;
    }

    /// <summary>Builds a <see cref="JsonModel"/> with only the fields a test cares about.</summary>
    private static JsonModel Model(
        string type,
        string? name,
        double? desiredRate = null,
        IReadOnlyList<string>? inputs = null,
        IReadOnlyList<object>? @params = null,
        string? path = null)
        => new()
        {
            Type = type,
            Name = name,
            DesiredRate = desiredRate,
            Inputs = inputs,
            Params = @params,
            Path = path,
        };

    private static StubExportable Stub(JsonModel model) => new(model);

    // ---------------------------------------------------------------- minimal block

    [TestMethod]
    public void Serialize_MinimalExportable_KeyedByNameWithTypeOnly()
    {
        // A model with only Type + Name => one top-level property "Rectifier" whose value carries just
        // "Type". Name and SchemaVersion are intentionally not re-emitted inside the value.
        var blocks = new object[] { Stub(Model(type: "Blocks.Filters.Rectifier", name: "Rectifier")) };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.AreEqual(1, root.EnumerateObject().Count()); // exactly one block emitted
        Assert.IsTrue(root.TryGetProperty("Rectifier", out var block));
        Assert.AreEqual("Blocks.Filters.Rectifier", block.GetProperty("Type").GetString());
        Assert.IsFalse(block.TryGetProperty("Name", out _));          // Name excluded (it is the key)
        Assert.IsFalse(block.TryGetProperty("schemaVersion", out _)); // SchemaVersion excluded
    }

    // ---------------------------------------------------------------- all fields, order + values

    [TestMethod]
    public void Serialize_AllOptionalFieldsPresent_EmittedInFixedOrderWithValues()
    {
        // Fixed per-block order per the docs: Type, DesiredRate, Inputs, Params, Path.
        var blocks = new object[]
        {
            Stub(Model(
                type: "Blocks.Filters.Rectifier",
                name: "Rectifier",
                desiredRate: 200.0,
                inputs: new[] { "RawEmg" },
                @params: new object[] { 0.02, "double", true },
                path: "calib.bin")),
        };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var block = doc.RootElement.GetProperty("Rectifier");

        // Property order is the fixed emission order.
        var order = block.EnumerateObject().Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Type", "DesiredRate", "Inputs", "Params", "Path" }, order);

        // Values round-trip verbatim.
        Assert.AreEqual("Blocks.Filters.Rectifier", block.GetProperty("Type").GetString());
        Assert.AreEqual(200.0, block.GetProperty("DesiredRate").GetDouble(), 1e-9);

        var inputs = block.GetProperty("Inputs");
        Assert.AreEqual(1, inputs.GetArrayLength());
        Assert.AreEqual("RawEmg", inputs[0].GetString());

        var prms = block.GetProperty("Params");
        Assert.AreEqual(3, prms.GetArrayLength());
        Assert.AreEqual(0.02, prms[0].GetDouble(), 1e-9); // number stays a JSON number
        Assert.AreEqual("double", prms[1].GetString());   // string stays a JSON string
        Assert.IsTrue(prms[2].GetBoolean());              // bool stays a JSON bool

        Assert.AreEqual("calib.bin", block.GetProperty("Path").GetString());
    }

    // ---------------------------------------------------------------- omission of empty optionals

    [TestMethod]
    public void Serialize_NullOptionalFields_OmitsEverythingButType()
    {
        // DesiredRate null, Inputs/Params null, Path null => only "Type" survives.
        var blocks = new object[] { Stub(Model(type: "Blocks.Passthrough", name: "P")) };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var block = doc.RootElement.GetProperty("P");
        var names = block.EnumerateObject().Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Type" }, names);
    }

    [TestMethod]
    public void Serialize_EmptyInputsAndParams_AreOmitted()
    {
        // The source keeps Inputs/Params only when Count > 0. Empty collections are dropped, so an empty
        // Inputs and empty Params leave just "Type".
        var blocks = new object[]
        {
            Stub(Model(
                type: "Blocks.Passthrough",
                name: "P",
                inputs: Array.Empty<string>(),
                @params: Array.Empty<object>())),
        };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var block = doc.RootElement.GetProperty("P");
        Assert.IsFalse(block.TryGetProperty("Inputs", out _));
        Assert.IsFalse(block.TryGetProperty("Params", out _));
        var names = block.EnumerateObject().Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Type" }, names);
    }

    [TestMethod]
    public void Serialize_DesiredRateZero_IsEmittedNotOmitted()
    {
        // The guard is DesiredRate.HasValue (not > 0). A present 0.0 is written; only null is omitted.
        var blocks = new object[]
        {
            Stub(Model(type: "Blocks.Passthrough", name: "P", desiredRate: 0.0)),
        };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var block = doc.RootElement.GetProperty("P");
        Assert.IsTrue(block.TryGetProperty("DesiredRate", out var rate));
        Assert.AreEqual(0.0, rate.GetDouble(), 1e-9);
    }

    // ---------------------------------------------------------------- skipping rules

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Serialize_BlockWithNullOrEmptyName_IsSkipped(string? emptyName)
    {
        // A block whose model has a null/empty Name has no dictionary key and is dropped; the well-named
        // one remains as the only property.
        var blocks = new object[]
        {
            Stub(Model(type: "Blocks.Nameless", name: emptyName)),
            Stub(Model(type: "Blocks.Good", name: "Good")),
        };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.AreEqual(1, root.EnumerateObject().Count());
        Assert.IsTrue(root.TryGetProperty("Good", out _));
    }

    [TestMethod]
    public void Serialize_NonExportableObjects_AreSkipped()
    {
        // Objects that don't implement IJsonExportable are silently ignored; only the stub is exported.
        var blocks = new object[]
        {
            "not a block",
            new object(),
            Stub(Model(type: "Blocks.Good", name: "Good")),
        };

        var json = JsonExporter.Serialize(blocks);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.AreEqual(1, root.EnumerateObject().Count());
        Assert.IsTrue(root.TryGetProperty("Good", out _));
    }

    [TestMethod]
    public void Serialize_EmptyCollection_ProducesEmptyJsonObject()
    {
        // No blocks => an empty JSON object "{}".
        var json = JsonExporter.Serialize(Array.Empty<object>());

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.AreEqual(0, doc.RootElement.EnumerateObject().Count());
    }

    [TestMethod]
    public void Serialize_NullBlocks_ThrowsArgumentNullException()
    {
        // ArgumentNullException.ThrowIfNull(blocks) guards the entry point.
        Assert.ThrowsExactly<ArgumentNullException>(() => JsonExporter.Serialize(null!));
    }

    // ---------------------------------------------------------------- round trip through JsonParser

    [TestMethod]
    public void Serialize_ThenJsonParser_RoundTripsFieldValues()
    {
        // Exported JSON must be re-parseable by the inverse component. Type/DesiredRate/Inputs/Params all
        // survive the round trip. (Params come back as JsonElement, decoded via the JsonModel helpers.)
        var blocks = new object[]
        {
            Stub(Model(
                type: "Blocks.Filters.Rectifier",
                name: "Rectifier",
                desiredRate: 200.0,
                inputs: new[] { "RawEmg" },
                @params: new object[] { 0.02, "double", true })),
        };

        var json = JsonExporter.Serialize(blocks);
        var parsed = JsonParser.Parse(json);

        Assert.AreEqual(1, parsed.Count);
        Assert.IsTrue(parsed.TryGetValue("Rectifier", out var model));
        Assert.AreEqual("Blocks.Filters.Rectifier", model!.Type);
        Assert.AreEqual(200.0, model.DesiredRate!.Value, 1e-9);

        Assert.IsNotNull(model.Inputs);
        Assert.AreEqual(1, model.Inputs!.Count);
        Assert.AreEqual("RawEmg", model.Inputs[0]);

        Assert.IsNotNull(model.Params);
        Assert.AreEqual(3, model.Params!.Count);
        Assert.AreEqual(0.02, JsonModel.GetDouble(model.Params[0]), 1e-9);
        Assert.AreEqual("double", JsonModel.GetString(model.Params[1]));
        Assert.IsTrue(JsonModel.GetBool(model.Params[2]));
    }

    // ---------------------------------------------------------------- file writer

    [TestMethod]
    public async Task SaveToFileAsync_WritesSameContentAsSerialize()
    {
        // SaveToFileAsync writes exactly what Serialize returns (same shared WriteOptions). Verify the file
        // exists and its text equals the in-memory serialization, then confirm it parses back.
        var blocks = new object[]
        {
            Stub(Model(type: "Blocks.Good", name: "Good", desiredRate: 100.0)),
        };
        var expected = JsonExporter.Serialize(blocks);
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mosaic_export_{Guid.NewGuid():N}.json");

        try
        {
            await JsonExporter.SaveToFileAsync(blocks, path);

            Assert.IsTrue(File.Exists(path));
            var onDisk = await File.ReadAllTextAsync(path);
            Assert.AreEqual(expected, onDisk);

            var parsed = JsonParser.Parse(onDisk);
            Assert.AreEqual(1, parsed.Count);
            Assert.IsTrue(parsed.ContainsKey("Good"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public async Task SaveToFileAsync_NullFilePath_ThrowsArgumentNullException()
    {
        // filePath is guarded by ArgumentNullException.ThrowIfNull.
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => JsonExporter.SaveToFileAsync(Array.Empty<object>(), null!));
    }
}
