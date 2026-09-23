using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

// The MOSAIC.Selector namespace is visible as the simple name "Selector" from within any
// MOSAIC.* namespace and shadows the block type, so alias it under a non-colliding name.
using SelectorBlock = MOSAIC.Models.FlowControl.Selector;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Selector"/>.
///
/// The block takes a labelled tuple <c>(string label, data)</c>, discards the label, and forwards
/// only the data payload — the exact same instance, unmodified. It accepts five shapes:
/// <list type="bullet">
///   <item><description><c>ValueTuple&lt;string, Vector&lt;double&gt;&gt;</c> — publishes the vector.</description></item>
///   <item><description><c>ValueTuple&lt;string, Matrix&lt;double&gt;&gt;</c> — publishes the matrix.</description></item>
///   <item><description><c>ValueTuple&lt;string, object&gt;</c> — publishes Item2 when it is a Vector or Matrix.</description></item>
///   <item><description><c>Tuple&lt;string, Vector&lt;double&gt;&gt;</c> — legacy class tuple; publishes the vector.</description></item>
///   <item><description><c>Tuple&lt;string, Matrix&lt;double&gt;&gt;</c> — legacy class tuple; publishes the matrix.</description></item>
/// </list>
/// A successful forward publishes its payload; unsupported input publishes nothing and
/// reports a separate validation error. Activity status is measured by BaseBlock.
/// </summary>
[TestClass]
public class SelectorTests
{
    // col0 = [1,4], col1 = [2,5], col2 = [3,6]
    private static Matrix<double> SampleMatrix() => Matrix<double>.Build.DenseOfArray(new double[,]
    {
        { 1.0, 2.0, 3.0 },
        { 4.0, 5.0, 6.0 },
    });

    private static Vector<double> SampleVector() => Vector<double>.Build.Dense(new[] { 10.0, 20.0, 30.0 });

    [TestMethod]
    public void OnReceive_ValueTupleVector_ForwardsSameVector()
    {
        // (label, Vector) => the label is dropped and the exact vector instance is published.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var payload = SampleVector();
        (string label, Vector<double> data) input = ("emg", payload);

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.IsTrue(ReferenceEquals(payload, result), "Selector must forward the exact payload instance.");
        Assert.AreEqual(10.0, result[0], 1e-9);
        Assert.AreEqual(20.0, result[1], 1e-9);
        Assert.AreEqual(30.0, result[2], 1e-9);
        Assert.IsNull(block.LastError);
    }

    [TestMethod]
    public void OnReceive_ValueTupleMatrix_ForwardsSameMatrix()
    {
        // (label, Matrix) => label dropped, exact matrix instance published, shape 2x3 preserved.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var payload = SampleMatrix();
        (string label, Matrix<double> data) input = ("features", payload);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);
        Assert.IsTrue(ReferenceEquals(payload, result), "Selector must forward the exact payload instance.");
        Assert.AreEqual(1.0, result[0, 0], 1e-9);
        Assert.AreEqual(6.0, result[1, 2], 1e-9);
        Assert.IsNull(block.LastError);
    }

    [TestMethod]
    public void OnReceive_ValueTupleObjectVectorPayload_ForwardsVector()
    {
        // (string, object) whose object is a Vector => boxed as ValueTuple<string, object>, so it
        // matches the runtime-typed branch (not the typed Vector branch) and forwards the vector.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var payload = SampleVector();
        (string label, object data) input = ("emg", payload);

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.IsTrue(ReferenceEquals(payload, result), "Selector must forward the exact payload instance.");
        Assert.AreEqual(10.0, result[0], 1e-9);
        Assert.AreEqual(30.0, result[2], 1e-9);
        Assert.IsNull(block.LastError);
    }

    [TestMethod]
    public void OnReceive_ValueTupleObjectMatrixPayload_ForwardsMatrix()
    {
        // (string, object) whose object is a Matrix => runtime-typed branch forwards the matrix.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var payload = SampleMatrix();
        (string label, object data) input = ("features", payload);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);
        Assert.IsTrue(ReferenceEquals(payload, result), "Selector must forward the exact payload instance.");
        Assert.AreEqual(2.0, result[0, 1], 1e-9);
        Assert.IsNull(block.LastError);
    }

    [TestMethod]
    public void OnReceive_LegacyClassTupleVector_ForwardsVector()
    {
        // Legacy reference-type Tuple<string, Vector> => label dropped, vector forwarded.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var payload = SampleVector();
        var input = new Tuple<string, Vector<double>>("emg", payload);

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.IsTrue(ReferenceEquals(payload, result), "Selector must forward the exact payload instance.");
        Assert.AreEqual(20.0, result[1], 1e-9);
        Assert.IsNull(block.LastError);
    }

    [TestMethod]
    public void OnReceive_LegacyClassTupleMatrix_ForwardsMatrix()
    {
        // Legacy reference-type Tuple<string, Matrix> => label dropped, matrix forwarded.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var payload = SampleMatrix();
        var input = new Tuple<string, Matrix<double>>("features", payload);

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);
        Assert.IsTrue(ReferenceEquals(payload, result), "Selector must forward the exact payload instance.");
        Assert.AreEqual(4.0, result[1, 0], 1e-9);
        Assert.IsNull(block.LastError);
    }

    [TestMethod]
    public void OnReceive_UnsupportedType_PublishesNothingAndReportsError()
    {
        // A bare vector is not a labelled tuple => no branch matches, nothing is published,
        // and a validation error is reported.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        var input = SampleVector();

        bool published = BlockHarness.TryCapture(block, input, out _);

        Assert.IsFalse(published, "Selector must not publish for an unsupported input type.");
        Assert.IsNotNull(block.LastError);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
    }

    [TestMethod]
    public void OnReceive_ObjectPayloadNeitherVectorNorMatrix_PublishesNothingAndReportsError()
    {
        // (string, object) whose object is a plain string fails the "is Vector or Matrix" guard on
        // the runtime-typed branch => falls through to the default: nothing published, separate error.
        using var block = new SelectorBlock(name: "Sel", desiredRate: 0);
        (string label, object data) input = ("emg", "not-a-payload");

        bool published = BlockHarness.TryCapture(block, input, out _);

        Assert.IsFalse(published, "Selector must not publish when the object payload is not a Vector/Matrix.");
        Assert.IsNotNull(block.LastError);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
    }
}
