using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.Streaming;

namespace MOSAIC.Tests.Components.Factory;

[TestClass]
public class GraphValidationTests
{
    private sealed class Source : BaseBlock
    {
        public Source() : base("source") { }
        public override int MinInputs => 0;
        protected override void OnReceive(object sender, object value) { }
    }

    private sealed class Factory : IBlockFactory
    {
        public readonly List<string> Constructed = new();
        public Type? GetBlockType(string type) => type switch
        {
            "source" => typeof(Source), "negate" => typeof(Negate), "sin" => typeof(SinGenerator), _ => null
        };
        public object Create(JsonModel model)
        {
            Constructed.Add(model.Name!);
            return model.Type == "source" ? new Source() : new Negate(model.Name!, 0);
        }
        public T Create<T>(JsonModel model) where T : class => (T)Create(model);
    }

    [TestMethod]
    [DataRow("negate", "", "Requires")]
    [DataRow("negate", "a,b", "Requires")]
    [DataRow("negate", "a,a", "Duplicate")]
    [DataRow("negate", "missing", "does not exist")]
    [DataRow("sin", "a", "not allowed")]
    public void InvalidInputs_AreReportedBeforeConstructorRuns(string type, string input, string reason)
    {
        var factory = new Factory();
        var graph = BlockGraphBuilder.Build(new Dictionary<string, JsonModel>
        {
            ["a"] = new() { Type = "source", Name = "a" },
            ["b"] = new() { Type = "source", Name = "b" },
            ["bad"] = new() { Type = type, Name = "bad", Inputs = input.Length == 0 ? [] : input.Split(',') },
            ["dependent"] = new() { Type = "negate", Name = "dependent", Inputs = ["bad"] }
        }, factory);
        try
        {
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, factory.Constructed);
            StringAssert.Contains(graph.Failures.Single(f => f.Key == "bad").Reason, reason);
            Assert.IsTrue(graph.Failures.Any(f => f.Key == "dependent"));
        }
        finally { foreach (var block in graph.Instances.Values.Cast<IDisposable>()) block.Dispose(); }
    }

    [TestMethod]
    public void FeedbackCycle_IsRejectedBeforeEitherBlockIsConstructed()
    {
        var factory = new Factory();
        var graph = BlockGraphBuilder.Build(new Dictionary<string, JsonModel>
        {
            ["a"] = new() { Type = "negate", Name = "a", Inputs = ["b"] },
            ["b"] = new() { Type = "negate", Name = "b", Inputs = ["a"] }
        }, factory);
        Assert.IsEmpty(factory.Constructed);
        Assert.HasCount(2, graph.Failures);
        Assert.IsTrue(graph.Failures.All(f => f.Reason.Contains("cycles")));
    }
}
