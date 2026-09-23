using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Tests that <see cref="Joiner"/> concatenates its sources in declared input order.
/// </summary>
/// <remarks>
/// The Joiner keeps the latest value per source in a dictionary, so iterating it directly
/// concatenates in <em>first-arrival</em> order. Feeding a BodyRig, the position of a sensor's
/// quaternion in the joined vector is what selects the segment it drives — so arrival-ordered
/// output means a restart can silently swap which IMU animates which limb. Invisible with one
/// input; a coin flip with two.
/// </remarks>
[TestClass]
public class JoinerOrderingTests
{
    /// <summary>A named stand-in for an upstream block, so the Joiner sees a real source name.</summary>
    private sealed class NamedSource : BaseBlock
    {
        public NamedSource(string name) : base(name, 0) { }
        protected override void OnReceive(object sender, object value) { }
    }

    private static Joiner Build(string timerSource, params string[] declaredInputs)
    {
        var joiner = Joiner.ConfigureInput(
            new ServiceCollection().BuildServiceProvider(),
            new JsonModel
            {
                Type = "Joiner",
                Name = "quats",
                Inputs = declaredInputs,
                Params = [$"timerBlockName:{timerSource}"]
            });

        joiner.SetInputsFromConfig(new JsonModel { Type = "Joiner", Inputs = declaredInputs });
        return joiner;
    }

    private static Vector<double> Quat(double tag) =>
        Vector<double>.Build.DenseOfArray([tag, tag + 0.1, tag + 0.2, tag + 0.3]);

    /// <summary>Captures the first published value.</summary>
    private sealed class Capture : MOSAIC.Components.Interfaces.ISubscriber
    {
        private readonly System.Threading.ManualResetEventSlim _got = new(false);
        public Vector<double>? Value { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Value = value as Vector<double>;
            _got.Set();
        }

        public bool Wait(int ms) => _got.Wait(ms);
    }

    /// <summary>
    /// Feeds a value in and returns what was published, or <see langword="null"/> when the
    /// Joiner withheld output because some declared input has not spoken yet.
    /// </summary>
    private static Vector<double>? TryPublish(Joiner joiner, BaseBlock source, Vector<double> value)
    {
        var capture = new Capture();
        joiner.AddSubscriber(capture);
        try
        {
            joiner.ReceiveInput(source, value);
            return capture.Wait(250) ? capture.Value : null;
        }
        finally { joiner.RemoveSubscriber(capture); }
    }

    /// <summary>
    /// Feeds <paramref name="source"/>'s value in and returns what the Joiner publishes.
    /// The sender identity is the whole point here — the Joiner keys its store on the source
    /// block's name and only publishes when the configured timer source arrives — so the shared
    /// <c>BlockHarness</c>, which passes the block as its own sender, cannot drive it.
    /// </summary>
    private static Vector<double> Publish(Joiner joiner, BaseBlock source, Vector<double> value)
    {
        var capture = new Capture();
        joiner.AddSubscriber(capture);
        joiner.ReceiveInput(source, value);

        Assert.IsTrue(capture.Wait(2000), $"Joiner did not publish when '{source.Name}' arrived.");
        Assert.IsNotNull(capture.Value);
        return capture.Value!;
    }

    [TestMethod]
    public void ConcatenatesInDeclaredOrder_EvenWhenTheSecondSourceArrivesFirst()
    {
        // "a" is declared first but "b" publishes first. The joined vector must still lead
        // with a's quaternion, or the BodyRig binds the wrong sensor to segment 0.
        var joiner = Build(timerSource: "a", "a", "b");
        using var a = new NamedSource("a");
        using var b = new NamedSource("b");

        joiner.ReceiveInput(b, Quat(2));                      // b arrives first
        var joined = Publish(joiner, a, Quat(1)); // ...then the timer source

        Assert.HasCount(8, joined);
        Assert.AreEqual(1.0, joined[0], 1e-9, "declared-first source must lead");
        Assert.AreEqual(2.0, joined[4], 1e-9);
    }

