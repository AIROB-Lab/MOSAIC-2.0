using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.Learning;

namespace MOSAIC.Tests.Components.Factory;

/// <summary>
/// Pins the one contract a saved graph depends on: everything <c>ToJsonModel</c> writes can be
/// read back by <c>BlockFactory</c> and the block's own <c>ConfigureInput</c>, and a block that
/// cannot be rebuilt costs the user that block, not the whole file.
/// </summary>
/// <remarks>
/// Each test here is a bug that shipped. Four blocks saved under a <c>JsonTypeName</c> the factory
/// had no case for; three declared parameters but never exported them while their loaders threw
/// when the parameters were missing; an empty Trigger or Stimulus exported nothing and then
/// refused to load; and <c>BlockGraphBuilder</c> built every block in one unguarded expression,
/// so any one of those took every other block and every wire down with it.
/// </remarks>
[TestClass]
public class SavedGraphReloadTests
{
    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private static JsonModel Model(string type, string name, params object[] parameters)
        => new() { Type = type, Name = name, Params = parameters.Length == 0 ? null : parameters };

    /// <summary>
    /// Serialises and reads back, so <c>Params</c> holds the <see cref="JsonElement"/> values a
    /// saved file produces rather than the boxed primitives <c>GetJsonParams</c> handed over.
    /// </summary>
    private static JsonModel ThroughJsonText(JsonModel model)
        => JsonSerializer.Deserialize<JsonModel>(JsonSerializer.Serialize(model))!;

    // ------------------------------------------------------------------ type names

