using System.Diagnostics;
using System.Text.Json;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Visualization;

// Deliberately dependency-free and hardware-free. Run Release outside a debugger.
// Measures processing/recording; it does not pretend to measure visible chart frame time.
BlockVisualization.Enabled = false;
var results = new List<object>();
foreach (int packetSize in new[] { 1, 8, 64 })
    await Measure($"window-{packetSize}-rows-per-callback", () => Window(packetSize));
await Measure("recording-8-channels", Recording);
await Measure("dispatch-latency", Dispatch);
string output = args.Length > 0 ? args[0] : "performance.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
    configuration = "Release, plots off, no hardware; medians of 7 runs after at least 1 second warmup per case; details from final run", results
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(Path.GetFullPath(output));

async Task Measure(string name, Func<Task<object>> run)
{
    // One invocation is too short for tiered compilation/PGO, especially for numeric
    // formatting on the writer task. Measure sustained operation after a timed warmup.
    var warmup = Stopwatch.StartNew();
    do { await run(); } while (warmup.Elapsed < TimeSpan.FromSeconds(1));
    var timings = new List<double>(); var allocations = new List<long>(); var cpu = new List<double>();
    object? details = null;
    for (int i = 0; i < 7; i++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess(); var startCpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        details = await run();
        timings.Add(watch.Elapsed.TotalMilliseconds);
        cpu.Add((process.TotalProcessorTime - startCpu).TotalMilliseconds);
        allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
    }
    timings.Sort(); allocations.Sort(); cpu.Sort();
    results.Add(new { name, elapsedMs = timings[3], allocatedBytes = allocations[3], cpuMs = cpu[3], details });
    Console.WriteLine($"{name}: {timings[3]:F2} ms, {allocations[3]:N0} bytes");
}

static async Task<object> Window(int packetSize)
{
    const int samples = 64000, channels = 8;
    using var source = new Probe(1024.0 / packetSize) { SignalRate = 1024 };
    await using var window = new SlidingWindow("window", 256, 64, SlidingWindow.WindowType.Hamming);
    source.AddSubscriber(window);
    object input = packetSize == 1 ? Vector<double>.Build.Dense(channels, 0.5)
        : Matrix<double>.Build.Dense(packetSize, channels, 0.5);
    int rateNotifications = 0;
    window.PropertyChanged += (_, e) => { if (e.PropertyName == "DesiredRate") rateNotifications++; };
    for (int i = 0; i < samples / packetSize; i++) window.ReceiveInput(source, input);
    return new { samples, channels, packetSize, rateNotifications };
}

static async Task<object> Recording()
{
    string directory = Path.Combine(Path.GetTempPath(), "mosaic-bench-" + Guid.NewGuid().ToString("N"));
    const int samples = 16000;
    try
    {
        await using var recorder = new CsvDumper(directory, "samples", queueCapacity: samples);
        var input = Vector<double>.Build.Dense(8, i => Math.Sin(i));
        var producer = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++) recorder.Enqueue(i / 1000d, input);
        double producerMs = producer.Elapsed.TotalMilliseconds;
        int backlog = recorder.QueuedCommands;
        await recorder.FlushAsync();
        await recorder.DisposeAsync();
        if (recorder.LastError is not null || recorder.DroppedRows != 0) throw new Exception("Benchmark lost recording data.");
        return new { samples, producerMs, backlog, droppedRows = recorder.DroppedRows,
            writtenRows = File.ReadLines(Path.Combine(directory, "samples.csv")).Count() };
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task<object> Dispatch()
{
    const int count = 4000;
    await using var dispatcher = new OutputDispatcher();
    var sink = new LatencySink(); dispatcher.AddSubscriber(sink);
    for (int i = 0; i < count; i++) dispatcher.Dispatch(sink, Stopwatch.GetTimestamp());
    int backlog = dispatcher.QueuedValues;
    await dispatcher.DisposeAsync();
    sink.Latencies.Sort();
    if (sink.Latencies.Count != count || dispatcher.RejectedValues != 0) throw new Exception("Benchmark lost dispatch data.");
    return new { count, backlog, p95LatencyMs = sink.Latencies[(int)(count * 0.95)] };
}

sealed class Probe(double rate) : BaseBlock("source", rate)
{
    protected override void OnReceive(object sender, object value) { }
}
sealed class LatencySink : ISubscriber
{
    public List<double> Latencies { get; } = new();
    public void ReceiveInput(object sender, object value) => Latencies.Add(Stopwatch.GetElapsedTime((long)value).TotalMilliseconds);
}
