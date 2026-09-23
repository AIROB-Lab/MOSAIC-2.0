using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.BodyRig;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Tests for the <see cref="BodyRigBlock"/> pipeline block: construction and JSON configuration,
/// quaternion-mode ingestion, the published output layout, kinematic-chain topology edits, and
/// calibration file round-tripping.
/// </summary>
/// <remarks>
/// The serial path is not exercised here — it needs a real <c>SerialPort</c> and an RN41 dongle.
/// What <em>is</em> covered is that a clock tick with no port open degrades gracefully instead of
/// throwing, which is the state the block is in whenever the hardware is absent.
/// </remarks>
[TestClass]
public class BodyRigTests
{
    private const int ValuesPerSegment = 7; // W, X, Y, Z, posX, posY, posZ
    private const float Tol = 1e-4f;

    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosaic-bodyrig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    /// <summary>Builds a flat quaternion vector: 4 values (w, x, y, z) per sensor.</summary>
    private static Vector<double> Quaternions(params QuaternionF[] qs)
    {
        var data = new double[qs.Length * 4];
        for (int i = 0; i < qs.Length; i++)
        {
            data[i * 4 + 0] = qs[i].W;
            data[i * 4 + 1] = qs[i].X;
            data[i * 4 + 2] = qs[i].Y;
            data[i * 4 + 3] = qs[i].Z;
        }
        return Vector<double>.Build.DenseOfArray(data);
    }

    // ── Construction & configuration ────────────────────────────────────────

    [TestMethod]
    public void Constructor_BuildsAUsableChainWithoutJsonConfiguration()
    {
        // A block dragged in from the palette never goes through ConfigureInput, so the
        // constructor alone has to leave a chain behind — otherwise every accessor and
        // the whole output buffer are empty.
        using var block = new BodyRigBlock();

        Assert.AreEqual(10, block.NumOfChannels);
        Assert.AreEqual(10, block.SegmentCount);
        Assert.AreEqual(100d, block.DesiredRate);
    }

    [TestMethod]
    public void Constructor_SeedsEachSegmentWithItsOwnSensorIndex()
    {
        using var block = new BodyRigBlock();

        for (int i = 0; i < block.SegmentCount; i++)
            Assert.AreEqual(i, block.GetSensorIndex(i), $"segment {i}");
    }

    [TestMethod]
    public void NumOfChannels_Assignment_RebuildsTheChain()
    {
        // The card's CH box writes straight to this property. If the chain did not follow,
        // every later index would address a stale array.
        using var block = new BodyRigBlock();

        block.NumOfChannels = 4;

        Assert.AreEqual(4, block.SegmentCount);
        Assert.AreEqual(-1, block.GetSensorIndex(4), "segment 4 should no longer exist");
    }

    [TestMethod]
    public void NumOfChannels_IsClampedToAtLeastOne()
    {
        using var block = new BodyRigBlock();

        block.NumOfChannels = 0;

        Assert.AreEqual(1, block.NumOfChannels);
        Assert.AreEqual(1, block.SegmentCount);
    }

    [TestMethod]
    public void ConfigureInput_ReadsPortChannelsAndCalibrationDirectory()
    {
        var dir = NewTempDir();
        var model = new JsonModel
        {
            Type = "BodyRig",
            Name = "Rig",
            DesiredRate = 250,
            Params = ["COM7", "6", dir]
        };

        using var block = BodyRigBlock.ConfigureInput(Services(), model);

        Assert.AreEqual("Rig", block.Name);
        Assert.AreEqual(250d, block.DesiredRate);
        Assert.AreEqual("COM7", block.PortNumber);
        Assert.AreEqual(6, block.NumOfChannels);
        Assert.AreEqual(6, block.SegmentCount);
    }

    [TestMethod]
    public void ConfigureInput_WithNoParams_FallsBackToDefaults()
    {
        using var block = BodyRigBlock.ConfigureInput(Services(), new JsonModel { Type = "BodyRig" });

        Assert.AreEqual("BodyRig", block.Name);
        Assert.AreEqual(10, block.NumOfChannels);
        Assert.AreEqual(10, block.SegmentCount);
        Assert.AreEqual(string.Empty, block.PortNumber);
    }