    /// <summary>
    /// Every concrete block in the assembly must save under a name the factory accepts. Read
    /// without running a constructor: several device blocks spawn threads or load an SDK when
    /// built, and <c>JsonTypeName</c> is a literal on every block that overrides it.
    /// </summary>
    [TestMethod]
    public void EveryBlockType_SavesUnderANameTheFactoryAccepts()
    {
        var factory = new BlockFactory(Services());
        var unresolved = new List<string>();

        var blockTypes = typeof(BaseBlock).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(BaseBlock).IsAssignableFrom(t))
            // The Template* blocks under Models/Templates are scaffolding for writing a new block and
            // are deliberately unregistered; they sit in MOSAIC.Models, not a namespace of their own.
            .Where(t => t.Namespace != "MOSAIC.Models.Templates" && !t.Name.StartsWith("Template", StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.IsTrue(blockTypes.Count > 40, $"Only {blockTypes.Count} block types were found; the scan is not looking at the right assembly.");

        foreach (var type in blockTypes)
        {
            string savedAs;
            try
            {
                var blank = RuntimeHelpers.GetUninitializedObject(type);
                var property = type.GetProperty("JsonTypeName", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                               ?? throw new MissingMemberException(type.Name, "JsonTypeName");
                savedAs = (string)property.GetValue(blank)!;
            }
            catch (Exception ex)
            {
                unresolved.Add($"{type.Name}: JsonTypeName could not be read without a constructor ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (!factory.CanCreate(savedAs))
                unresolved.Add($"{type.Name} saves as '{savedAs}', which BlockFactory does not accept");
        }

        Assert.AreEqual(
            0,
            unresolved.Count,
            "A block that saves under a name the factory cannot load makes every graph containing it unopenable:"
            + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
    }

    [TestMethod]
    [DataRow("QuattrocentoBlock")]
    [DataRow("WulpusPython")]
    [DataRow("LslBlock")]
    public void TypeNamesThatUsedToBeUnloadable_NowResolve(string savedAs)
        => Assert.IsTrue(
            new BlockFactory(Services()).CanCreate(savedAs),
            $"'{savedAs}' is what the block writes on save; a graph holding one could not be reopened.");

    // ------------------------------------------------------------------ parameters

    [TestMethod]
    public void CommandSender_SaveLoad_KeepsHostAndPort()
    {
        using var block = CommandSender.ConfigureInput(Services(), Model("CommandSender", "cmd", "10.0.0.7", 4321));
        var saved = block.ToJsonModel();

        Assert.IsNotNull(saved.Params, "CommandSender exported Params: null, and its loader throws without them.");

        using var reloaded = CommandSender.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual("10.0.0.7", reloaded.Host);
        Assert.AreEqual(4321, reloaded.Port);
    }

    [TestMethod]
    public void Joiner_SaveLoad_KeepsTheTimerSource()
    {
        using var block = Joiner.ConfigureInput(Services(), Model("Joiner", "join", "timerBlockName:Clock1"));
        var saved = block.ToJsonModel();

        Assert.IsNotNull(saved.Params, "Joiner exported Params: null, and its loader throws without a timer.");

        using var reloaded = Joiner.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual("Clock1", reloaded.TimerSourceName);
    }

    [TestMethod]
    public void HybridPredictor_SaveLoad_KeepsEveryParameter()
    {
        // Every value differs from its ConfigureInput default, so a dropped or reordered parameter
        // cannot pass by accident. SoftmaxRFF is not the default model type.
        var model = new JsonModel
        {
            Type = "HybridPredictor",
            Name = "hp",
            Params = ["SoftmaxRFF", 12, 0.05, 0.002, 2.0, 400],
            Path = System.IO.Path.GetTempPath(),
        };

        using var block = HybridPredictorBlock.ConfigureInput(Services(), model);
        var saved = block.ToJsonModel();

        Assert.IsNotNull(saved.Params, "HybridPredictor exported Params: null, and its loader throws without them.");
        Assert.AreEqual(6, saved.Params!.Count);

        using var reloaded = HybridPredictorBlock.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual("SoftmaxRFF", reloaded.ModelType.ToString());
        Assert.AreEqual(12, reloaded.InputDim);
        Assert.AreEqual(0.05, reloaded.Config.LearningRate, 1e-12);
        Assert.AreEqual(0.002, reloaded.Config.Lambda, 1e-12);
        Assert.AreEqual(2.0, reloaded.Config.Sigma, 1e-12);
        Assert.AreEqual(400, reloaded.Config.FeatureDim);
    }

    [TestMethod]
    public void Trigger_WithNoActions_SavesAndReloads()
    {
        // The card lets the user delete every action; that state used to save as no Params and
        // then fail to load with "must have at least 1 parameter".
        using var block = Trigger.ConfigureInput(Services(), Model("Trigger", "trig"));
        var saved = block.ToJsonModel();

        using var reloaded = Trigger.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(0, reloaded.ActionCount);
    }

    [TestMethod]
    public void Stimulus_WithNoTasks_SavesAndReloadsItsCaptureDuration()
    {
        // An integer-valued duration keeps this assertion clear of the culture-sensitive parse in
        // JsonModel.GetDouble, which is a separate defect with its own fix.
        using var block = Stimulus.ConfigureInput(Services(), Model("Stimulus", "stim", 3));
        Assert.AreEqual(3.0, block.CaptureDuration);

        var saved = block.ToJsonModel();
        Assert.IsNotNull(saved.Params, "Stimulus with no tasks exported Params: null and then refused to load.");

        using var reloaded = Stimulus.ConfigureInput(Services(), ThroughJsonText(saved));

        Assert.AreEqual(3.0, reloaded.CaptureDuration);
        Assert.AreEqual(0, reloaded.ToJsonModel().Params!.Count - 1, "no tasks should have appeared from nowhere");
    }

    // ------------------------------------------------------------------ one bad block

    private sealed class StubBlock : BaseBlock
    {
        public override int MinInputs => 0;
        public override int MaxInputs => int.MaxValue;
        public StubBlock(string name) : base(name) { }

        protected override void OnReceive(object sender, object value) => Publish(value);
    }

    /// <summary>Builds a <see cref="StubBlock"/> for every model except the one it is told to refuse.</summary>
    private sealed class FailingFactory(string failingName) : IBlockFactory
    {
        public object Create(JsonModel m)
            => m.Name == failingName
                ? throw new InvalidOperationException($"'{m.Name}' cannot be built in this test.")
                : new StubBlock(m.Name!);

        public T Create<T>(JsonModel m) where T : class => (T)Create(m);
    }

    [TestMethod]
    public void Build_SkipsABlockItCannotCreate_AndReportsIt_InsteadOfLosingTheGraph()
    {
        var models = new Dictionary<string, JsonModel>
        {
            ["source"] = new() { Type = "Stub", Name = "source" },
            ["broken"] = new() { Type = "Stub", Name = "broken", Inputs = ["source"] },
            ["sink"]   = new() { Type = "Stub", Name = "sink", Inputs = ["broken", "source"] },
        };

        var graph = BlockGraphBuilder.Build(models, new FailingFactory("broken"));

        CollectionAssert.AreEquivalent(new[] { "source", "sink" }, graph.Instances.Keys.ToArray());
        Assert.IsTrue(
            graph.Failures.Any(f => f.Key == "broken"),
            "the block that could not be built must be reported");
        Assert.IsTrue(
            graph.Failures.Any(f => f.Key == "sink" && f.Reason.Contains("broken")),
            "a connection that could not be made must be reported against the block that declared it");

        foreach (var instance in graph.Instances.Values.OfType<IDisposable>()) instance.Dispose();
    }

    [TestMethod]
    public void Build_WithNothingWrong_ReportsNoFailures()
    {
        var models = new Dictionary<string, JsonModel>
        {
            ["source"] = new() { Type = "Stub", Name = "source" },
            ["sink"]   = new() { Type = "Stub", Name = "sink", Inputs = ["source"] },
        };

        var graph = BlockGraphBuilder.Build(models, new FailingFactory("nobody"));

        Assert.AreEqual(2, graph.Instances.Count);
        Assert.AreEqual(0, graph.Failures.Count);

        foreach (var instance in graph.Instances.Values.OfType<IDisposable>()) instance.Dispose();
    }
}
