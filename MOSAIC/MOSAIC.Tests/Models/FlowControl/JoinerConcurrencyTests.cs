using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Stress tests for <see cref="Joiner"/> under the concurrency it actually runs at.
/// </summary>
/// <remarks>
/// <para>
/// Every declared input is published by its own block, and every block owns its own
/// <c>OutputDispatcher</c> with its own pump thread. So a Joiner with seven inputs is written by
/// seven threads at once — on the live BodyRig rig, roughly 1,440 writes a second. Its store was
/// a plain <see cref="Dictionary{TKey,TValue}"/> with no synchronisation.
/// </para>
/// <para>
/// Two failure modes are being guarded here, and neither announces itself:
/// concurrent first-time adds can corrupt a Dictionary's bucket chain, which manifests as a hang
/// inside a resize rather than an exception; and <c>ConcatenateLatest</c> walks the sources twice
/// — once to sum lengths, once to fill — so a writer landing between the passes can make the
/// second disagree with the first.
/// </para>
/// <para>
/// The threads are released from a common gate so their first writes land together. That is the
/// window where the keys are still being created, which is when a Dictionary is most fragile.
/// </para>
/// </remarks>
[TestClass]
public class JoinerConcurrencyTests
{
    /// <summary>Eight keys is past a Dictionary's initial capacity, so warmup forces a resize.</summary>
    private const int Sources = 8;

    private const int Iterations = 4000;

    private sealed class NamedSource(string name) : BaseBlock(name, 0)
    {
        protected override void OnReceive(object sender, object value) { }
    }

    /// <summary>Records the width of everything published, and anything that went wrong.</summary>
    private sealed class WidthWatcher : ISubscriber
    {
        private readonly ManualResetEventSlim _first = new(false);
        private int _count;
        public readonly ConcurrentBag<int> Widths = [];

        public int Count => Volatile.Read(ref _count);

        public bool WaitForFirst(int ms) => _first.Wait(ms);

        public void ReceiveInput(object sender, object value)
        {
            Interlocked.Increment(ref _count);
            if (value is Vector<double> v) Widths.Add(v.Count);
            _first.Set();
        }
    }

    /// <summary>
    /// Waits for the dispatcher to deliver, and then for it to go quiet.
    /// </summary>
    /// <remarks>
    /// <see cref="BaseBlock.Publish"/> hands the value to a per-subscriber channel drained by a
    /// pump task, so delivery is asynchronous and outlives the producer threads. Asserting the
    /// moment they return raced that pump and intermittently observed nothing at all — which
    /// says something about the test, not about the Joiner.
    /// </remarks>
    private static void WaitForDelivery(WidthWatcher watcher)
    {
        Assert.IsTrue(watcher.WaitForFirst(10_000),
            "the pacer published, but the dispatcher never delivered anything");

        // Then let the backlog drain, so the width assertions see the whole set.
        int last = -1;
        for (int i = 0; i < 40 && last != watcher.Count; i++)
        {
            last = watcher.Count;
            Thread.Sleep(25);
        }
    }

    private static Vector<double> Quat(double tag) =>
        Vector<double>.Build.DenseOfArray([tag, tag + 0.1, tag + 0.2, tag + 0.3]);

    /// <summary>
    /// Drives <paramref name="names"/> concurrently, one thread each, all released together.
    /// </summary>
    private static ConcurrentBag<Exception> Hammer(Joiner joiner, IReadOnlyList<string> names)
    {
        var faults = new ConcurrentBag<Exception>();
        using var gate = new ManualResetEventSlim(false);

        var sources = names.Select(n => new NamedSource(n)).ToArray();
        var threads = sources.Select((src, i) => Task.Factory.StartNew(() =>
        {
            try
            {
                gate.Wait();
                for (int k = 0; k < Iterations; k++)
                    joiner.ReceiveInput(src, Quat(i + 1));
            }
            catch (Exception ex) { faults.Add(ex); }
        }, TaskCreationOptions.LongRunning)).ToArray();

        gate.Set();

        // A corrupted Dictionary hangs inside a resize rather than throwing, so a timeout is the
        // only way that failure ever becomes visible.
        Assert.IsTrue(Task.WaitAll(threads, TimeSpan.FromSeconds(60)),
            "producers did not finish — a corrupted dictionary spins rather than throwing");

        foreach (var src in sources) src.Dispose();
        return faults;
    }

