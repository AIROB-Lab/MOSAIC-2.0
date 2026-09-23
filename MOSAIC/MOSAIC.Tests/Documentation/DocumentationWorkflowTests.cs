using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.Streaming;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.Tests.Documentation;

[TestClass]
[DoNotParallelize]
public class DocumentationWorkflowTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Documentation", "examples")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository documentation not found.");
        }
    }

    private sealed class Graph : IDisposable
    {
        public BlockGraphBuilder.BuiltGraph Built { get; }
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public Graph(string example)
        {
            var json = File.ReadAllText(Path.Combine(Root, "Documentation", "examples", example + ".json"));
            var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
            Built = BlockGraphBuilder.Build(models, new BlockFactory(_services));
            Assert.AreEqual(0, Built.Failures.Count, "The downloadable graph must load completely.");
        }
        public T Get<T>(string name) where T : BaseBlock => (T)Built.Instances[name];
        public void Dispose()
        {
            foreach (var block in Built.Instances.Values) (block as IDisposable)?.Dispose();
            _services.Dispose();
        }
    }

    private sealed class Capture : ISubscriber
    {
        public readonly ConcurrentQueue<object> Values = new();
        public void ReceiveInput(object sender, object value) => Values.Enqueue(value);
        public void Wait(int count) => Assert.IsTrue(SpinWait.SpinUntil(() => Values.Count >= count, 10000),
            $"Expected {count} outputs; got {Values.Count}.");
    }

    [TestMethod]
    public void SignalLab_ProducesDocumentedShapesFeatureAndSpectrum()
    {
        using var graph = new Graph("signal-lab");
        var signal = graph.Get<SinGenerator>("Signal");
        var window = graph.Get<SlidingWindow>("Window");
        var mav = graph.Get<MeanAverageValue>("MAV");
        var fft = graph.Get<FFT>("Spectrum");
        var raw = new Capture(); var windows = new Capture(); var features = new Capture(); var spectra = new Capture();
        signal.AddSubscriber(raw); window.AddSubscriber(windows); mav.AddSubscriber(features); fft.AddSubscriber(spectra);
        for (int n = 0; n < 4096; n++) signal.ReceiveInput(graph.Get<ClockBlock>("Clock"), n / 256.0);
        raw.Wait(4096); windows.Wait(64); features.Wait(64); spectra.Wait(64);
        Assert.AreEqual(64, windows.Values.Count);
        Assert.IsTrue(raw.Values.All(v => v is Vector<double> x && x.Count == 1));
        var first = (Matrix<double>)windows.Values.First();
        Assert.AreEqual(256, first.RowCount);
        Assert.IsTrue(first.Column(0).Take(255).All(x => x == 0), "Startup window is padded.");
        Assert.AreEqual(256.0, window.SignalRate, 1e-9);
        Assert.AreEqual(4.0, window.DesiredRate, 1e-9);
        var last = (Vector<double>)features.Values.Last();
        Assert.AreEqual(0.6346, last[0], 0.01, "Settled MAV of the filtered amplitude-1 sine.");
        Assert.AreEqual(4.0, mav.Viz!.EffectiveSignalRate, 1e-9);
        var spectrum = (Matrix<double>)spectra.Values.Last();
        Assert.AreEqual(129, spectrum.RowCount);
        Assert.AreEqual(1, spectrum.ColumnCount);
        int peak = Enumerable.Range(0,129).MaxBy(i => spectrum[i,0]);
        Assert.AreEqual(8, peak);
        Assert.AreEqual(1.0, fft.FrequencyResolution, 1e-9);
        Assert.AreEqual(1.0, spectrum[8,0], 0.02);
    }

    [TestMethod]
    public void DownloadableRms_ComputesFeaturesWithoutMutatingInput()
    {
        using var source = new SlidingWindow("source",256,64,desiredRate:256);
        using var rms = new MOSAIC.Documentation.Examples.WindowRms("rms");
        source.AddSubscriber(rms);
        var input=Matrix<double>.Build.DenseOfArray(new double[,]{{3,0},{4,2}});
        var snapshot=input.Clone();
        var result=new Capture(); rms.AddSubscriber(result);
        rms.ReceiveInput(source,input);
        result.Wait(1);
        var feature=(Vector<double>)result.Values.First();
        Assert.AreEqual(Math.Sqrt(12.5),feature[0],1e-12);
        Assert.AreEqual(Math.Sqrt(2),feature[1],1e-12);
        Assert.IsTrue(input.Equals(snapshot));
        Assert.AreEqual(4.0,rms.SignalRate,1e-9);
        Assert.AreEqual(0.0,rms.Viz.SignalRate,1e-9,"No redundant explicit scope rate is needed.");
        Assert.AreEqual(4.0,rms.Viz.EffectiveSignalRate,1e-9);
        rms.ReceiveInput(source,Vector<double>.Build.Dense(2));
        Assert.AreEqual(1, result.Values.Count, "Unsupported input must not publish a result.");

        rms.ReceiveInput(source,Matrix<double>.Build.Dense(1,1,double.NaN));
        Assert.AreEqual(1,result.Values.Count);
        source.DesiredRate = 8;
        rms.ReceiveInput(source, input);
        result.Wait(2);
        Assert.AreEqual(8.0, rms.SignalRate, 1e-9);
        Assert.AreEqual(8.0, rms.Viz.EffectiveSignalRate, 1e-9,
            "The derived plot rate must follow a changed feature publication rate.");
    }

    [TestMethod]
    public void DownloadableGain_TransformsAndRoundTripsItsAppliedSetting()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        // Parse actual JSON so ConfigureInput sees the same parameter representation as a saved graph.
        var entry = JsonParser.Parse("""
            {"Amplify":{"Type":"gainblock","Inputs":["Signal"],"Params":[2.5]}}
            """)["Amplify"] with { Name = "Amplify" };
        using var gain = GainBlock.ConfigureInput(services, entry);
        gain.SetInputsFromConfig(entry);
        using var source = new Negate("source", 100);
        source.AddSubscriber(gain);
        var results = new Capture();
        gain.AddSubscriber(results);
        var input = Vector<double>.Build.DenseOfArray(new[] { 1d, -2d, 0d });
        gain.ReceiveInput(source, input);
        results.Wait(1);
        CollectionAssert.AreEqual(new[] { 2.5, -5, 0 }, ((Vector<double>)results.Values.First()).ToArray());
        CollectionAssert.AreEqual(new[] { 1d, -2d, 0d }, input.ToArray());
        Assert.AreEqual(100d, gain.DesiredRate);
        var constraints = BlockConstraints.For(typeof(GainBlock));
        Assert.AreEqual(1, constraints.Min);
        Assert.AreEqual(1, constraints.Max);
        Assert.AreEqual(0, constraints.Allowable.Count);

        gain.ReceiveInput(source, Matrix<double>.Build.Dense(1, 1));
        Assert.AreEqual(1, results.Values.Count);
        gain.SetGain(-1);
        gain.ReceiveInput(source, input);
        results.Wait(2);

        CollectionAssert.AreEqual(new[] { -1d, 2d, 0d }, ((Vector<double>)results.Values.Last()).ToArray());

        var exported = gain.ToJsonModel();
        using var reloaded = GainBlock.ConfigureInput(services, exported);
        Assert.AreEqual("gainblock", exported.Type);
        Assert.AreEqual("Signal", exported.Inputs![0]);
        Assert.AreEqual(-1d, reloaded.Gain);
        using var defaults = GainBlock.ConfigureInput(services, new JsonModel { Type = "gainblock" });
        Assert.AreEqual(1d, defaults.Gain);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => gain.SetGain(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => gain.SetGain(double.PositiveInfinity));
        Assert.AreEqual(-1d, gain.Gain, "Rejected settings must leave the applied gain unchanged.");
    }

    [TestMethod]
    public void BeginnerGainPipeline_LoadsAndDisplaysTheTransformedSignal()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var json = File.ReadAllText(Path.Combine(Root, "Documentation", "examples", "developer", "gain-pipeline.json"));
        var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
        var graph = BlockGraphBuilder.Build(models, new TeachingFactory(services));
        try
        {
            Assert.AreEqual(0, graph.Failures.Count);
            var source = (SinGenerator)graph.Instances["Signal"];
            var check = (Function)graph.Instances["Check"];
            var gain = (GainBlock)graph.Instances["Amplify"];
            var results = new Capture();
            check.AddSubscriber(results);
            for (int n = 0; n < 100; n++) source.ReceiveInput(graph.Instances["Clock"], n / 100d);
            results.Wait(100);
            var values = results.Values.Cast<Vector<double>>().Select(v => v[0]).ToArray();
            Assert.AreEqual(2.5, values.Max(), 1e-9);
            Assert.AreEqual(-2.5, values.Min(), 1e-9);
            gain.SetGain(0);
            source.ReceiveInput(graph.Instances["Clock"], 1d);
            results.Wait(101);
            Assert.AreEqual(0d, ((Vector<double>)results.Values.Last())[0]);
        }
        finally
        {
            foreach (var block in graph.Instances.Values) (block as IDisposable)?.Dispose();
        }
    }

    [TestMethod]
    public void GainCard_AppliesOnlyValidDraftsAndSavesTheAppliedSetting()
    {
        using var block = new GainBlock("gain", gain: 2.5);
        var vm = new MOSAIC.ViewModels.SignalProcessing.GainViewModel(block);
        Assert.AreSame(block, vm.Block);
        Assert.AreEqual(2.5, vm.Gain);

        vm.Gain = -3;
        Assert.AreEqual(2.5, block.Gain, "Editing the card must not immediately change processing.");
        Assert.AreEqual(2.5, block.ToJsonModel().Params![0]);
        vm.ApplyCommand.Execute(null);
        Assert.AreEqual(-3d, block.Gain);

        vm.Gain = double.NaN;
        vm.ApplyCommand.Execute(null);
        Assert.AreEqual(-3d, block.Gain);
        StringAssert.Contains(vm.Message, "finite");

        using var services = new ServiceCollection().BuildServiceProvider();
        using var reloaded = GainBlock.ConfigureInput(services, block.ToJsonModel());
        Assert.AreEqual(-3d, reloaded.Gain);
    }

    // Models the one factory case students add; the production palette remains unchanged.
    private sealed class TeachingFactory(IServiceProvider services) : IBlockFactory
    {
        private readonly BlockFactory _standard = new(services);
        public object Create(JsonModel model) => model.Type.Trim().ToLowerInvariant() == "gainblock"
            ? GainBlock.ConfigureInput(services, model) : _standard.Create(model);
        public T Create<T>(JsonModel model) where T : class => (T)Create(model);
    }

    [TestMethod]
    public void VisualizationTemplate_UsesPayloadAndSharesThePipelineModel()
    {
        using var source = new Negate("source", 100);
        using var block = new MOSAIC.Models.TemplateViz();
        source.AddSubscriber(block);
        var vm = new MOSAIC.ViewModels.TemplateViewModel(block);
        Assert.AreSame(block, vm.Block);
        var results = new Capture();
        block.AddSubscriber(results);
        var input = Vector<double>.Build.DenseOfArray(new[] { 1d, -2d });
        block.ReceiveInput(source, input);
        results.Wait(1);
        var output = (Vector<double>)results.Values.First();
        Assert.AreNotSame(input, output);
        CollectionAssert.AreEqual(input.ToArray(), output.ToArray());
        Assert.AreEqual(100d, block.Viz.EffectiveSignalRate);
        block.ReceiveInput(source, "unsupported");
        Assert.AreEqual(1, results.Values.Count);

        block.Dispose();
        block.Dispose();
    }

    [TestMethod]
    public void LearningLab_CapturesTwoLabelsAndClassifiesNewFeatures()
    {
        using var graph = new Graph("learning-lab");
        var labels = graph.Get<Trigger>("Labels");
        var buffer = graph.Get<TriggerBuffer>("Training");
        var mav = graph.Get<MeanAverageValue>("MAV");
        var classifier = graph.Get<ClassifierBlock>("Classifier");
        // Drive capture deterministically through the real public receive path, without UI timers.
        foreach (var (label, center) in new[] { ("Low",0.6346), ("High",1.9038) })
        {
            labels.SelectAction(label);
            buffer.ReceiveInput(labels, labels.CurrentActionVector!);
            for(int i=0;i<20;i++) buffer.ReceiveInput(mav, Vector<double>.Build.DenseOfArray(new[]{center+(i%3-1)*0.005}));
            buffer.ReceiveInput(labels, null!);
            int expected = label=="Low" ? 20 : 40;
            Assert.IsTrue(SpinWait.SpinUntil(() => classifier.SampleCount == expected && classifier.IsTrained, 5000));
        }
        Assert.AreEqual(2,buffer.dB.Count);
        Assert.AreEqual("Low",classifier.ClassLabels[0]);
        Assert.AreEqual("High",classifier.ClassLabels[1]);
        var predictions=new Capture(); classifier.AddSubscriber(predictions);
        classifier.ReceiveInput(mav,Vector<double>.Build.DenseOfArray(new[]{0.65}));
        predictions.Wait(1);
        classifier.ReceiveInput(mav,Vector<double>.Build.DenseOfArray(new[]{1.88}));
        predictions.Wait(2);
        var values=predictions.Values.Cast<Vector<double>>().ToArray();
        Assert.IsTrue(values[0][0]>values[0][1]);
        Assert.IsTrue(values[1][1]>values[1][0]);
        Assert.AreEqual(40,classifier.SampleCount,"Prediction must not add training data.");
        using var fresh = new Graph("learning-lab");
        Assert.IsFalse(fresh.Get<ClassifierBlock>("Classifier").IsTrained);
        Assert.AreEqual(0,fresh.Get<TriggerBuffer>("Training").dB.Count);
    }

    [TestMethod]
    public async Task CsvMatrix_HasOneTimestampAndAnIndexPerRow()
    {
        var folder=Path.Combine(Root,"tmp","documentation-validation");
        Directory.CreateDirectory(folder);
        var name="matrix-"+Guid.NewGuid().ToString("N");
        var matrix=Matrix<double>.Build.DenseOfArray(new double[,]{{1,2},{3,4}});
        await using(var dumper=new CsvDumper(folder,name))
        {
            dumper.Enqueue(123.5,matrix);
            await dumper.FlushAsync();
        }
        var path=Path.Combine(folder,name+".csv");
        CollectionAssert.AreEqual(new[]{"123.5,0,1,2","123.5,1,3,4"},File.ReadAllLines(path));
        File.Delete(path);
    }

    [TestMethod]
    public void BatchedSource_PreservesSamplesAndPublishesCorrectWindowRate()
    {
        using var graph=new Graph("batched-signal");
        var source=graph.Get<SinGenerator>("Signal");
        var packet=new Capture(); source.AddSubscriber(packet);
        for(int i=0;i<16;i++) source.ReceiveInput(graph.Get<ClockBlock>("Clock"),0d);
        packet.Wait(16);
        var samples=packet.Values.Cast<Matrix<double>>().ToArray();
        Assert.IsTrue(samples.All(p=>p.RowCount==4 && p.ColumnCount==1));
        Assert.AreEqual(256.0,source.SignalRate,1e-9);
        Assert.AreEqual(64.0,source.DesiredRate,1e-9);
        using var window=new SlidingWindow("probe",256,64);
        source.AddSubscriber(window);
        var output=new Capture(); window.AddSubscriber(output);
        for(int i=0;i<16;i++) window.ReceiveInput(source,samples[i]);
        output.Wait(1);
        // Four samples per 64 Hz packet give 256 samples/s and four stride-64 windows/s.
        Assert.AreEqual(4.0,window.DesiredRate,1e-9,
            "Window metadata must use the rate of sample rows, not packet arrivals.");
        using var scope=new ScopeMonitor();
        scope.UpdateSignalRate(2000);
        var interval=(TimeSpan)typeof(ScopeMonitor).GetProperty("SampleAcceptInterval",
            System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(scope)!;
        Assert.IsTrue(interval.TotalSeconds>scope.SamplePeriodSeconds,
            "High-rate vector feeds have a display acceptance interval longer than their time step.");
    }
}
