using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.ViewModels.Devices;
using MOSAIC.Visualization;

namespace MOSAIC.Tests.TestSupport;

/// <summary>
/// Assembly-wide test bootstrap. <see cref="AssemblyInitialize"/> runs exactly once, before any
/// test in the assembly, so this is the safe place to configure process-wide switches.
/// </summary>
[TestClass]
public static class TestBootstrap
{
    /// <summary>
    /// Disables <see cref="BlockVisualization"/>'s chart monitors for the whole test run. In a
    /// headless test process there is no window to draw on, and building the LiveCharts controls
    /// triggers a non-thread-safe one-time global init that corrupts under parallel execution.
    /// With the monitors off, constructing any block is cheap and side-effect-free, which is what
    /// lets the suite run in parallel again.
    /// </summary>
    [AssemblyInitialize]
    public static void Init(TestContext context)
    {
        BlockVisualization.Enabled = false;

        // The BodyRig card falls back to the calibration profiles shipped beside the executable.
        // Those get copied next to the test runner too, which would make every assertion about
        // an unconfigured card depend on the build layout. Tests that want the fallback set this
        // to a folder they control.
        BodyRigViewModel.DefaultProfileDirectory = null;
    }
}
