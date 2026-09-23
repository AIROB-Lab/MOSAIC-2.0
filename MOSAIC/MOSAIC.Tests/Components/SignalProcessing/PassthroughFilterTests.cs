using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.SignalProcessing;

namespace MOSAIC.Tests.Components.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="PassthroughFilter"/>: a stateless no-op filter.
/// Contract: ProcessSample(x) == x, ProcessSamples(a) returns the SAME array instance
/// with identical contents, and Reset() is an unobservable no-op (no internal state).
/// </summary>
[TestClass]
public class PassthroughFilterTests
{
    [DataTestMethod]
    [DataRow(0.0)]
    [DataRow(3.5)]
    [DataRow(-2.25)]
    [DataRow(1e12)]
    public void ProcessSample_AnyValue_ReturnsInputUnchanged(double sample)
    {
        // Pass-through: output equals input exactly, for any real value.
        var filter = new PassthroughFilter();

        double result = filter.ProcessSample(sample);

        Assert.AreEqual(sample, result, 1e-9);
    }

    [TestMethod]
    public void ProcessSamples_MultipleValues_ReturnsElementwiseEqualContents()
    {
        // Each element passes through unmodified => output content equals input content.
        var filter = new PassthroughFilter();
        var samples = new[] { 1.0, -3.0, 4.5, 0.0 };

        double[] result = filter.ProcessSamples(samples);

        Assert.AreEqual(4, result.Length);      // length preserved before element checks
        Assert.AreEqual(1.0, result[0], 1e-9);
        Assert.AreEqual(-3.0, result[1], 1e-9);
        Assert.AreEqual(4.5, result[2], 1e-9);
        Assert.AreEqual(0.0, result[3], 1e-9);
    }

    [TestMethod]
    public void ProcessSamples_ReturnsSameArrayInstance_NoCopyMade()
    {
        // Documented behaviour: the original array instance is returned (no copy).
        var filter = new PassthroughFilter();
        var samples = new[] { 7.0, 8.0, 9.0 };

        double[] result = filter.ProcessSamples(samples);

        Assert.AreSame(samples, result);
    }

    [TestMethod]
    public void ProcessSamples_EmptyArray_ReturnsEmptyArray()
    {
        // Edge case: an empty window has no samples to transform => empty result.
        var filter = new PassthroughFilter();
        var samples = new double[0];

        double[] result = filter.ProcessSamples(samples);

        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void Reset_ThenProcessSample_StillReturnsInputUnchanged()
    {
        // Reset holds no state, so behaviour after Reset is identical to before.
        var filter = new PassthroughFilter();
        double before = filter.ProcessSample(2.0);   // = 2.0

        filter.Reset();
        double after = filter.ProcessSample(2.0);     // still = 2.0

        Assert.AreEqual(2.0, before, 1e-9);
        Assert.AreEqual(2.0, after, 1e-9);
    }
}
