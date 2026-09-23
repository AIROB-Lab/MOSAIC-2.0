using System;
using System.Diagnostics;
using MOSAIC.Models.Devices;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Components.Devices.Wulpus;

/// <summary>
/// Assembles raw Wulpus acquisitions into publishable output (<see cref="Vector"/> or <see cref="Matrix"/>).
/// </summary>
/// <remarks>
/// Three implementations correspond to the three <see cref="Models.Devices.Wulpus.DataSendingMode"/> values.
/// The block delegates all data assembly logic to the active assembler, keeping the block
/// focused on pipeline orchestration.
/// </remarks>
public interface IDataAssembler
{
    /// <summary>
    /// Performs one acquisition cycle and returns the assembled output, or
    /// <see langword="null"/> if the output is not yet ready (e.g., incomplete frame).
    /// </summary>
    /// <remarks>
    /// <b>Must be called under GIL.</b> Implementations call
    /// <see cref="Connection.ReceiveOne"/> internally.
    /// </remarks>
    /// <param name="connection">The active Wulpus connection to read from.</param>
    /// <returns>A <see cref="Vector"/> or <see cref="Matrix"/>, or <see langword="null"/>.</returns>
    object? Assemble(Connection connection);

    /// <summary>
    /// Resets internal state (e.g., clears partial frames). Called when streaming starts.
    /// </summary>
    /// <param name="connection">Connection providing current <c>NumSamples</c> and <c>NumChannelConfigs</c>.</param>
    void Reset(Connection connection);

    /// <summary>
    /// Removes metadata columns (config index + acquisition counter) from the output.
    /// </summary>
    /// <param name="output">The raw output from <see cref="Assemble"/>.</param>
    /// <param name="connection">Connection providing dimensions.</param>
    /// <returns>Output with metadata columns removed.</returns>
    object? CropMetadata(object output, Connection connection);
}

/// <summary>
/// Factory for creating the appropriate <see cref="IDataAssembler"/> based on
/// <see cref="Models.Devices.Wulpus.DataSendingMode"/>.
/// </summary>
public static class DataAssemblerFactory
{
    /// <summary>
    /// Creates a data assembler for the specified mode.
    /// </summary>
    public static IDataAssembler Create(Models.Devices.Wulpus.DataSendingMode mode) => mode switch
    {
        Models.Devices.Wulpus.DataSendingMode.SingleAcquisitionVector => new SingleAcquisitionAssembler(),
        Models.Devices.Wulpus.DataSendingMode.CompleteFrameMatrix      => new CompleteFrameAssembler(),
        Models.Devices.Wulpus.DataSendingMode.UpdateSingleAcqMatrix    => new UpdateSingleAcqAssembler(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown DataSendingMode")
    };
}

/// <summary>
/// Shared helpers for <see cref="IDataAssembler"/> implementations.
/// </summary>
internal static class AssemblerHelpers
{
    /// <summary>
    /// Copies a <c>short[]</c> buffer into a matrix row, converting each element to <c>double</c>.
    /// </summary>
    internal static void CopyBufferToRow(Matrix target, int rowIndex, short[] source)
    {
        var row = Vector.Build.Dense(source.Length);
        for (int i = 0; i < source.Length; i++)
            row[i] = source[i];
        target.SetRow(rowIndex, row);
    }
}

#region Single Acquisition → Vector

/// <summary>
/// Each acquisition is published immediately as a <see cref="Vector"/>.
/// </summary>
internal sealed class SingleAcquisitionAssembler : IDataAssembler
{
    private short[]? _buffer;

    /// <inheritdoc/>
    public void Reset(Connection connection)
    {
        _buffer = new short[connection.NumSamples + 2];
    }

    /// <inheritdoc/>
    public object? Assemble(Connection connection)
    {
        if (_buffer is null) return null;

        var acq = connection.ReceiveOne(_buffer);
        if (acq is null) return null;

        return Vector.Build.DenseOfArray(Array.ConvertAll(_buffer, x => (double)x));
    }

    /// <inheritdoc/>
    public object? CropMetadata(object output, Connection connection)
        => output is Vector v ? v.SubVector(0, connection.NumSamples) : null;
}

#endregion

#region Complete Frame → Matrix

/// <summary>
/// Waits for a full frame (all TX/RX configs) before publishing as a <see cref="Matrix"/>.
/// </summary>
internal sealed class CompleteFrameAssembler : IDataAssembler
{
    private short[]? _buffer;
    private Matrix? _frame;
    private int _expectedConfigIx;

    /// <inheritdoc/>
    public void Reset(Connection connection)
    {
        _buffer = new short[connection.NumSamples + 2];
        _frame = Matrix.Build.Dense(connection.NumChannelConfigs, connection.NumSamples + 2);
        _expectedConfigIx = 0;
    }

    /// <inheritdoc/>
    public object? Assemble(Connection connection)
    {
        if (_buffer is null || _frame is null) return null;

        var acq = connection.ReceiveOne(_buffer);
        if (acq is null) return null;

        if (acq.Value.ConfigIndex != _expectedConfigIx)
        {
            Debug.WriteLine($"[CompleteFrameAssembler] Incomplete frame — expected config {_expectedConfigIx}, got {acq.Value.ConfigIndex}. Resetting.");
            _expectedConfigIx = 0;
        }

        if (_expectedConfigIx == 0)
            _frame.Clear();

        AssemblerHelpers.CopyBufferToRow(_frame, _expectedConfigIx, _buffer);
        _expectedConfigIx++;

        if (_expectedConfigIx == connection.NumChannelConfigs)
        {
            _expectedConfigIx = 0;
            return Matrix.Build.DenseOfMatrix(_frame);
        }

        return null;
    }

    /// <inheritdoc/>
    public object? CropMetadata(object output, Connection connection)
        => output is Matrix m
            ? m.SubMatrix(0, connection.NumChannelConfigs, 0, connection.NumSamples)
            : null;
}

#endregion

#region Update Single Acq → Matrix (higher frame rate)

/// <summary>
/// Updates the corresponding row in the B-mode matrix on every acquisition
/// and publishes the full matrix each time, yielding higher frame rates.
/// </summary>
internal sealed class UpdateSingleAcqAssembler : IDataAssembler
{
    private short[]? _buffer;
    private Matrix? _frame;

    /// <inheritdoc/>
    public void Reset(Connection connection)
    {
        _buffer = new short[connection.NumSamples + 2];
        _frame = Matrix.Build.Dense(connection.NumChannelConfigs, connection.NumSamples + 2);
    }

    /// <inheritdoc/>
    public object? Assemble(Connection connection)
    {
        if (_buffer is null || _frame is null) return null;

        var acq = connection.ReceiveOne(_buffer);
        if (acq is null) return null;

        AssemblerHelpers.CopyBufferToRow(_frame, acq.Value.ConfigIndex, _buffer);
        return Matrix.Build.DenseOfMatrix(_frame);
    }

    /// <inheritdoc/>
    public object? CropMetadata(object output, Connection connection)
        => output is Matrix m
            ? m.SubMatrix(0, connection.NumChannelConfigs, 0, connection.NumSamples)
            : null;
}

#endregion