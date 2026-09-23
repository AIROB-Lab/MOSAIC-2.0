using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Selects or filters channels from an input vector or matrix.
/// Inactive channels can either be set to zero or removed from the output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Modes:</b>
/// <list type="bullet">
///     <item><description><b>Zero mode</b> (DeleteChannel=false): Inactive channels output 0</description></item>
///     <item><description><b>Delete mode</b> (DeleteChannel=true): Inactive channels are removed from output</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Input types:</b>
/// <list type="bullet">
///     <item><description><see cref="Vector{T}"/>: Each element is one channel.</description></item>
///     <item><description><see cref="Matrix{T}"/> [timesteps × channels]: Each column is one channel.
///     The activation mask is applied column-wise. In delete mode, inactive columns are removed;
///     in zero mode, inactive columns are zeroed.</description></item>
/// </list>
/// </para>
/// <para>
/// Thread-safe for concurrent access to channel activations.
/// </para>
/// </remarks>
/// <example>
/// <para>Block entry for a larger pipeline. Params is not used. Select active channels on the card or through the channel-selection API; the channel mask is not restored from this JSON.</para>
/// <code language="json">
/// {
///   "ChannelSelector": {
///     "Type": "channelselector",
///     "Inputs": ["Signal"]
///   }
/// }
/// </code>
/// </example>
public partial class ChannelSelector : BaseBlock
{
    private readonly object _lock = new();
    private bool[] _channelActivations = Array.Empty<bool>();
    private int _numberOfChannels;

    // Preallocated output buffer for zero-mode vector (same size as input)
    private double[]? _zeroModeBuffer;

    // Reusable list for delete-mode vector
    private readonly List<double> _deleteModeBuffer = new();

    /// <summary>
    /// Scope for visualizing the output signal.
    /// </summary>
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets or sets whether inactive channels should be deleted (true) or set to zero (false).
    /// </summary>
    [ObservableProperty]
    private bool _deleteChannel;

    /// <summary>
    /// Gets the number of channels detected from the input.
    /// </summary>
    public int NumberOfChannels
    {
        get => _numberOfChannels;
        private set
        {
            if (_numberOfChannels != value)
            {
                _numberOfChannels = value;
                OnPropertyChanged();
                NumberOfChannelsChanged?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Gets the number of currently active channels.
    /// </summary>
    public int ActiveChannelCount
    {
        get
        {
            lock (_lock)
            {
                return _channelActivations.Count(a => a);
            }
        }
    }

    /// <summary>
    /// Gets the output dimension based on current settings.
    /// </summary>
    public int OutputDimension => DeleteChannel ? ActiveChannelCount : NumberOfChannels;

    /// <summary>
    /// Gets the current channel activation states.
    /// </summary>
    public IReadOnlyList<bool> ChannelActivations
    {
        get
        {
            lock (_lock)
            {
                return _channelActivations.ToArray();
            }
        }
    }

    /// <summary>
    /// Event raised when the number of channels changes.
    /// </summary>
    public event Action<int>? NumberOfChannelsChanged;

    /// <summary>
    /// Event raised when channel activations change.
    /// </summary>
    public event Action? ActivationsChanged;

    public ChannelSelector(string name, double desiredRate = 0)
        : base(name, desiredRate)
    {
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_selected.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_selected";

    public static ChannelSelector ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var block = new ChannelSelector(m.Name ?? "ChannelSelector", m.DesiredRate ?? 0d);
        return block;
    }

    public override void Dispose()
    {
        base.Dispose();
        Viz.Dispose();
    }

    #region Channel Activation API

    /// <summary>
    /// Updates the activation state of each channel.
    /// </summary>
    public void UpdateChannelActivations(IEnumerable<bool> activations)
    {
        lock (_lock)
        {
            _channelActivations = activations.ToArray();
        }

        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(OutputDimension));
        ActivationsChanged?.Invoke();
    }

    /// <summary>
    /// Sets all channels to active or inactive.
    /// </summary>
    public void SetAllChannels(bool active)
    {
        lock (_lock)
        {
            Array.Fill(_channelActivations, active);
        }

        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(OutputDimension));
        ActivationsChanged?.Invoke();
    }

    /// <summary>
    /// Toggles a specific channel's activation state.
    /// </summary>
    public void ToggleChannel(int index)
    {
        lock (_lock)
        {
            if (index >= 0 && index < _channelActivations.Length)
                _channelActivations[index] = !_channelActivations[index];
        }

        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(OutputDimension));
        ActivationsChanged?.Invoke();
    }

