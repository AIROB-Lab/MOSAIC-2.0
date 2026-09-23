using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models.SignalProcessing;

public sealed partial class CrossCorrelation
{
    private bool _fullLagRange;
    public bool UsesLagSummary { get; private set; }
    public int MinLag { get; private set; }
    public int ChannelCount { get; private set; } = 2;
    public int LowPassWindow { get; private set; } = 1;
    public int HighPassWindow { get; private set; } = 1;
    public int MovMedianWindow { get; private set; } = 1;

    // The forward-window filters retain the branch's N-window output convention.
    // Wait for actual overlap at every requested lag; a window of 1 disables a filter.
    public int RequiredSamples => !UsesLagSummary ? MinimumBufferLength :
        (LowPassWindow > 1 ? LowPassWindow : 0) + (HighPassWindow > 1 ? HighPassWindow : 0) +
        (MovMedianWindow > 1 ? MovMedianWindow : 0) + Math.Max(Math.Abs(MinLag), Math.Abs(MaxLag)) + 2;

    private int _selectedChannel1;
    private int _selectedChannel2 = 1;
    public int SelectedChannel1
    {
        get => _selectedChannel1;
        set => SelectChannel(ref _selectedChannel1, value, nameof(SelectedChannel1));
    }
    public int SelectedChannel2
    {
        get => _selectedChannel2;
        set => SelectChannel(ref _selectedChannel2, value, nameof(SelectedChannel2));
    }

    private void SelectChannel(ref int field, int value, string property)
    {
        if (value < -1 || value >= ChannelCount)
            throw new ArgumentOutOfRangeException(property, $"Choose -1 (average) or 0 through {ChannelCount - 1}.");
        lock (_bufferLock)
        {
            if (field == value) return;
            field = value;
            // Never correlate a buffer containing a mixture of the old and new channels.
            _bufCh1.Clear();
            _bufCh2.Clear();
            _counter = 0;
            StatusMessage = $"Warming up: 0/{RequiredSamples} samples";
            OnPropertyChanged(property);
        }
    }