    [TestMethod]
    public void ConcatenatesInDeclaredOrder_WhenSourcesArriveInOrder()
    {
        var joiner = Build(timerSource: "b", "a", "b");
        using var a = new NamedSource("a");
        using var b = new NamedSource("b");

        joiner.ReceiveInput(a, Quat(1));
        var joined = Publish(joiner, b, Quat(2));

        Assert.AreEqual(1.0, joined[0], 1e-9);
        Assert.AreEqual(2.0, joined[4], 1e-9);
    }

    [TestMethod]
    public void OrderIsStableAcrossRepublishes()
    {
        // The order must not drift as values are refreshed.
        var joiner = Build(timerSource: "a", "a", "b");
        using var a = new NamedSource("a");
        using var b = new NamedSource("b");

        joiner.ReceiveInput(b, Quat(2));
        Publish(joiner, a, Quat(1));

        joiner.ReceiveInput(b, Quat(5));
        var joined = Publish(joiner, a, Quat(4));

        Assert.AreEqual(4.0, joined[0], 1e-9);
        Assert.AreEqual(5.0, joined[4], 1e-9);
    }

    [TestMethod]
    public void ThreeSourcesKeepTheirDeclaredPositions()
    {
        // Matches the legacy body-rig topology: two sensors plus a feature stream.
        var joiner = Build(timerSource: "c", "a", "b", "c");
        using var a = new NamedSource("a");
        using var b = new NamedSource("b");
        using var c = new NamedSource("c");

        joiner.ReceiveInput(c, Quat(3));   // deliberately reversed arrival
        joiner.ReceiveInput(b, Quat(2));
        joiner.ReceiveInput(a, Quat(1));

        var joined = Publish(joiner, c, Quat(3));

        Assert.HasCount(12, joined);
        Assert.AreEqual(1.0, joined[0], 1e-9, "a");
        Assert.AreEqual(2.0, joined[4], 1e-9, "b");
        Assert.AreEqual(3.0, joined[8], 1e-9, "c");
    }

    [TestMethod]
    public void AbsentSources_WithholdOutput_RatherThanShiftLaterOnesIntoEarlierSlots()
    {
        // A source that has never published contributes no group, so the vector would come out
        // short and everything declared after it would slide into a slot belonging to someone
        // else. Downstream of a BodyRig that is not cosmetic: a segment is bound to a POSITION
        // in the joined vector, so a single node failing to power on re-maps every later node
        // onto the wrong limb — silently, while the rig reports itself healthy. It happened on
        // a live seven-probe rig: the torso was driven by an upper-arm IMU.
        //
        // So the Joiner publishes complete records only. Once every source has spoken once its
        // width is known, and from then on a quiet source stands in with its last value, which
        // is what keeps slot k pinned to declared input k for the rest of the run.
        var joiner = Build(timerSource: "b", "a", "b", "c");
        using var a = new NamedSource("a");
        using var b = new NamedSource("b");
        using var c = new NamedSource("c");

        Assert.IsNull(TryPublish(joiner, b, Quat(2)), "'a' and 'c' have never spoken");
        CollectionAssert.AreEqual(new[] { "a", "c" }, joiner.PendingInputs.ToArray(),
            "and it names the ones it is waiting for");

        joiner.ReceiveInput(a, Quat(1));
        Assert.IsNull(TryPublish(joiner, b, Quat(2)), "'c' is still missing");

        joiner.ReceiveInput(c, Quat(3));
        var joined = Publish(joiner, b, Quat(2));

        Assert.HasCount(12, joined, "one group per declared input");
        Assert.AreEqual(1.0, joined[0], 1e-9, "a in slot 0");
        Assert.AreEqual(2.0, joined[4], 1e-9, "b in slot 1");
        Assert.AreEqual(3.0, joined[8], 1e-9, "c in slot 2");
        Assert.IsEmpty(joiner.PendingInputs);
    }

    [TestMethod]
    public void WithNoDeclaredInputs_StillPublishesEverythingItHas()
    {
        // A hand-built Joiner has no config to read; fall back to arrival order rather
        // than dropping data.
        var joiner = new Joiner("quats", 0, "a");
        using var a = new NamedSource("a");
        using var b = new NamedSource("b");

        joiner.ReceiveInput(b, Quat(2));
        var joined = Publish(joiner, a, Quat(1));

        Assert.HasCount(8, joined);
    }
}
