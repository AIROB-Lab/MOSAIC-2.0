using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.ControlAlgorithm;
using MOSAIC.Models.Devices;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Receives prediction vectors and maps them to actuation values using a pluggable
/// <see cref="IControlAlgorithmStrategy"/>. Supports runtime algorithm switching.
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Params contains an algorithm:&lt;Name&gt; token. The first matching token selects the strategy. Confirm the input and output dimensions for the receiving device.</para>
/// <code language="json">
/// {
///   "ControlAlgorithm": {
///     "Type": "controlalgorithm",
///     "Inputs": ["Predictions"],
///     "Params": ["algorithm:DirectControl"]
///   }
/// }
/// </code>
/// </example>
public sealed partial class ControlAlgorithm : BaseBlock
{
    private IControlAlgorithmStrategy _strategy;
    private readonly object _lock = new();

    [ObservableProperty]
    private string _algorithmName;

    /// <summary>
    /// The currently active strategy. Used by the ViewModel to access
    /// strategy-specific properties (e.g. deadband values on DLControlStrategy).
    /// </summary>
    public IControlAlgorithmStrategy Strategy
    {
        get { lock (_lock) return _strategy; }
    }

    public static IReadOnlyList<string> AvailableAlgorithms { get; } = new[]
    {
        "DirectControl",
        "StepwiseControl",
        "BidirectionalControl",
        "DLControl",
    };

    public ControlAlgorithm(
        string name,
        double desiredRate,
        IControlAlgorithmStrategy strategy)
        : base(name, desiredRate)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _algorithmName = strategy.Name;
    }

    public static ControlAlgorithm ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "ControlAlgorithm";
        var rate = m.DesiredRate ?? 0;

        string algoName = "DirectControl";
        if (m.Params is { Count: > 0 })
        {
            foreach (var p in m.Params)
            {
                var s = p?.ToString() ?? "";
                if (s.StartsWith("algorithm:", StringComparison.OrdinalIgnoreCase))
                {
                    algoName = s.Split(':', 2)[1].Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(algoName))
            throw new ArgumentException("ControlAlgorithm requires 'algorithm:<name>' in Params.");

        var strategy = CreateStrategy(algoName);
        return ActivatorUtilities.CreateInstance<ControlAlgorithm>(sp, name, rate, strategy);
    }

    #region JSON Export

    protected override string JsonTypeName => "ControlAlgorithm";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { $"algorithm:{AlgorithmName}" };

    #endregion

    public void SetStrategy(IControlAlgorithmStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        lock (_lock)
        {
            _strategy = strategy;
            AlgorithmName = strategy.Name;
        }
    }

    public void ResetStrategy()
    {
        lock (_lock)
        {
            _strategy.Reset();
        }
    }

    public static IControlAlgorithmStrategy CreateStrategy(string name)
    {
        return name switch
        {
            "DirectControl" => new DirectControlStrategy(),
            "StepwiseControl" => new StepwiseControlStrategy(),
            "BidirectionalControl" => new BidirectionalControlStrategy(),
            "DLControl" => new DLControlStrategy(),
            _ => throw new ArgumentException(
                $"Unknown algorithm '{name}'. Available: {string.Join(", ", AvailableAlgorithms)}")
        };
    }

    protected override void OnReceive(object sender, object data)
    {
        Vector<double>? prediction = data as Vector<double>;

        if (prediction is null && data is ValueTuple<string, object> tagged && tagged.Item2 is Vector<double> v)
            prediction = v;

        if (prediction is null) return;

        lock (_lock)
        {
            _strategy.ProcessPrediction(prediction);
            Publish(new Dictionary<DegreesOfActuation, double>(_strategy.GetControlDict()));
        }
    }
}
