using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Crops (slices) incoming vectors or matrices to a sub-range along the depth and/or channel axis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vector input:</b> Extracts a sub-vector starting at <see cref="DepthIndex"/> with length
/// <see cref="DepthCount"/>.
/// </para>
/// <para>
/// <b>Matrix input:</b> Extracts a sub-matrix starting at row <see cref="RowIndex"/> with
/// <see cref="RowCount"/> rows and column <see cref="DepthIndex"/> with <see cref="DepthCount"/>
/// columns. When only two params are provided, the row range defaults to all rows.
/// </para>
/// <para>
/// Useful for trimming ultrasound A-mode lines to a region of interest, discarding metadata
/// samples, or selecting a subset of channels from a multi-channel frame.
/// </para>
/// <para>
/// <b>JSON configuration:</b>
/// <code>
/// // Vector crop (depth only):
/// "crop": {
///   "Type": "Crop",
///   "Inputs": ["wulpusPy"],
///   "Params": [10, 380]
/// }
///
/// // Matrix crop (rows + columns):
/// "crop": {
///   "Type": "Crop",
///   "Inputs": ["wulpusPy"],
///   "Params": [10, 380, 0, 4]
/// }
/// </code>
/// 2-param form: [DepthIndex, DepthCount]
/// 4-param form: [DepthIndex, DepthCount, RowIndex, RowCount]
/// </para>
/// </remarks>
public sealed partial class Crop : BaseBlock
{
    #region Observable Properties

    /// <summary>Start index along the depth (column / sample) axis.</summary>
    [ObservableProperty] private int _depthIndex;

    /// <summary>Number of samples to keep along the depth axis.</summary>
    [ObservableProperty] private int _depthCount = 1;

    /// <summary>Start index along the row (channel) axis. Only used for Matrix input.</summary>
    [ObservableProperty] private int _rowIndex;

    /// <summary>Number of rows (channels) to keep. Only used for Matrix input.</summary>
    [ObservableProperty] private int _rowCount;

    /// <summary>Whether a 4-param (row + depth) crop is active.</summary>
    [ObservableProperty] private bool _hasRowCrop;

    /// <summary>Index of the currently visualized channel (for matrix output).</summary>
    [ObservableProperty] private int _visualizedConfig;

    /// <summary>Dimensions of the last input received (for display).</summary>
    [ObservableProperty] private string _inputShape = "";

    /// <summary>Dimensions of the last output produced (for display).</summary>
    [ObservableProperty] private string _outputShape = "";

    #endregion

    #region Public Surface

    /// <summary>Visualization bundle for scope display of the cropped output.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    #endregion

    #region Constructor & Factory

    /// <summary>
    /// Creates a new <see cref="Crop"/> block.
    /// </summary>
    /// <param name="name">Block name.</param>
    /// <param name="desiredRate">Desired execution rate in Hz.</param>
    /// <param name="depthIndex">Start index along depth axis.</param>
    /// <param name="depthCount">Number of depth samples to keep.</param>
    /// <param name="rowIndex">Start row index (0 if unused).</param>
    /// <param name="rowCount">Number of rows to keep (0 if unused).</param>
    public Crop(
        string name,
        double desiredRate = 0,
        int depthIndex = 0,
        int depthCount = 1,
        int rowIndex = 0,
        int rowCount = 0)
        : base(name, desiredRate)
    {
        _depthIndex = depthIndex;
        _depthCount = depthCount;
        _rowIndex = rowIndex;
        _rowCount = rowCount;
        _hasRowCrop = rowCount > 0;

    }

    /// <summary>
    /// Creates a <see cref="Crop"/> from JSON pipeline configuration.
    /// </summary>
    public static Crop ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Crop";
        var rate = m.DesiredRate ?? 0;

        int depthIndex = 0, depthCount = 1, rowIndex = 0, rowCount = 0;

        if (m.Params is { Count: >= 2 })
        {
            depthIndex = GetInt(m.Params[0], 0);
            depthCount = GetInt(m.Params[1], 1);
        }
        if (m.Params is { Count: >= 4 })
        {
            rowIndex = GetInt(m.Params[2], 0);
            rowCount = GetInt(m.Params[3], 0);
        }

        var block = ActivatorUtilities.CreateInstance<Crop>(
            sp, name, rate, depthIndex, depthCount, rowIndex, rowCount);

        return block;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc/>
    protected override string JsonTypeName => "Crop";

    /// <inheritdoc/>
    protected override IReadOnlyList<object>? GetJsonParams()
    {
        if (HasRowCrop)
            return new List<object> { DepthIndex, DepthCount, RowIndex, RowCount };
        return new List<object> { DepthIndex, DepthCount };
    }

    #endregion

    #region Data Processing

    /// <inheritdoc/>
    protected override void OnReceive(object sender, object data)
    {
        try
        {
            switch (data)
            {
                case Vector v:
                {
                    InputShape = $"Vector[{v.Count}]";
                    var cropped = v.SubVector(DepthIndex, DepthCount);
                    OutputShape = $"Vector[{cropped.Count}]";
                    Publish(cropped);
                    Viz.Feed(cropped);
                    break;
                }

                case Matrix m:
                {
                    InputShape = $"Matrix[{m.RowCount}×{m.ColumnCount}]";

                    Matrix cropped;
                    if (HasRowCrop)
                    {
                        cropped = m.SubMatrix(RowIndex, RowCount, DepthIndex, DepthCount);
                    }
                    else
                    {
                        // Keep all rows, crop columns only
                        cropped = m.SubMatrix(0, m.RowCount, DepthIndex, DepthCount);
                    }

                    OutputShape = $"Matrix[{cropped.RowCount}×{cropped.ColumnCount}]";
                    Publish(cropped);

                    var visIx = Math.Clamp(VisualizedConfig, 0, cropped.RowCount - 1);
                    Viz.Feed(cropped.Row(visIx));
                    break;
                }

                default:
                    ReportError("Expected a vector or matrix to crop.");
                    break;
            }
        }
        catch (Exception ex)
        {
            ReportError("Crop failed; check the selected rows and columns.", ex);
        }
    }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public override void Dispose()
    {
        Viz.Dispose();
        base.Dispose();
    }

    #endregion
}