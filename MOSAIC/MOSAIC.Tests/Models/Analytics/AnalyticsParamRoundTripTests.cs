using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models.Analytics;

namespace MOSAIC.Tests.Models.Analytics;

/// <summary>
/// Save/load round-trip tests for the five analytics blocks that build themselves from positional
/// <see cref="JsonModel.Params"/>: <see cref="OnlinePCA"/>, <see cref="SupervisedPCA"/>,
/// <see cref="OnlineLDA"/>, <see cref="OnlineICA"/> and <see cref="SupervisedICA"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>BaseBlock.GetJsonParams</c> returns <see langword="null"/> unless a block overrides it, so a
/// block that reads five hyper-parameters in <c>ConfigureInput</c> but never exports them saves as
/// <c>Params: null</c> and reloads at every default — component count, learning rate,
/// reorthonormalisation interval, minimum stable count, regularisation. That failure is silent:
/// the graph loads, the block runs, only the settings are gone.
/// </para>
/// <para>
/// Every model below is built with a value at EVERY index that differs from the
/// <c>ConfigureInput</c> default, so a dropped, duplicated or reordered param cannot pass by
/// accident. Each test also reloads through real JSON text, because that is the only leg where
/// <c>Params</c> holds <see cref="JsonElement"/> values — the form <c>TryInt</c>/<c>TryDouble</c>
/// actually meet on disk, and the form that exposes a flag written as a JSON boolean rather than
/// as 0/1.
/// </para>
/// </remarks>
[TestClass]
public class AnalyticsParamRoundTripTests
{
    /// <summary>The setting whose loss prompted this work: 3 components drives the 3-D scatter pane.</summary>
    private const int Components = 3;

    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private static JsonModel Model(string type, string name, params object[] parameters)
        => new() { Type = type, Name = name, Params = parameters };

    /// <summary>
    /// Serialises <paramref name="model"/> and reads it back, so <c>Params</c> holds the
    /// <see cref="JsonElement"/> values a saved config file produces rather than the boxed
    /// primitives <c>GetJsonParams</c> handed over in memory.
    /// </summary>
    private static JsonModel ThroughJsonText(JsonModel model)
        => JsonSerializer.Deserialize<JsonModel>(JsonSerializer.Serialize(model))!;

    /// <summary>
    /// Reads one exported param as a number, whether it arrived boxed or as a
    /// <see cref="JsonElement"/>, and fails loudly on a JSON value kind the block's own
    /// <c>TryInt</c>/<c>TryDouble</c> cannot read back.
    /// </summary>
    private static double AsNumber(object value, string what) => value switch
    {
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
        JsonElement je => throw new AssertFailedException(
            $"{what} was written as JSON {je.ValueKind}. ConfigureInput reads it with TryInt/TryDouble, " +
            "neither of which has an arm for that kind, so on reload it would silently fall back to " +
            "its default. Booleans in particular must be exported as 0/1."),
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Asserts <paramref name="actual"/> exists and matches <paramref name="expected"/> value by
    /// value. The non-null check comes first and on its own: comparing two absent lists is exactly
    /// the bug these tests exist to catch.
    /// </summary>
    private static void AssertParams(string what, IReadOnlyList<object> expected, IReadOnlyList<object>? actual)
    {
        Assert.IsNotNull(
            actual,
            $"{what} exported Params: null. GetJsonParams is not overridden, so saving the graph " +
            "drops every hyper-parameter and reloading resets the block to its defaults.");

        Assert.AreEqual(
            expected.Count,
            actual!.Count,
            $"{what} exported {actual.Count} params but ConfigureInput reads {expected.Count}.");

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.IsNotNull(actual[i], $"{what} Params[{i}] is null.");
            Assert.AreEqual(
                AsNumber(expected[i], $"{what} Params[{i}] (expected)"),
                AsNumber(actual[i], $"{what} Params[{i}]"),
                1e-12,
                $"{what} Params[{i}] is not the value ConfigureInput was given.");
        }
    }

    // ── Per-block round trips ───────────────────────────────────────────────

    [TestMethod]
    public void OnlinePCA_SaveLoad_KeepsEveryParameter()
    {
        // Defaults are k 2, eta0 0.2, reorth 100, minStable 500 — none of these repeats one.
        var expected = new object[] { Components, 0.35, 7, 42 };

        using var block = OnlinePCA.ConfigureInput(Services(), Model("OnlinePCA", "pca", expected));
        var saved = block.ToJsonModel();

        AssertParams("OnlinePCA", expected, saved.Params);

        using var reloaded = OnlinePCA.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(Components, reloaded.ComponentCount);
        AssertParams("OnlinePCA after reload", expected, reloaded.ToJsonModel().Params);
    }

