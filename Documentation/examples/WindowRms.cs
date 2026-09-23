using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;

namespace MOSAIC.Documentation.Examples;

/// <summary>Teaching example: one RMS value per column of an incoming sample window.</summary>
public sealed class WindowRms : BaseBlock
{
    public WindowRms(string name, double desiredRate = 0) : base(name, desiredRate) { }
    public BlockVisualization Viz { get; } = new();
    protected override BlockVisualization? Visualization => Viz;

    protected override void OnReceive(object sender, object value)
    {
        if (value is not Matrix<double> input || input.RowCount == 0 || input.ColumnCount == 0)
            return;

        var result = Vector<double>.Build.Dense(input.ColumnCount);
        for (int channel = 0; channel < input.ColumnCount; channel++)
        {
            double sumOfSquares = 0;
            for (int row = 0; row < input.RowCount; row++)
            {
                double sample = input[row, channel];
                if (!double.IsFinite(sample))
                    return;

                sumOfSquares += sample * sample;
            }

            result[channel] = Math.Sqrt(sumOfSquares / input.RowCount);
            if (!double.IsFinite(result[channel]))
                return;
        }

        // This feature stream has one time point per input publication.
        // Set its output meaning explicitly; the input window's sample spacing is not the feature spacing.
        if (InputRate > 0)
        {
            if (Math.Abs(DesiredRate - InputRate) > 1e-9)
                UpdateAndPropagateRate(InputRate);
            if (Math.Abs(SignalRate - InputRate) > 1e-9)
                UpdateAndPropagateSignalRate(InputRate);
        }

        Publish(result);
        Viz.Feed(result);
    }

    public static WindowRms ConfigureInput(IServiceProvider services, JsonModel model)
        => ActivatorUtilities.CreateInstance<WindowRms>(
            services, model.Name ?? "RMS", model.DesiredRate ?? 0d);

    protected override string JsonTypeName => "windowrms";

    protected override void Dispose(bool disposing)
    {
        // BlockVisualization.Dispose is idempotent, as is the base cleanup.
        if (disposing)
            Viz.Dispose();
        base.Dispose(disposing);
    }
}