    [TestMethod]
    public void ConfigureInput_WithPartialParams_AppliesWhatItCan()
    {
        // The old guard was `Count >= 3`, so a two-param config silently discarded the
        // port and the channel count as well.
        using var block = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["COM3", "5"] });

        Assert.AreEqual("COM3", block.PortNumber);
        Assert.AreEqual(5, block.NumOfChannels);
        Assert.AreEqual(5, block.SegmentCount);
    }

    [TestMethod]
    public void ConfigureInput_WithUnparseableChannelCount_KeepsTheDefault()
    {
        using var block = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["COM3", "not-a-number", ""] });

        Assert.AreEqual(10, block.NumOfChannels);
    }

    // ── Quaternion-mode ingestion & output layout ───────────────────────────

    [TestMethod]
    public void Publishes_SevenValuesPerSegment()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;

        var result = BlockHarness.CaptureVector(block, Quaternions(
            QuaternionF.Identity, QuaternionF.Identity, QuaternionF.Identity));

        Assert.HasCount(3 * ValuesPerSegment, result);
    }

    [TestMethod]
    public void Publishes_OrientationThenPositionPerSegment()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 1;
        block.SetDhAngles(0, 0, 0, 0);                     // no DH offset
        block.SetLinkLength(0, 1, 0, 0);                   // unit link along +X

        // 90° yaw should swing the unit link from +X onto +Y.
        var result = BlockHarness.CaptureVector(block, Quaternions(QuaternionF.FromEulerDegrees(0, 0, 90)));

        Assert.AreEqual(MathF.Cos(MathF.PI / 4f), result[0], 1e-4, "W");
        Assert.AreEqual(0d, result[1], 1e-4, "X");
        Assert.AreEqual(0d, result[2], 1e-4, "Y");
        Assert.AreEqual(MathF.Sin(MathF.PI / 4f), result[3], 1e-4, "Z");
        Assert.AreEqual(0d, result[4], 1e-4, "posX");
        Assert.AreEqual(1d, result[5], 1e-4, "posY");
        Assert.AreEqual(0d, result[6], 1e-4, "posZ");
    }

    [TestMethod]
    public void QuaternionInput_UpdatesTheSegmentBoundToThatSensor()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;
        block.SetDhAngles(0, 0, 0, 0);
        block.SetDhAngles(1, 0, 0, 0);

        // Sensor 0 stays level, sensor 1 yaws 90°.
        BlockHarness.CaptureVector(block, Quaternions(
            QuaternionF.Identity, QuaternionF.FromEulerDegrees(0, 0, 90)));

        var seg0 = block.GetSegment(0)!;
        var seg1 = block.GetSegment(1)!;

        Assert.AreEqual(0f, QuaternionF.Yaw(seg0.Orientation) * 180f / MathF.PI, 1e-2f);
        Assert.AreEqual(90f, QuaternionF.Yaw(seg1.Orientation) * 180f / MathF.PI, 1e-2f);
    }

    [TestMethod]
    public void QuaternionInput_UpdatesNumOfDevices()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 5;

        BlockHarness.CaptureVector(block, Quaternions(
            QuaternionF.Identity, QuaternionF.Identity, QuaternionF.Identity));

        Assert.AreEqual(3, block.NumOfDevices);
    }

    [TestMethod]
    public void QuaternionInput_ShorterThanTheChain_LeavesTheRestUntouched()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 4;

        var result = BlockHarness.CaptureVector(block, Quaternions(QuaternionF.Identity));

        Assert.HasCount(4 * ValuesPerSegment, result);
    }

    [TestMethod]
    public void QuaternionInput_WithARaggedLength_IgnoresThePartialGroup()
    {
        // A vector whose length is not a multiple of four used to index past its end.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        var ragged = Vector<double>.Build.DenseOfArray([1, 0, 0, 0, 1, 0]); // 6 values

        var result = BlockHarness.CaptureVector(block, ragged);

        Assert.HasCount(2 * ValuesPerSegment, result);
        Assert.AreEqual(1, block.NumOfDevices);
    }

    [TestMethod]
    public void QuaternionInput_ThatIsEmpty_StillPublishes()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        var result = BlockHarness.CaptureVector(block, Vector<double>.Build.Dense(0));

        Assert.HasCount(2 * ValuesPerSegment, result);
    }

    [TestMethod]
    public void NonVectorInput_IsIgnoredButStillPublishes()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        var result = BlockHarness.CaptureVector(block, "not a quaternion vector");

        Assert.HasCount(2 * ValuesPerSegment, result);
    }

    [TestMethod]
    public void ClockTick_WithNoSerialPortOpen_PublishesWithoutThrowing()
    {
        using var block = new BodyRigBlock();
        using var clock = new ClockBlock();
        block.NumOfChannels = 2;

        var capture = new CaptureSubscriber();
        block.AddSubscriber(capture);
        block.ReceiveInput(clock, 0);

        Assert.IsTrue(capture.Wait(2000), "Block did not publish on a clock tick.");
        Assert.IsInstanceOfType<Vector<double>>(capture.Captured);
        Assert.HasCount(2 * ValuesPerSegment, (Vector<double>)capture.Captured!);
    }

    [TestMethod]
    public void NonFiniteInput_DoesNotPoisonThePublishedSample()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 1;
        block.SetDhAngles(0, 0, 0, 0);
        block.SetLinkLength(0, 1, 0, 0);

        // A good sample first, then a NaN burst: the last good values must survive.
        BlockHarness.CaptureVector(block, Quaternions(QuaternionF.Identity));

        var poisoned = Vector<double>.Build.DenseOfArray([double.NaN, double.NaN, double.NaN, double.NaN]);
        var result = BlockHarness.CaptureVector(block, poisoned);

        for (int i = 0; i < result.Count; i++)
            Assert.IsFalse(double.IsNaN(result[i]) || double.IsInfinity(result[i]),
                $"index {i} was {result[i]}");
    }

    [TestMethod]
    public void FramesProcessed_CountsEveryPublish()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 1;

        BlockHarness.CaptureVector(block, Quaternions(QuaternionF.Identity));
        BlockHarness.CaptureVector(block, Quaternions(QuaternionF.Identity));

        Assert.AreEqual(2, block.Info.FramesProcessed);
    }

    // ── Segment accessors ───────────────────────────────────────────────────

    [TestMethod]
    public void LinkLength_RoundTripsThroughTheAccessors()
    {
        using var block = new BodyRigBlock();

        block.SetLinkLength(2, 0.31f, -0.12f, 0.05f);

        var link = block.GetLinkLengths(2);
        Assert.AreEqual(0.31f, link.X, Tol);
        Assert.AreEqual(-0.12f, link.Y, Tol);
        Assert.AreEqual(0.05f, link.Z, Tol);
    }

    [TestMethod]
    public void DhAngles_RoundTripThroughTheAccessors()
    {
        // This is the exact read-modify-write the segment inspector performs.
        using var block = new BodyRigBlock();
        const float rad2deg = 180f / MathF.PI;

        block.SetDhAngles(1, 20f, -35f, 100f);

        var dh = block.GetDhAngles(1);
        Assert.AreEqual(20f, dh.X * rad2deg, 1e-2f, "roll");
        Assert.AreEqual(-35f, dh.Y * rad2deg, 1e-2f, "pitch");
        Assert.AreEqual(100f, dh.Z * rad2deg, 1e-2f, "yaw");
    }

    [TestMethod]
    public void SensorIndex_RoundTripsThroughTheAccessors()
    {
        using var block = new BodyRigBlock();

        block.SetSensorIndex(3, 7);

        Assert.AreEqual(7, block.GetSensorIndex(3));
    }

    [TestMethod]
    public void Accessors_OutsideTheChain_ReturnNeutralValuesInsteadOfThrowing()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        Assert.AreEqual(Vector3F.Zero, block.GetLinkLengths(99));
        Assert.AreEqual(Vector3F.Zero, block.GetDhAngles(-1));
        Assert.AreEqual(-1, block.GetSensorIndex(99));
        Assert.AreEqual(-1, block.GetParentIndex(99));
        Assert.IsNull(block.GetSegment(99));

        // Setters on an out-of-range index must be no-ops, not exceptions.
        block.SetLinkLength(99, 1, 1, 1);
        block.SetDhAngles(99, 1, 1, 1);
        block.SetSensorIndex(99, 1);
        block.SetParent(99, 0);
    }

    [TestMethod]
    public void OrientationRelativeTo_IsIdentityForASegmentAgainstItself()
    {
        using var block = new BodyRigBlock();

        BlockHarness.CaptureVector(block, Quaternions(QuaternionF.FromEulerDegrees(10, 20, 30)));

        var rel = block.OrientationRelativeTo(0, 0);
        Assert.AreEqual(1f, MathF.Abs(rel.W), 1e-4f);
    }

    [TestMethod]
    public void OrientationRelativeTo_OutOfRange_ReturnsIdentity()
    {
        using var block = new BodyRigBlock();

        Assert.AreEqual(QuaternionF.Identity, block.OrientationRelativeTo(0, 99));
        Assert.AreEqual(QuaternionF.Identity, block.OrientationRelativeTo(-1, 0));
    }

    // ── Chain topology ──────────────────────────────────────────────────────

    [TestMethod]
    public void SetParent_LinksBothDirections()
    {
        using var block = new BodyRigBlock();

        block.SetParent(childIndex: 1, parentIndex: 0);

        Assert.AreEqual(0, block.GetParentIndex(1));
        Assert.AreSame(block.GetSegment(1), block.GetSegment(0)!.Children.Single());
        Assert.AreSame(block.GetSegment(0), block.GetSegment(1)!.Parent);
    }

    [TestMethod]
    public void SetParent_ToMinusOne_DetachesFromBothSides()
    {
        using var block = new BodyRigBlock();
        block.SetParent(1, 0);

        block.SetParent(1, -1);

        Assert.AreEqual(-1, block.GetParentIndex(1));
        Assert.IsNull(block.GetSegment(1)!.Parent);
        Assert.IsEmpty(block.GetSegment(0)!.Children);
    }

    [TestMethod]
    public void SetParent_Reparenting_ClearsTheOldParentsChildLink()
    {
        // Leaving the old parent pointing at a segment it no longer owns corrupts
        // the recursive forward-kinematics walk.
        using var block = new BodyRigBlock();
        block.SetParent(2, 0);

        block.SetParent(2, 1);

        Assert.AreEqual(1, block.GetParentIndex(2));
        Assert.IsEmpty(block.GetSegment(0)!.Children, "old parent still claims the child");
        Assert.AreSame(block.GetSegment(2), block.GetSegment(1)!.Children.Single());
    }

    [TestMethod]
    public void SetParent_RejectsSelfParenting()
    {
        using var block = new BodyRigBlock();

        block.SetParent(1, 1);

        Assert.AreEqual(-1, block.GetParentIndex(1));
    }

    [TestMethod]
    public void SetParent_RejectsACycle()
    {
        using var block = new BodyRigBlock();
        block.SetParent(1, 0);   // 0 → 1

        block.SetParent(0, 1);   // would close the loop

        Assert.AreEqual(-1, block.GetParentIndex(0));
        Assert.AreEqual(0, block.GetParentIndex(1));
    }

    // ── Calibration ─────────────────────────────────────────────────────────

    [TestMethod]
    public void StoreCalibration_ThenLoadCalibration_RestoresTheChain()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "profile.txt");

        using (var source = BodyRigBlock.ConfigureInput(Services(),
                   new JsonModel { Type = "BodyRig", Params = ["", "3", dir] }))
        {
            source.SetLinkLength(0, 0.25f, 0.1f, -0.05f);
            source.SetDhAngles(0, 10f, -20f, 30f);
            source.SetSensorIndex(0, 2);
            source.SetParent(1, 0);
            source.SetSensorIndex(1, 5);

            source.StoreCalibration(file);
        }

        Assert.IsTrue(File.Exists(file), "calibration file was not written");

        using var target = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["", "3", dir] });
        target.LoadCalibration();

        var link = target.GetLinkLengths(0);
        Assert.AreEqual(0.25f, link.X, Tol);
        Assert.AreEqual(0.1f, link.Y, Tol);
        Assert.AreEqual(-0.05f, link.Z, Tol);

        const float rad2deg = 180f / MathF.PI;
        var dh = target.GetDhAngles(0);
        Assert.AreEqual(10f, dh.X * rad2deg, 1e-2f, "roll");
        Assert.AreEqual(-20f, dh.Y * rad2deg, 1e-2f, "pitch");
        Assert.AreEqual(30f, dh.Z * rad2deg, 1e-2f, "yaw");

        Assert.AreEqual(2, target.GetSensorIndex(0));
        Assert.AreEqual(5, target.GetSensorIndex(1));
        Assert.AreEqual(0, target.GetParentIndex(1));
    }

    [TestMethod]
    public void LoadCalibration_RestoresTheChildLinkAsWellAsTheParent()
    {
        // Assigning Parent alone (as the loader used to) leaves every Child null, so a
        // loaded chain never propagates an update past its root.
        var dir = NewTempDir();
        var file = Path.Combine(dir, "profile.txt");

        using (var source = BodyRigBlock.ConfigureInput(Services(),
                   new JsonModel { Type = "BodyRig", Params = ["", "3", dir] }))
        {
            source.SetParent(1, 0);
            source.StoreCalibration(file);
        }

        using var target = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["", "3", dir] });
        target.LoadCalibration();

        Assert.AreSame(target.GetSegment(1), target.GetSegment(0)!.Children.Single());
    }

    [TestMethod]
    public void StoreCalibration_WritesOneInvariantCultureLinePerSegment()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "profile.txt");

        using var block = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["", "3", dir] });
        block.SetLinkLength(0, 0.5f, 0, 0);
        block.StoreCalibration(file);

        var lines = File.ReadAllLines(file);
        Assert.HasCount(3, lines);

        // Ten fields: the nine geometry/topology values plus the segment name, which is last so
        // that a name containing the separator survives without escaping.
        Assert.HasCount(10, lines[0].Split(':'));

        // A decimal-comma locale would write "0,5" and make the colon-separated file
        // unparseable by LoadCalibration.
        Assert.AreEqual(0.5f, float.Parse(lines[0].Split(':')[0], CultureInfo.InvariantCulture), Tol);
    }

    [TestMethod]
    public void LoadCalibration_WithNoCalibrationFile_IsANoOp()
    {
        using var block = new BodyRigBlock();

        block.LoadCalibration(); // must not throw

        Assert.IsNull(block.CalibrationName);
    }

    [TestMethod]
    public void LoadCalibration_WithMalformedLines_SkipsThemAndKeepsGoing()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "profile.txt");
        File.WriteAllLines(file, ["garbage", "0.5:0:0:1:0:0:0:-1:4"]);

        using var block = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["", "3", dir] });
        block.LoadCalibration();

        Assert.AreEqual(4, block.GetSensorIndex(1), "the well-formed second line should still apply");
    }

    [TestMethod]
    public void ConfigureInput_DiscoversCalibrationProfilesInTheDirectory()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "0:0:0:1:0:0:0:-1:0\n");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "0:0:0:1:0:0:0:-1:1\n");

        using var block = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["", "2", dir] });

        Assert.HasCount(2, block.Profiles);
        Assert.IsNotNull(block.CalibrationName);
    }

    [TestMethod]
    public void NextProfile_And_PreviousProfile_BothWrapAround()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "0:0:0:1:0:0:0:-1:0\n");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "0:0:0:1:0:0:0:-1:1\n");

        using var block = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Params = ["", "2", dir] });

        var first = block.CalibrationName;

        block.NextProfile();
        var second = block.CalibrationName;
        Assert.AreNotEqual(first, second);

        block.NextProfile();
        Assert.AreEqual(first, block.CalibrationName, "Next should wrap back to the first profile");

        // Previous from the first entry has to wrap to the last, not stick at index 0.
        block.PreviousProfile();
        Assert.AreEqual(second, block.CalibrationName, "Previous should wrap to the last profile");
    }

    [TestMethod]
    public void ProfileNavigation_WithNoProfiles_IsANoOp()
    {
        using var block = new BodyRigBlock();

        block.NextProfile();
        block.PreviousProfile();

        Assert.IsEmpty(block.Profiles);
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Connect_WithNoPortConfigured_FlagsItInsteadOfThrowing()
    {
        using var block = new BodyRigBlock();

        block.Connect();

        Assert.AreEqual("No port configured", block.Info.Flag);
    }

    [TestMethod]
    public void Disconnect_WithoutConnecting_IsSafe()
    {
        using var block = new BodyRigBlock();

        block.Disconnect();

        Assert.AreEqual("Disconnected", block.Info.Flag);
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var block = new BodyRigBlock();

        block.Dispose();
        block.Dispose(); // must not throw on the already-closed UDP client
    }

    /// <summary>Captures the first published value; used where the sender must not be the block itself.</summary>
    private sealed class CaptureSubscriber : MOSAIC.Components.Interfaces.ISubscriber
    {
        private readonly System.Threading.ManualResetEventSlim _received = new(false);
        public object? Captured { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Captured = value;
            _received.Set();
        }

        public bool Wait(int timeoutMs) => _received.Wait(timeoutMs);
    }
}
