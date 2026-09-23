using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer / invariant tests for <see cref="Filter"/>, a per-channel online
/// digital filter block.
///
/// A <see cref="Matrix{T}"/> input is treated as [timesteps (rows) × channels (columns)];
/// each column is filtered independently by its own <see cref="MOSAIC.Components.SignalProcessing.IOnlineFilter"/>,
/// and the block publishes a <see cref="Matrix{T}"/> of identical shape.
///
/// With <c>desiredRate: 0</c> and no propagated SignalRate, <see cref="Filter.SampleRate"/>
/// falls back to 1000 Hz (SignalRate &gt; 0 ? SignalRate : DesiredRate &gt; 0 ? DesiredRate : 1000).
///
/// Design facts used to hand-derive the expected values:
///  * FIR lowpass coefficients are normalized to unity DC gain (Σ b[k] = 1), so a constant
///    DC input, once the length-N delay line is full, yields output = DC · Σ b[k] = DC.
///  * FIR highpass = δ − lowpass, so Σ b[k] = 1 − 1 = 0, and a constant DC input, once the
///    delay line is full, yields output = DC · 0 = 0 (DC is fully rejected).
///  * IIR Butterworth lowpass is normalized for unity gain at DC, so a constant DC input
///    converges to DC once the transient settles.
///  * When IsEnabled is false the block is a pure passthrough: it republishes the input unchanged.
/// </summary>
[TestClass]
public class FilterTests
{
    private const int LowpassTaps = 5;

    [TestMethod]
    public void OnReceive_Disabled_PassesMatrixThroughUnchanged()
    {
        // IsEnabled = false => passthrough: every element is republished exactly as received.
        using var block = new Filter(name: "F", desiredRate: 0)
        {
            IsEnabled = false
        };
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, -2.0 },
            { 3.5,  4.0 },
            { 9.0,  0.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        // Shape preserved: 3 timesteps × 2 channels.
        Assert.AreEqual(3, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        for (int r = 0; r < input.RowCount; r++)
        for (int c = 0; c < input.ColumnCount; c++)
            Assert.AreEqual(input[r, c], result[r, c], 1e-9); // exact copy, no arithmetic
    }

    [TestMethod]
    public void OnReceive_PreservesInputShape()
    {
        // A default IIR lowpass filter must never change the matrix dimensions:
        // rows (timesteps) and columns (channels) are carried through 1:1.
        using var block = new Filter(
            name: "F",
            desiredRate: 0,
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.IIR,
            cutoffLow: 100,
            order: 2);
        var input = Matrix<double>.Build.Dense(rows: 6, columns: 3, value: 0.5);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(6, result.RowCount);    // timesteps unchanged
        Assert.AreEqual(3, result.ColumnCount); // channels unchanged
    }

    [TestMethod]
    public void OnReceive_FirLowpass_ConstantDcInput_PreservesDcAfterTransient()
    {
        // FIR lowpass coefficients sum to 1 (unity DC gain). Feed a constant DC signal;
        // once the length-5 delay line is full (rows >= 5), the convolution output equals
        // DC · Σ b[k] = DC · 1 = DC. We check the final row, well past the fill point.
        const double dc = 4.0;
        using var block = new Filter(
            name: "F",
            desiredRate: 0,
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 100,
            firTaps: LowpassTaps);
        var input = Matrix<double>.Build.Dense(rows: 20, columns: 1, value: dc);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(20, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        // Delay line is full by row index 4; the last row is a clean steady-state DC value.
        Assert.AreEqual(dc, result[19, 0], 1e-6); // 4.0 · (Σ b[k] = 1) = 4.0
    }

    [TestMethod]
    public void OnReceive_FirHighpass_ConstantDcInput_RejectsDcAfterTransient()
    {
        // FIR highpass = δ − lowpass, so Σ b[k] = 1 − 1 = 0. A constant DC input, once the
        // length-5 delay line is full, yields output = DC · Σ b[k] = DC · 0 = 0.
        const double dc = 7.0;
        using var block = new Filter(
            name: "F",
            desiredRate: 0,
            filterType: FilterType.Highpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 100,
            firTaps: LowpassTaps);
        var input = Matrix<double>.Build.Dense(rows: 20, columns: 1, value: dc);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(20, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        // Once history is filled with the same DC value, the highpass output is exactly 0.
        Assert.AreEqual(0.0, result[19, 0], 1e-6); // 7.0 · (Σ b[k] = 0) = 0
    }

    [TestMethod]
    public void OnReceive_IirLowpass_ConstantDcInput_ConvergesToDc()
    {
        // Butterworth IIR lowpass is normalized for unity gain at DC. Feeding a constant DC
        // signal, the output converges to DC once the transient decays. A low cutoff relative
        // to Fs=1000 Hz settles within a few dozen samples, so the last of 200 rows is at DC.
        const double dc = 3.0;
        using var block = new Filter(
            name: "F",
            desiredRate: 0,
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.IIR,
            cutoffLow: 100,
            order: 2);
        var input = Matrix<double>.Build.Dense(rows: 200, columns: 1, value: dc);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(200, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        // Unity DC gain => steady-state output equals the DC input.
        Assert.AreEqual(dc, result[199, 0], 1e-6);
    }

    [TestMethod]
    public void OnReceive_FiltersEachChannelIndependently()
    {
        // Two channels carry different constant DC levels through an FIR lowpass (Σ b = 1).
        // After the delay line fills, each channel independently recovers its own DC value,
        // proving per-column filter state does not bleed across channels.
        using var block = new Filter(
            name: "F",
            desiredRate: 0,
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 100,
            firTaps: LowpassTaps);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 2.0, -5.0 }, { 2.0, -5.0 }, { 2.0, -5.0 }, { 2.0, -5.0 },
            { 2.0, -5.0 }, { 2.0, -5.0 }, { 2.0, -5.0 }, { 2.0, -5.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(8, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(2.0, result[7, 0], 1e-6);  // ch0 DC preserved:  2.0 · 1
        Assert.AreEqual(-5.0, result[7, 1], 1e-6); // ch1 DC preserved: -5.0 · 1
    }

    [TestMethod]
    public void OnReceive_ZeroInput_ProducesZeroOutput()
    {
        // A linear filter maps the all-zero signal to all-zero output regardless of type or
        // implementation: y[n] = Σ b[k]·0 − Σ a[k]·0 = 0 for every sample.
        using var block = new Filter(
            name: "F",
            desiredRate: 0,
            filterType: FilterType.Bandpass,
            implementation: FilterImplementation.IIR,
            cutoffLow: 50,
            cutoffHigh: 150,
            order: 2);
        var input = Matrix<double>.Build.Dense(rows: 10, columns: 2, value: 0.0);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(10, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        for (int r = 0; r < result.RowCount; r++)
        for (int c = 0; c < result.ColumnCount; c++)
            Assert.AreEqual(0.0, result[r, c], 1e-9); // zero in => zero out
    }
}
