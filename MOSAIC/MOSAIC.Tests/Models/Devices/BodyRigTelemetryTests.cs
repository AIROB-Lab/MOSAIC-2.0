using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.BodyRig;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Tests for the telemetry the BodyRig card depends on: the drive mode, per-sensor arrival
/// counters, the durable parser-error counter, and JSON round-tripping.
/// </summary>
/// <remarks>
/// These are the signals that make the card honest. Liveness in particular cannot be derived
/// any other way — <see cref="BodyRigBlock.NumOfDevices"/> is only ever assigned on the upstream
/// path, and inferring activity from a changing orientation reports a motionless-but-healthy IMU
/// as dead, which is exactly the state a user checks during setup.
/// </remarks>
[TestClass]
public class BodyRigTelemetryTests
{
    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosaic-rig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static Vector<double> Quaternions(int sensors)
    {
        var data = new double[sensors * 4];
        for (int i = 0; i < sensors; i++) data[i * 4] = 1;   // identity per sensor
        return Vector<double>.Build.DenseOfArray(data);
    }

    // ── Mode ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Mode_StartsIdle()
    {
        using var block = new BodyRigBlock();

        Assert.AreEqual(BodyRigMode.Idle, block.Mode);
    }

    [TestMethod]
    public void Mode_BecomesUpstreamWhenFedAQuaternionVector()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        BlockHarness.CaptureVector(block, Quaternions(2));