    [TestMethod]
    public void SupervisedPCA_SaveLoad_KeepsEveryParameter()
    {
        // Same four params as OnlinePCA, deliberately different values so a test that accidentally
        // exercised the base class instead of the subclass would still be reading its own numbers.
        var expected = new object[] { Components, 0.45, 9, 33 };

        using var block = SupervisedPCA.ConfigureInput(Services(), Model("SupervisedPCA", "spca", expected));
        var saved = block.ToJsonModel();

        Assert.AreEqual("SupervisedPCA", saved.Type, "The subclass must not save itself as its base type.");
        AssertParams("SupervisedPCA", expected, saved.Params);

        using var reloaded = SupervisedPCA.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(Components, reloaded.ComponentCount);
        AssertParams("SupervisedPCA after reload", expected, reloaded.ToJsonModel().Params);
    }

    [TestMethod]
    public void OnlineLDA_SaveLoad_KeepsEveryParameter()
    {
        // Defaults are k 2, eta0 0.1, reorth 100, minStable 500, regularisation 1e-4.
        var expected = new object[] { Components, 0.35, 7, 42, 0.25 };

        using var block = OnlineLDA.ConfigureInput(Services(), Model("OnlineLDA", "lda", expected));
        var saved = block.ToJsonModel();

        AssertParams("OnlineLDA", expected, saved.Params);

        using var reloaded = OnlineLDA.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(Components, reloaded.ComponentCount);
        AssertParams("OnlineLDA after reload", expected, reloaded.ToJsonModel().Params);
    }

    [TestMethod]
    public void OnlineICA_SaveLoad_KeepsEveryParameter()
    {
        // Defaults are k 2, eta0 0.1, reorth 50, minStable 1000, contrast 0.0, warmup 500,
        // freezeWhitening 1, adaptiveWhitening 0, covarianceDecay 0.005.
        //
        // freeze 0 with adaptive 1 is the one combination that survives the constructor unchanged:
        // it stores freeze && !adaptive, so freeze 1 with adaptive 1 would collapse to 0 and the
        // test would be asserting against a value the block never held.
        var expected = new object[] { Components, 0.35, 7, 42, 2.0, 11, 0, 1, 0.02 };

        using var block = OnlineICA.ConfigureInput(Services(), Model("OnlineICA", "ica", expected));
        var saved = block.ToJsonModel();

        AssertParams("OnlineICA", expected, saved.Params);

        using var reloaded = OnlineICA.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(Components, reloaded.ComponentCount);
        AssertParams("OnlineICA after reload", expected, reloaded.ToJsonModel().Params);
    }

    [TestMethod]
    public void SupervisedICA_SaveLoad_KeepsEveryParameter()
    {
        var expected = new object[] { Components, 0.45, 9, 33, 1.0, 13, 0, 1, 0.03 };

        using var block = SupervisedICA.ConfigureInput(Services(), Model("SupervisedICA", "sica", expected));
        var saved = block.ToJsonModel();

        Assert.AreEqual("SupervisedICA", saved.Type, "The subclass must not save itself as its base type.");
        AssertParams("SupervisedICA", expected, saved.Params);

        using var reloaded = SupervisedICA.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(Components, reloaded.ComponentCount);
        AssertParams("SupervisedICA after reload", expected, reloaded.ToJsonModel().Params);
    }

    // ── The user-visible symptom ────────────────────────────────────────────

    [TestMethod]
    public void OnlineLDA_ThreeComponents_SurviveASaveLoadCycle()
    {
        // The report that prompted this work: set Components to 3 to get the card's 3-D scatter
        // pane, save the graph, reopen it, and the block is back to the ConfigureInput default of 2.
        using var configured = OnlineLDA.ConfigureInput(Services(), Model("OnlineLDA", "lda", Components));
        Assert.AreEqual(Components, configured.ComponentCount, "Precondition: the block starts with 3 components.");

        using var reloaded = OnlineLDA.ConfigureInput(Services(), ThroughJsonText(configured.ToJsonModel()));

        Assert.AreEqual(
            Components,
            reloaded.ComponentCount,
            "A saved OnlineLDA came back with a different component count, so the 3-D scatter pane is gone.");
    }
}