    [TestMethod]
    public void ConcurrentWriters_NeverCorruptTheStore_NorTearASnapshot()
    {
        var names = Enumerable.Range(0, Sources).Select(i => $"src{i}").ToArray();
        using var joiner = new Joiner("quats", 0, names[0]) { Inputs = names };

        var watcher = new WidthWatcher();
        joiner.AddSubscriber(watcher);

        var faults = Hammer(joiner, names);

        Assert.IsEmpty(faults, faults.FirstOrDefault()?.ToString() ?? "");
        WaitForDelivery(watcher);
        Assert.IsGreaterThan(0, watcher.Count, "the pacer should have published");

        // The load-bearing assertion. Every record must carry one group per declared input; a
        // short or long one means the two passes of ConcatenateLatest saw different worlds.
        var widths = watcher.Widths.Distinct().OrderBy(w => w).ToArray();
        CollectionAssert.AreEqual(new[] { Sources * 4 }, widths,
            $"every published record must be {Sources * 4} wide; saw {string.Join(", ", widths)}");
    }

    [TestMethod]
    public void AnUndeclaredSourceArrivingConcurrently_DoesNotRunOffTheEndOfTheBuffer()
    {
        // OrderedLatest emits declared inputs first, then anything else it has heard from. An
        // undeclared source appearing between the sizing pass and the filling pass is the case
        // that could write past the destination array.
        var declared = Enumerable.Range(0, Sources).Select(i => $"src{i}").ToArray();
        using var joiner = new Joiner("quats", 0, declared[0]) { Inputs = declared };

        var watcher = new WidthWatcher();
        joiner.AddSubscriber(watcher);

        var withStrangers = declared.Concat(["stranger1", "stranger2"]).ToArray();
        var faults = Hammer(joiner, withStrangers);

        Assert.IsEmpty(faults, faults.FirstOrDefault()?.ToString() ?? "");
        WaitForDelivery(watcher);

        // Width may legitimately grow as the undeclared sources are first heard, so the shape is
        // what matters: always whole groups, never narrower than the declared set.
        foreach (var w in watcher.Widths)
        {
            Assert.AreEqual(0, w % 4, $"published width {w} is not a whole number of groups");
            Assert.IsGreaterThanOrEqualTo(Sources * 4, w, "a record narrower than the declared inputs");
        }
    }

    [TestMethod]
    public void PendingInputs_IsSafeToReadWhileWritersRun()
    {
        // The card polls this from the UI thread while the pumps are writing.
        var names = Enumerable.Range(0, Sources).Select(i => $"src{i}").ToArray();
        using var joiner = new Joiner("quats", 0, names[0]) { Inputs = names };

        using var stop = new CancellationTokenSource();
        var readerFaults = new ConcurrentBag<Exception>();
        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = joiner.IsWaitingForInputs;
                    foreach (var _ in joiner.PendingInputs) { }
                }
            }
            catch (Exception ex) { readerFaults.Add(ex); }
        });

        var faults = Hammer(joiner, names);
        stop.Cancel();
        reader.Wait(TimeSpan.FromSeconds(10));

        Assert.IsEmpty(faults, faults.FirstOrDefault()?.ToString() ?? "");
        Assert.IsEmpty(readerFaults, readerFaults.FirstOrDefault()?.ToString() ?? "");
        Assert.IsEmpty(joiner.PendingInputs, "every source spoke, so nothing is pending");
    }
}