    /// <summary>
    /// Sets a specific channel's activation state.
    /// </summary>
    public void SetChannel(int index, bool active)
    {
        bool changed = false;
        lock (_lock)
        {
            if (index >= 0 && index < _channelActivations.Length && _channelActivations[index] != active)
            {
                _channelActivations[index] = active;
                changed = true;
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(ActiveChannelCount));
            OnPropertyChanged(nameof(OutputDimension));
            ActivationsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Gets whether a specific channel is active.
    /// </summary>
    public bool IsChannelActive(int index)
    {
        lock (_lock)
        {
            return index >= 0 && index < _channelActivations.Length && _channelActivations[index];
        }
    }

    #endregion

    #region OnReceive

    protected override void OnReceive(object sender, object? value)
    {
        switch (value)
        {
            case Vector<double> vec:
                EnsureChannelCount(vec.Count);
                var vecResult = GenerateVectorOutput(vec);
                Publish(vecResult);
                Viz.Feed(vecResult);
                break;

            case Matrix<double> mat:
                // Channels = columns, timesteps = rows
                EnsureChannelCount(mat.ColumnCount);
                var matResult = GenerateMatrixOutput(mat);
                Publish(matResult);
                // Feed visualization with the last row (latest timestep)
                if (matResult.RowCount > 0)
                    Viz.Feed(matResult.Row(matResult.RowCount - 1));
                break;

            default:
                ReportError("Expected a vector or matrix of channel values.");
                return;
        }

    }

    #endregion

    #region Channel Count Management

    /// <summary>
    /// Detects channel count changes and resizes the activation array,
    /// preserving existing activations where possible.
    /// </summary>
    private void EnsureChannelCount(int channelCount)
    {
        if (channelCount == NumberOfChannels) return;

        NumberOfChannels = channelCount;

        lock (_lock)
        {
            var old = _channelActivations;
            _channelActivations = new bool[channelCount];

            for (int i = 0; i < channelCount; i++)
                _channelActivations[i] = i < old.Length ? old[i] : true;
        }

        _zeroModeBuffer = new double[channelCount];

        OnPropertyChanged(nameof(ActiveChannelCount));
        OnPropertyChanged(nameof(OutputDimension));
    }

    #endregion

    #region Output Generation — Vector

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector<double> GenerateVectorOutput(Vector<double> input)
    {
        bool deleteMode = DeleteChannel;
        int count = input.Count;

        lock (_lock)
        {
            if (deleteMode)
            {
                _deleteModeBuffer.Clear();
                for (int i = 0; i < count; i++)
                {
                    if (_channelActivations[i])
                        _deleteModeBuffer.Add(input[i]);
                }
                return DenseVector.OfArray(_deleteModeBuffer.ToArray());
            }
            else
            {
                var buffer = _zeroModeBuffer;
                if (buffer == null || buffer.Length != count)
                    buffer = _zeroModeBuffer = new double[count];

                for (int i = 0; i < count; i++)
                    buffer[i] = _channelActivations[i] ? input[i] : 0.0;

                return DenseVector.OfArray(buffer);
            }
        }
    }

    #endregion

    #region Output Generation — Matrix

    /// <summary>
    /// Applies the channel activation mask to a [timesteps × channels] matrix.
    /// In delete mode, inactive columns are removed. In zero mode, they are zeroed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Matrix<double> GenerateMatrixOutput(Matrix<double> input)
    {
        int rows = input.RowCount;
        int cols = input.ColumnCount;
        bool deleteMode = DeleteChannel;

        lock (_lock)
        {
            if (deleteMode)
            {
                // Collect active column indices
                var activeCols = new List<int>(cols);
                for (int c = 0; c < cols; c++)
                {
                    if (_channelActivations[c])
                        activeCols.Add(c);
                }

                if (activeCols.Count == 0)
                    return Matrix<double>.Build.Dense(rows, 0);

                var result = Matrix<double>.Build.Dense(rows, activeCols.Count);
                for (int ci = 0; ci < activeCols.Count; ci++)
                {
                    var srcCol = input.Column(activeCols[ci]);
                    result.SetColumn(ci, srcCol);
                }

                return result;
            }
            else
            {
                // Zero inactive columns — copy whole matrix then zero masked columns
                var result = input.Clone();

                for (int c = 0; c < cols; c++)
                {
                    if (!_channelActivations[c])
                        result.ClearColumn(c);
                }

                return result;
            }
        }
    }

    #endregion
}