    private static CrossCorrelation ConfigureLagSummary(IServiceProvider sp, JsonModel model)
    {
        var p = model.Params!;
        string lagSpec = p[0]?.ToString()?.Trim() ?? "";
        var lagParts = lagSpec.Split(':');
        if (!lagParts[0].Equals("Lags", StringComparison.OrdinalIgnoreCase) || lagParts.Length is < 2 or > 3)
            throw new ArgumentException("Extended CrossCorrelation Params must start with Lags:max or Lags:min:max.");

        static int Integer(object? value, string label, int min, int max)
        {
            if (!int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < min || number > max)
                throw new ArgumentException($"CrossCorrelation {label} must be an integer between {min} and {max}.");
            return number;
        }

        int maxLag = Integer(lagParts[^1], "maximum lag", lagParts.Length == 2 ? 0 : -100000, 100000);
        int minLag = lagParts.Length == 2 ? -maxLag : Integer(lagParts[1], "minimum lag", -100000, maxLag);
        int buffer = p.Count > 1 ? Integer(p[1], "BufferLen", 2, 1000000) : 1000;
        int every = p.Count > 2 ? Integer(p[2], "ProcessEveryN", 1, int.MaxValue) : 5;
        int channels = p.Count > 3 ? Integer(p[3], "ChannelCount", 2, 4096) : 2;
        int low = 1, high = 1, median = 1, channel1 = 0, channel2 = 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object option in p.Skip(4))
        {
            var parts = (option?.ToString() ?? "").Trim().Split(':');
            if (!seen.Add(parts[0])) throw new ArgumentException($"Duplicate CrossCorrelation option '{parts[0]}'.");
            if (parts[0].Equals("Channels", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
            {
                channel1 = Integer(parts[1], "first channel", -1, channels - 1);
                channel2 = Integer(parts[2], "second channel", -1, channels - 1);
                continue;
            }
            if (parts.Length != 2) throw new ArgumentException($"Invalid CrossCorrelation option '{option}'.");
            int window = Integer(parts[1], parts[0], 1, 100000);
            switch (parts[0].ToLowerInvariant())
            {
                case "lowpass": low = window; break;
                case "highpass": high = window; break;
                case "movmedian": median = window; break;
                default: throw new ArgumentException($"Unknown CrossCorrelation option '{parts[0]}'.");
            }
        }

        var block = new CrossCorrelation(model.Name ?? "CrossCorr", model.DesiredRate ?? 0, 0, buffer, every)
        {
            UsesLagSummary = true,
            MinLag = minLag,
            _maxLag = maxLag,
            _fullLagRange = lagParts.Length == 2 && maxLag == 0,
            ChannelCount = channels,
            LowPassWindow = low,
            HighPassWindow = high,
            MovMedianWindow = median,
            _selectedChannel1 = channel1,
            _selectedChannel2 = channel2
        };
        block._bufferLen = Math.Max(buffer, block.RequiredSamples);
        block.InitChannelDumpers(sp, model.Path);
        return block;
    }

    private IReadOnlyList<object> GetLagSummaryParams() => new List<object>
    {
        _fullLagRange ? "Lags:0" : $"Lags:{MinLag}:{MaxLag}", BufferLen, ProcessEveryN, ChannelCount,
        $"LowPass:{LowPassWindow}", $"HighPass:{HighPassWindow}", $"MovMedian:{MovMedianWindow}",
        $"Channels:{SelectedChannel1}:{SelectedChannel2}"
    };

    private double ReadChannel(Vector<double> values, int offset, int channel)
    {
        if (channel >= 0) return values[offset + channel];
        double sum = 0;
        for (int c = 0; c < ChannelCount; c++) sum += values[offset + c];
        return sum / ChannelCount;
    }

    private double ReadChannel(Matrix<double> values, int row, int channel)
    {
        if (channel >= 0) return values[row, channel];
        double sum = 0;
        for (int c = 0; c < ChannelCount; c++) sum += values[row, c];
        return sum / ChannelCount;
    }

    internal Vector<double> ProcessConfigured(Vector<double> input)
    {
        var result = Normalize(input);
        if (LowPassWindow > 1) result = MovingMean(result, LowPassWindow);
        if (MovMedianWindow > 1) result = MovingMedian(result, MovMedianWindow);
        if (HighPassWindow > 1)
        {
            var mean = MovingMean(result, HighPassWindow);
            result = result.SubVector(0, mean.Count) - mean;
        }
        return result;
    }

    // Linear-time means avoid allocating a vector per window on a tablet.
    private static Vector<double> MovingMean(Vector<double> signal, int window)
    {
        var result = Vector<double>.Build.Dense(signal.Count - window);
        double sum = 0;
        for (int i = 0; i < window; i++) sum += signal[i];
        for (int i = 0; i < result.Count; i++)
        {
            result[i] = sum / window;
            sum += signal[i + window] - signal[i];
        }
        return result;
    }

    private static Vector<double> MovingMedian(Vector<double> signal, int window)
    {
        var result = Vector<double>.Build.Dense(signal.Count - window);
        var sorted = new double[window];
        for (int i = 0; i < result.Count; i++)
        {
            for (int j = 0; j < window; j++) sorted[j] = signal[i + j];
            Array.Sort(sorted);
            result[i] = window % 2 == 0 ? (sorted[window / 2 - 1] + sorted[window / 2]) / 2 : sorted[window / 2];
        }
        return result;
    }

    private Vector<double> BuildLagSummary(Vector<double> correlation, int firstLag, bool flat)
    {
        var output = Vector<double>.Build.Dense(3); // Third value reserved, matching feature_xcorr.
        if (flat || !correlation.All(double.IsFinite) || correlation.L2Norm() < 1e-12)
        {
            StatusMessage = "No usable correlation — lag/rate output is zero.";
            return output;
        }
        int lag = firstLag + correlation.MaximumIndex();
        double rate = DesiredRate > 0 ? DesiredRate : EffectiveSampleRate;
        output[0] = lag;
        output[1] = lag != 0 && double.IsFinite(rate) ? rate / lag : 0;
        if (lag == 0) StatusMessage = "Peak at zero lag — rate/lag is undefined; output is zero.";
        return output;
    }
}