        Assert.AreEqual(BodyRigMode.Upstream, block.Mode);
    }

    [TestMethod]
    public void Mode_BecomesSerialWhenDrivenByAClock()
    {
        // The sender type is the only honest source: the block's declared inputs are names,
        // not types, so the mode cannot be read off the configuration.
        using var block = new BodyRigBlock();
        using var clock = new ClockBlock();

        block.ReceiveInput(clock, 0);

        Assert.AreEqual(BodyRigMode.Serial, block.Mode);
    }

    // ── Per-sensor arrival counters ─────────────────────────────────────────

    [TestMethod]
    public void SensorPacketCounts_StartAtZero()
    {
        using var block = new BodyRigBlock();

        for (int s = 0; s < 8; s++) Assert.AreEqual(0, block.GetSensorPacketCount(s), $"sensor {s}");
    }

    [TestMethod]
    public void SensorPacketCounts_IncrementPerArrivingSensor()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;

        BlockHarness.CaptureVector(block, Quaternions(3));

        Assert.AreEqual(1, block.GetSensorPacketCount(0));
        Assert.AreEqual(1, block.GetSensorPacketCount(1));
        Assert.AreEqual(1, block.GetSensorPacketCount(2));
        Assert.AreEqual(0, block.GetSensorPacketCount(3), "sensor 3 sent nothing");
    }

    [TestMethod]
    public void SensorPacketCounts_AccumulateAcrossFrames()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        BlockHarness.CaptureVector(block, Quaternions(2));
        BlockHarness.CaptureVector(block, Quaternions(2));
        BlockHarness.CaptureVector(block, Quaternions(2));

        Assert.AreEqual(3, block.GetSensorPacketCount(0));
    }

    [TestMethod]
    public void SensorPacketCounts_CountArrivalsEvenWhenNoSegmentIsBound()
    {
        // A sensor that arrives but matches no segment is exactly the misconfiguration the
        // card needs to surface, so it has to be counted before the match loop.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;
        block.SetSensorIndex(0, -1);
        block.SetSensorIndex(1, -1);

        BlockHarness.CaptureVector(block, Quaternions(2));

        Assert.AreEqual(1, block.GetSensorPacketCount(0), "arrival is counted regardless of binding");
        Assert.AreEqual(1, block.GetSensorPacketCount(1));
    }

    [TestMethod]
    public void SensorPacketCounts_AreStableForAMotionlessSensor()
    {
        // The point of counting rather than diffing orientations: a still IMU is still alive.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 1;

        BlockHarness.CaptureVector(block, Quaternions(1));
        int first = block.GetSensorPacketCount(0);
        BlockHarness.CaptureVector(block, Quaternions(1));   // byte-identical frame

        Assert.IsGreaterThan(first, block.GetSensorPacketCount(0));
    }

    [TestMethod]
    public void SensorPacketCounts_OutOfRangeIdsAreSafe()
    {
        using var block = new BodyRigBlock();

        Assert.AreEqual(0, block.GetSensorPacketCount(-1));
        Assert.AreEqual(0, block.GetSensorPacketCount(9999));
    }

    // ── Durable error counter ───────────────────────────────────────────────

    [TestMethod]
    public void FrameErrors_StartAtZero()
    {
        using var block = new BodyRigBlock();

        Assert.AreEqual(0, block.Info.FrameErrors);
    }

    // ── Port state ──────────────────────────────────────────────────────────

    [TestMethod]
    public void IsPortOpen_IsFalseWithoutAConnection()
    {
        using var block = new BodyRigBlock();

        Assert.IsFalse(block.IsPortOpen);
    }

    [TestMethod]
    public void IsPortOpen_StaysFalseAfterAFailedConnect()
    {
        // Connect() records the failure and returns normally, so a caller that only watches
        // for an exception would wrongly believe the port opened.
        using var block = new BodyRigBlock { PortNumber = "COM250" };

        block.Connect();

        Assert.IsFalse(block.IsPortOpen);
        Assert.StartsWith("Connection failed", block.Info.Flag);
    }

    // ── Calibration directory ───────────────────────────────────────────────

    [TestMethod]
    public void CalibrationDirectory_IsSettableAtRuntimeAndRescans()
    {
        // Previously only reachable from JSON Params[2], so a palette-dragged block had an
        // empty profile list and the profile arrows were permanent no-ops.
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "0:0:0:1:0:0:0:-1:0\n");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "0:0:0:1:0:0:0:-1:1\n");

        using var block = new BodyRigBlock();
        Assert.IsEmpty(block.Profiles);

        block.CalibrationDirectory = dir;

        Assert.HasCount(2, block.Profiles);
        Assert.AreEqual(dir, block.CalibrationDirectory);
    }

    [TestMethod]
    public void CalibrationDirectory_ClearedByAnEmptyValue()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "0:0:0:1:0:0:0:-1:0\n");

        using var block = new BodyRigBlock { CalibrationDirectory = dir };
        block.CalibrationDirectory = "";

        Assert.IsNull(block.CalibrationDirectory);
        Assert.IsEmpty(block.Profiles);
    }

    [TestMethod]
    public void LoadCalibrationWithAPath_LoadsThatFileAndMakesItCurrent()
    {
        // The parameterless overload reads the block's own current file, which is why a path
        // typed into the card used to be ignored.
        var dir = NewTempDir();
        var wanted = Path.Combine(dir, "b.txt");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "0:0:0:1:0:0:0:-1:0\n");
        File.WriteAllText(wanted, "0.5:0:0:1:0:0:0:-1:4\n");

        using var block = new BodyRigBlock { CalibrationDirectory = dir };
        block.LoadCalibration(wanted);

        Assert.AreEqual(wanted, block.CalibrationName);
        Assert.AreEqual(4, block.GetSensorIndex(0));
        Assert.AreEqual(0.5f, block.GetLinkLengths(0).X, 1e-4f);
    }

    // ── JSON round-trip ─────────────────────────────────────────────────────

    [TestMethod]
    public void ToJsonModel_EmitsThePositionalParamsConfigureInputReadsBack()
    {
        // Without the GetJsonParams override the base returns null, so a saved pipeline
        // records Params: null and silently loses the port, channels and calibration dir.
        var dir = NewTempDir();
        using var block = new BodyRigBlock { PortNumber = "COM7", CalibrationDirectory = dir };
        block.NumOfChannels = 6;

        var model = block.ToJsonModel();

        Assert.AreEqual("BodyRig", model.Type);
        Assert.IsNotNull(model.Params);
        Assert.HasCount(4, model.Params!);
        Assert.AreEqual("COM7", model.Params![0]);
        Assert.AreEqual(6, model.Params![1]);
        Assert.AreEqual(dir, model.Params![2]);

        // Fourth field names the profile in use. Empty here because the directory has none;
        // the directory alone says where profiles live, not which one the rig was built from.
        Assert.AreEqual(string.Empty, model.Params![3]);
    }

    [TestMethod]
    public void ToJsonModel_RecordsTheProfileInUse_AsABareFileName()
    {
        // A path would pin the pipeline to one machine's folder layout; the directory param
        // already carries the location, so this field only has to say which file.
        var dir = NewTempDir();
        using var block = new BodyRigBlock { CalibrationDirectory = dir };
        block.NumOfChannels = 2;
        block.StoreCalibration(Path.Combine(dir, "seated.txt"));

        var model = block.ToJsonModel();

        Assert.AreEqual("seated.txt", model.Params![3]);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesPortChannelsAndDirectory()
    {
        var dir = NewTempDir();
        using var original = new BodyRigBlock { PortNumber = "COM9", CalibrationDirectory = dir };
        original.NumOfChannels = 4;

        var model = original.ToJsonModel() with { Name = "Rig" };
        using var restored = BodyRigBlock.ConfigureInput(
            new ServiceCollection().BuildServiceProvider(), model);

        Assert.AreEqual("COM9", restored.PortNumber);
        Assert.AreEqual(4, restored.NumOfChannels);
        Assert.AreEqual(dir, restored.CalibrationDirectory);
    }

    // ── Segment naming ──────────────────────────────────────────────────────

    [TestMethod]
    public void SegmentName_DefaultsToNullAndRoundTrips()
    {
        using var block = new BodyRigBlock();
        var segment = block.GetSegment(0)!;

        Assert.IsNull(segment.Name);

        segment.Name = "rightForeArm";
        Assert.AreEqual("rightForeArm", block.GetSegment(0)!.Name);
    }
}
