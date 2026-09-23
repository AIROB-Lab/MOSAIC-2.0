using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="Function"/>, which applies a configured element-wise
/// function to every entry of the incoming Vector/Matrix and republishes the same shape.
/// The delegate is either supplied to the constructor or (re)built by <see cref="Function.SetFunction"/>
/// from a type string plus up to two parameters:
///   abs       => |x|
///   add       => x + P1
///   multiply  => x * P1
///   power     => x ^ P1
///   clip      => clamp(x, P1, P2)
///   threshold => sign(x) * max(0, |x| - P1)
///   (unknown) => |x|   (falls back to abs)
/// </summary>
[TestClass]
public class FunctionTests
{
    [TestMethod]
    public void OnReceive_AbsDelegateFromConstructor_TakesElementwiseMagnitude()
    {
        // Constructor delegate is Math.Abs, so each entry maps to |x|:
        // [[-1,-2],[3,-4]] => [[1,2],[3,4]].
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { -1.0, -2.0 },
            {  3.0, -4.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(1.0, result[0, 0], 1e-9); // |-1|
        Assert.AreEqual(2.0, result[0, 1], 1e-9); // |-2|
        Assert.AreEqual(3.0, result[1, 0], 1e-9); // |3|
        Assert.AreEqual(4.0, result[1, 1], 1e-9); // |-4|
    }

    [TestMethod]
    public void OnReceive_PowerTwo_SquaresEachElement()
    {
        // SetFunction("power", 2) => x^2:
        // [[-2,3],[4,-5]] => [[4,9],[16,25]].
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        block.SetFunction("power", param1: 2.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { -2.0,  3.0 },
            {  4.0, -5.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(4.0, result[0, 0], 1e-6);  // (-2)^2
        Assert.AreEqual(9.0, result[0, 1], 1e-6);  // 3^2
        Assert.AreEqual(16.0, result[1, 0], 1e-6); // 4^2
        Assert.AreEqual(25.0, result[1, 1], 1e-6); // (-5)^2
    }

    [TestMethod]
    public void OnReceive_Add_ShiftsEachElementByParam1()
    {
        // SetFunction("add", 10) => x + 10:
        // [[1,-2],[3,-4]] => [[11,8],[13,6]].
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        block.SetFunction("add", param1: 10.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, -2.0 },
            { 3.0, -4.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(11.0, result[0, 0], 1e-9); // 1 + 10
        Assert.AreEqual(8.0, result[0, 1], 1e-9);  // -2 + 10
        Assert.AreEqual(13.0, result[1, 0], 1e-9); // 3 + 10
        Assert.AreEqual(6.0, result[1, 1], 1e-9);  // -4 + 10
    }

    [TestMethod]
    public void OnReceive_Multiply_ScalesEachElementByParam1()
    {
        // SetFunction("multiply", -3) => x * -3:
        // [[1,-2],[0,4]] => [[-3,6],[0,-12]].
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        block.SetFunction("multiply", param1: -3.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, -2.0 },
            { 0.0,  4.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(-3.0, result[0, 0], 1e-9);  // 1 * -3
        Assert.AreEqual(6.0, result[0, 1], 1e-9);   // -2 * -3
        Assert.AreEqual(0.0, result[1, 0], 1e-9);   // 0 * -3
        Assert.AreEqual(-12.0, result[1, 1], 1e-9); // 4 * -3
    }

    [TestMethod]
    public void OnReceive_Clip_ClampsEachElementBetweenParam1AndParam2()
    {
        // SetFunction("clip", -1, 1) => clamp(x, -1, 1):
        // -5 -> -1 (below lower), 0.5 -> 0.5 (inside), 2 -> 1 (above upper), -0.25 -> -0.25 (inside).
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        block.SetFunction("clip", param1: -1.0, param2: 1.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { -5.0,  0.5 },
            {  2.0, -0.25 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(-1.0, result[0, 0], 1e-9);   // clamp(-5, -1, 1)
        Assert.AreEqual(0.5, result[0, 1], 1e-9);    // clamp(0.5, -1, 1)
        Assert.AreEqual(1.0, result[1, 0], 1e-9);    // clamp(2, -1, 1)
        Assert.AreEqual(-0.25, result[1, 1], 1e-9);  // clamp(-0.25, -1, 1)
    }

    [TestMethod]
    public void OnReceive_Threshold_SubtractsParam1FromMagnitudeAndKeepsSign()
    {
        // SetFunction("threshold", 2) => sign(x) * max(0, |x| - 2), applied per element (single channel):
        //   3  -> +1 * max(0, 3-2) =  1
        //  -5  -> -1 * max(0, 5-2) = -3
        //   1  -> +1 * max(0, 1-2) =  0   (below threshold, squashed)
        //  -2  -> -1 * max(0, 2-2) =  0   (exactly at threshold, mag not > 0)
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        block.SetFunction("threshold", param1: 2.0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            {  3.0 },
            { -5.0 },
            {  1.0 },
            { -2.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(4, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        Assert.AreEqual(1.0, result[0, 0], 1e-9);   //  3 -> 1
        Assert.AreEqual(-3.0, result[1, 0], 1e-9);  // -5 -> -3
        Assert.AreEqual(0.0, result[2, 0], 1e-9);   //  1 -> 0
        Assert.AreEqual(0.0, result[3, 0], 1e-9);   // -2 -> 0
    }

    [TestMethod]
    public void OnReceive_UnknownFunctionType_FallsBackToAbs()
    {
        // An unrecognized type hits the default switch arm, which is Math.Abs:
        // [[-7]] => [[7]].
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        block.SetFunction("bogus");
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { -7.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(1, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        Assert.AreEqual(7.0, result[0, 0], 1e-9); // |-7|
    }

    [TestMethod]
    public void OnReceive_VectorInput_PublishesVectorOfSameLength()
    {
        // A Vector<double> input follows the vector branch of OnReceive and republishes a Vector.
        // abs of [-1, 2, -3] => [1, 2, 3].
        using var block = new Function(name: "fn", desiredRate: 0, function: Math.Abs);
        var input = Vector<double>.Build.Dense(new[] { -1.0, 2.0, -3.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9); // |-1|
        Assert.AreEqual(2.0, result[1], 1e-9); // |2|
        Assert.AreEqual(3.0, result[2], 1e-9); // |-3|
    }

    [TestMethod]
    public void Constructor_NullFunction_ThrowsArgumentNullException()
    {
        // The constructor guards the delegate: a null function is rejected up front.
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Function(name: "fn", desiredRate: 0, function: null!));
    }
}
