using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="Resampler"/>: a streaming sample-and-hold resampler.
///
/// Resampling uses sample time: the first row publishes at t=0. These cases provide
/// ten input samples per second and request one output sample per second, so a packet
/// shorter than one second publishes only its first row regardless of processing speed.
///
/// Published type is a MathNet <c>DenseVector</c> (an <see cref="Vector{T}"/>), NOT a Matrix.
/// </summary>
[TestClass]
public class ResamplerTests
{
    [TestMethod]
    public void OnReceive_MatrixInput_PublishesFirstRowAsVector()
    {
        // First sample always lands on the initial deadline => row 0 is published verbatim.
        // Row 0 = [1, 2, 3] over 3 channels, so Count == 3 and values match element-for-element.
        using var block = new Resampler(name: "R", desiredRate: 10, targetRate: 1.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
            { 7.0, 8.0, 9.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count); // channel count preserved = input column count
        Assert.AreEqual(1.0, result[0], 1e-9);
        Assert.AreEqual(2.0, result[1], 1e-9);
        Assert.AreEqual(3.0, result[2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_SingleChannelColumn_PreservesChannelCountAndValue()
    {
        // Single-channel input (1 column). Row 0 = [42] => published vector is [42], Count == 1.
        using var block = new Resampler(name: "R", desiredRate: 10, targetRate: 1.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 42.0 },
            { 43.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(42.0, result[0], 1e-9); // zero-order hold: exact value, no interpolation
    }

    [TestMethod]
    public void OnReceive_VectorInput_PublishesSameVector()
    {
        // The Vector<double> path feeds ProcessSample directly; the first (only) sample publishes.
        // Input [10, -20] => published [10, -20] with Count == 2.
        using var block = new Resampler(name: "R", desiredRate: 10, targetRate: 1.0);
        var input = Vector<double>.Build.Dense(new[] { 10.0, -20.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(10.0, result[0], 1e-9);
        Assert.AreEqual(-20.0, result[1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ConstantRamp_PublishedRowMatchesInputRowExactly()
    {
        // A linear ramp: row 0 = [0, 100]. Sample-and-hold copies values verbatim (no filtering),
        // so the published vector equals row 0 exactly.
        using var block = new Resampler(name: "R", desiredRate: 10, targetRate: 1.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0, 100.0 },
            { 1.0, 101.0 },
            { 2.0, 102.0 },
            { 3.0, 103.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
        Assert.AreEqual(100.0, result[1], 1e-9);
    }

    [TestMethod]
    public void Constructor_SetsDesiredRateToTargetRate()
    {
        // The constructor overrides DesiredRate with targetRate so downstream blocks inherit it.
        using var block = new Resampler(name: "R", desiredRate: 500, targetRate: 100);

        Assert.AreEqual(100.0, block.DesiredRate, 1e-9);
        Assert.AreEqual(100.0, block.TargetRate, 1e-9);
    }

    [TestMethod]
    public void ResampleRatio_ComputedFromInputAndTargetRate()
    {
        // ResampleRatio = TargetRate / InputRate when InputRate > 0, else 0.
        // Fresh block: InputRate == 0 => ratio == 0 (guarded divide).
        using var block = new Resampler(name: "R", desiredRate: 0, targetRate: 100);

        Assert.AreEqual(0.0, block.ResampleRatio, 1e-9); // InputRate is 0 on a fresh block

        block.InputRate = 400; // ratio = 100 / 400 = 0.25
        Assert.AreEqual(0.25, block.ResampleRatio, 1e-9);
    }

    [TestMethod]
    public void OnTargetRateChanged_UpdatesDesiredRate()
    {
        // Setting TargetRate to a positive value updates DesiredRate via the observable callback.
        using var block = new Resampler(name: "R", desiredRate: 0, targetRate: 100);

        block.TargetRate = 250;

        Assert.AreEqual(250.0, block.DesiredRate, 1e-9);
        Assert.AreEqual(250.0, block.TargetRate, 1e-9);
    }

    [TestMethod]
    public void Constructor_NonPositiveTargetRate_Throws()
    {
        // Guard: targetRate <= 0 => ArgumentOutOfRangeException (MSTest 4.0: ThrowsExactly).
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new Resampler(name: "R", desiredRate: 0, targetRate: 0.0));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new Resampler(name: "R", desiredRate: 0, targetRate: -5.0));
    }
}
