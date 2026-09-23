using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Projects (selects) specific channels from an input vector or matrix by index,
/// producing a reduced-dimension output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vector input:</b> Selects elements at the configured <see cref="ProjectedIndices"/>
/// and publishes a shorter vector containing only those elements.
/// </para>
/// <para>
/// <b>Matrix input [timesteps × channels]:</b> Selects columns at the configured indices
/// and publishes a narrower matrix. All timesteps are preserved.
/// </para>
/// <para>
/// <b>Index validation:</b> Indices outside the valid range [0, N−1] are silently skipped
/// at runtime. The <see cref="ValidationError"/> and <see cref="IsValid"/> properties provide
/// UI feedback when invalid indices are configured.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "SelectChannels": {
///     "Type": "Projector",
///     "Inputs": [ "EMG" ],
///     "Params": [ "0;2;4;6" ]
///   }
/// }
/// </code>
/// Params[0]: semicolon-separated zero-based channel indices.
/// </example>
public partial class Projector : BaseBlock
{
    /// <summary>Preallocated vector output buffer.</summary>
    private Vector<double>? _projectedValue;

    /// <summary>Visualization helper for binding the projected output to the UI scope.</summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets or sets the indices to project from the input.
    /// </summary>
    [ObservableProperty]
    private int[] _projectedIndices = Array.Empty<int>();

    /// <summary>
    /// Gets the number of input channels (detected from the input).
    /// </summary>
    [ObservableProperty]
    private int _inputChannels;

    /// <summary>Gets the number of output channels.</summary>
    public int OutputChannels => ProjectedIndices.Length;

    /// <summary>Gets the maximum valid index.</summary>
    public int MaxValidIndex => Math.Max(0, InputChannels - 1);

    /// <summary>Gets a human-readable summary of the projection mapping.</summary>
    public string ConfigSummary => InputChannels > 0
        ? $"{InputChannels} ch → {OutputChannels} ch"
        : $"→ {OutputChannels} ch";

    /// <summary>
    /// Gets a validation error message, or <see langword="null"/> if valid.
    /// </summary>
    public string? ValidationError
    {
        get
        {
            if (InputChannels == 0) return null;
            var invalid = ProjectedIndices.Where(i => i < 0 || i >= InputChannels).ToArray();
            return invalid.Length > 0
                ? $"Invalid indices: {string.Join(", ", invalid)} (max: {InputChannels - 1})"
                : null;
        }
    }

    /// <summary>Gets whether the current index configuration is valid.</summary>
    public bool IsValid => ValidationError == null;

    public Projector(string name, int[]? indices, double desiredRate = 0) : base(name, desiredRate)
    {
        _projectedIndices = indices ?? [];
    }

    partial void OnProjectedIndicesChanged(int[] value)
    {
        _projectedValue = null;
        NotifyComputedProperties();
    }

    partial void OnInputChannelsChanged(int value) => NotifyComputedProperties();

    private void NotifyComputedProperties()
    {
        OnPropertyChanged(nameof(OutputChannels));
        OnPropertyChanged(nameof(MaxValidIndex));
        OnPropertyChanged(nameof(ConfigSummary));
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_projected.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_projected";

    public static Projector ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var indices = Array.Empty<int>();
        if (m.Params is { Count: > 0 })
        {
            var param = m.Params[0];
            var paramStr = param is JsonElement je ? je.GetString() : param?.ToString();
            if (!string.IsNullOrEmpty(paramStr))
            {
                indices = paramStr.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse)
                    .ToArray();
            }
        }

        var block = ActivatorUtilities.CreateInstance<Projector>(sp, m.Name ?? "Projector", indices, m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Projector";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => [string.Join(";", ProjectedIndices)];

    #endregion

    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }

    protected override void OnReceive(object sender, object? value)
    {
        switch (value)
        {
            case Vector<double> vec:
                ProcessVector(vec);
                break;

            case Matrix<double> mat:
                ProcessMatrix(mat);
                break;

            default:
                ReportError("Expected a vector or matrix to project.");
                break;
        }
    }

    /// <summary>
    /// Projects elements from a vector by index.
    /// </summary>
    private void ProcessVector(Vector<double> input)
    {
        if (input.Count == 0) { ReportError("Cannot project an empty vector."); return; }

        if (InputChannels != input.Count)
            InputChannels = input.Count;

        var indices = ProjectedIndices;
        var valid = indices.Where(i => i >= 0 && i < input.Count).ToArray();

        if (valid.Length == 0) { ReportError("No selected channel index is valid for this input."); return; }

        if (_projectedValue == null || _projectedValue.Count != valid.Length)
            _projectedValue = Vector<double>.Build.Dense(valid.Length);

        for (int i = 0; i < valid.Length; i++)
            _projectedValue[i] = input[valid[i]];

        if (valid.Length != indices.Length) ReportError("Some selected channel indices are outside the input; only valid channels are published.");

        var output = _projectedValue.Clone();
        Publish(output);
        Viz?.Feed(output);
    }

    /// <summary>
    /// Projects columns from a [timesteps × channels] matrix by index.
    /// </summary>
    private void ProcessMatrix(Matrix<double> input)
    {
        if (input.ColumnCount == 0 || input.RowCount == 0) { ReportError("Cannot project an empty matrix."); return; }

        if (InputChannels != input.ColumnCount)
            InputChannels = input.ColumnCount;

        var indices = ProjectedIndices;
        var valid = indices.Where(i => i >= 0 && i < input.ColumnCount).ToArray();

        if (valid.Length == 0) { ReportError("No selected channel index is valid for this input."); return; }

        var result = Matrix<double>.Build.Dense(input.RowCount, valid.Length);
        for (int i = 0; i < valid.Length; i++)
            result.SetColumn(i, input.Column(valid[i]));

        if (valid.Length != indices.Length) ReportError("Some selected channel indices are outside the input; only valid channels are published.");

        Publish(result);

        // Feed viz with last row (latest timestep)
        if (result.RowCount > 0)
            Viz?.Feed(result.Row(result.RowCount - 1));
    }
}