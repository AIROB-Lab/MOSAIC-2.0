using System;
using System.Collections.Generic;

namespace MOSAIC.Components.Devices.Delsys;

/// <summary>
/// Fixes quaternion sign discontinuities (hemisphere flips) on a per-sensor basis.
/// </summary>
public sealed class QuaternionFixer
{
    private readonly Dictionary<Guid, double[]> _prev = new();

    public void Fix(Guid sensorId, ref double w, ref double x, ref double y, ref double z)
    {
        if (_prev.TryGetValue(sensorId, out var prev))
        {
            double dot = prev[0] * w + prev[1] * x + prev[2] * y + prev[3] * z;
            if (dot < 0) { w = -w; x = -x; y = -y; z = -z; }
        }

        _prev[sensorId] = [w, x, y, z];
    }

    public void Reset() => _prev.Clear();
}