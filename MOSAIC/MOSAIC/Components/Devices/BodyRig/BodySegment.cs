using System;
using System.Collections.Generic;
using System.Linq;

namespace MOSAIC.Components.BodyRig;

/// <summary>
/// Represents a single rigid segment in a kinematic chain, storing its orientation
/// (global and local), position, and relationship to parent/child segments.
/// </summary>
/// <remarks>
/// <para>
/// Each segment holds a <see cref="DhRelativeToGyro"/> quaternion that maps from
/// the sensor (gyroscope/IMU) frame to the segment's Denavit–Hartenberg frame.
/// When <see cref="UpdateOrientation"/> is called with a new sensor quaternion,
/// the segment computes its global orientation, and — if a parent exists — derives
/// local orientation and position via forward kinematics.
/// </para>
/// <para>
/// The kinematic chain is a tree: each segment has one <see cref="Parent"/> and any number of
/// <see cref="Children"/>, so a torso can carry several limbs. Calling
/// <see cref="UpdateOrientation(QuaternionF)"/> propagates recursively down every branch.
/// </para>
/// </remarks>
public class BodySegment
{
    /// <summary>Global (world-frame) orientation of this segment.</summary>
    public QuaternionF Orientation { get; set; } = QuaternionF.Identity;

    /// <summary>
    /// Orientation relative to the parent segment.
    /// For the root segment this equals <see cref="Orientation"/>.
    /// </summary>
    public QuaternionF LocalOrientation { get; set; } = QuaternionF.Identity;

    /// <summary>Optional additional rotation offset applied before the DH transform.</summary>
    public QuaternionF RotationOffset { get; set; } = QuaternionF.Identity;

    /// <summary>Global (world-frame) position of this segment's distal end.</summary>
    public Vector3F Position { get; set; }

    /// <summary>
    /// Position relative to the parent segment's frame.
    /// For the root segment this equals <see cref="Position"/>.
    /// </summary>
    public Vector3F LocalPosition { get; set; }

    /// <summary>
    /// Link length vector that defines the offset from this segment's proximal joint
    /// to its distal end, expressed in the segment's own frame.
    /// </summary>
    public Vector3F LinkLength { get; set; } = new(1, 0, 0);

    /// <summary>Parent segment in the kinematic chain, or <see langword="null"/> for the root.</summary>
    public BodySegment? Parent { get; set; }

    private readonly List<BodySegment> _children = [];

    /// <summary>Child segments hanging off this one. Empty for a terminal segment.</summary>
    /// <remarks>
    /// A list rather than a single reference because real rigs branch. The lab's
    /// <c>double_hand</c> model hangs two arms off one torso and <c>bimanual</c> hangs three
    /// branches off it; while this was a single <c>Child</c>, attaching the second one severed
    /// the first, so those models loaded as one intact chain plus limbs floating at the origin —
    /// and nothing reported it.
    /// </remarks>
    public IReadOnlyList<BodySegment> Children => _children;

    /// <summary>Adds <paramref name="child"/> to this segment's children, if not already one.</summary>
    /// <remarks>
    /// Deliberately one-directional: it does not touch <see cref="Parent"/>. Maintaining both
    /// links is <c>BodyRig.SetParent</c>'s job and should stay its alone — keeping the two apart
    /// is also what lets <see cref="IsDescendantOf"/>'s loop guard be tested against a
    /// deliberately corrupted graph.
    /// </remarks>
    public void AddChild(BodySegment child)
    {
        if (child is null || ReferenceEquals(child, this)) return;
        if (!_children.Any(c => ReferenceEquals(c, child))) _children.Add(child);
    }

    /// <summary>Removes <paramref name="child"/> from this segment's children.</summary>
    public void RemoveChild(BodySegment child) =>
        _children.RemoveAll(c => ReferenceEquals(c, child));
    

    /// <summary>
    /// Human-readable name for this segment, e.g. "rightForeArm". Optional, but a chain of
    /// segments called "segment 0".."segment 9" tells a reader nothing about the body it models.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Sensor or hardware index associated with this segment (e.g. IMU slot number).</summary>
    public int SensorIndex { get; set; } = -1;

    /// <summary>Ordinal position in the kinematic chain (0 = root).</summary>
    public int OrdinalIndex { get; set; } = -1;
    

    /// <summary>
    /// Quaternion that maps from the sensor (IMU/gyroscope) frame to the segment's
    /// Denavit–Hartenberg reference frame. Set during construction or calibration.
    /// </summary>
    public QuaternionF DhRelativeToGyro { get; private set; }

    private QuaternionF _poseForCalibration = QuaternionF.Identity;

    /// <summary>
    /// Initialises a body segment with a default DH offset of −90° yaw (about Z)
    /// and a unit link along the X axis.
    /// </summary>
    public BodySegment()
    {
        DhRelativeToGyro = QuaternionF.FromEulerDegrees(0, 0, -90);
        Position = LinkLength;
        LocalPosition = Position;
    }

    /// <summary>
    /// Initialises a body segment with explicit DH Euler angles (degrees) and link length.
    /// </summary>
    /// <param name="dhRoll">DH roll angle (about X) in degrees.</param>
    /// <param name="dhPitch">DH pitch angle (about Y) in degrees.</param>
    /// <param name="dhYaw">DH yaw angle (about Z) in degrees.</param>
    /// <param name="linkX">Link length X component.</param>
    /// <param name="linkY">Link length Y component.</param>
    /// <param name="linkZ">Link length Z component.</param>
    public BodySegment(float dhRoll, float dhPitch, float dhYaw,
                       float linkX, float linkY, float linkZ)
    {
        DhRelativeToGyro = QuaternionF.FromEulerDegrees(dhRoll, dhPitch, dhYaw);
        LinkLength = new Vector3F(linkX, linkY, linkZ);
        Position = LinkLength;
        LocalPosition = Position;
    }

    /// <summary>
    /// Sets the reference pose quaternion used during <see cref="Calibrate(QuaternionF)"/>.
    /// </summary>
    /// <param name="pose">The desired orientation the segment should represent at calibration.</param>
    public void SetCalibrationPose(QuaternionF pose) =>
        _poseForCalibration = pose;

    /// <summary>
    /// Calibrates the DH-to-sensor mapping by recording the current sensor orientation.
    /// After calibration, <see cref="DhRelativeToGyro"/> is set so that the calibration
    /// pose corresponds to the recorded sensor reading.
    /// </summary>
    /// <param name="currentSensorOrientation">
    /// The raw quaternion from the IMU at the moment of calibration.
    /// </param>
    public void Calibrate(QuaternionF currentSensorOrientation)
    {
        // Normalised for the same reason as SetDh: a non-unit result here becomes a per-update
        // gain on every descendant. The product of two unit quaternions is unit in exact
        // arithmetic, but neither input is guaranteed unit — the sensor reading arrives
        // unvalidated and the calibration pose is caller-supplied.
        DhRelativeToGyro =
            (_poseForCalibration.Conjugate() * currentSensorOrientation).Conjugate().Normalized();
    }

    /// <summary>
    /// Calibrates from a float array in <c>[W, X, Y, Z]</c> order (Muovi/Delsys convention).
    /// </summary>
    /// <param name="wxyz">A 4-element array: <c>{ W, X, Y, Z }</c>.</param>
    public void Calibrate(float[] wxyz) =>
        Calibrate(new QuaternionF(wxyz[1], wxyz[2], wxyz[3], wxyz[0]));

    /// <summary>
    /// Calibrates from individual components in <c>(X, Y, Z, W)</c> order.
    /// </summary>
    public void Calibrate(float x, float y, float z, float w) =>
        Calibrate(new QuaternionF(x, y, z, w));
    

    /// <summary>
    /// Updates this segment's orientation from a new sensor reading and propagates
    /// forward kinematics down the chain.
    /// </summary>
    /// <param name="sensorOrientation">
    /// The raw quaternion from the IMU sensor.
    /// </param>
    /// <remarks>
    /// <para>
    /// The global orientation is computed as <c>sensorOrientation × DhRelativeToGyro</c>.
    /// If a <see cref="Parent"/> exists, <see cref="LocalOrientation"/> and
    /// <see cref="Position"/> are derived relative to the parent.
    /// </para>
    /// <para>
    /// Every segment in <see cref="Children"/> is then recursively updated, so each branch
    /// of the chain stays geometrically consistent with this segment's new tip.
    /// </para>
    /// </remarks>
    public void UpdateOrientation(QuaternionF sensorOrientation)
    {
        Orientation = sensorOrientation * DhRelativeToGyro;

        if (Parent is not null)
        {
            LocalOrientation = Parent.Orientation.Conjugate() * Orientation;
            Position = Parent.Position + Orientation * LinkLength;
            LocalPosition = Parent.Orientation.Conjugate() * (Parent.Position - Position);
        }
        else
        {
            Position = Orientation * LinkLength;
            LocalPosition = Position;
            LocalOrientation = Orientation;
        }

        // Propagate down every branch of the chain. Each child is re-driven with its own last
        // sensor reading — its orientation with its DH offset backed out — so that it recomputes
        // its position against this segment's new tip without needing a fresh packet.
        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            child.UpdateOrientation(child.Orientation * child.DhRelativeToGyro.Conjugate());
        }
    }

    /// <summary>
    /// Updates orientation from a float array in <c>[W, X, Y, Z]</c> order.
    /// </summary>
    /// <param name="wxyz">A 4-element array: <c>{ W, X, Y, Z }</c>.</param>
    public void UpdateOrientation(float[] wxyz) =>
        UpdateOrientation(new QuaternionF(wxyz[1], wxyz[2], wxyz[3], wxyz[0]));

    /// <summary>
    /// Updates orientation from individual components in <c>(X, Y, Z, W)</c> order.
    /// </summary>
    public void UpdateOrientation(float x, float y, float z, float w) =>
        UpdateOrientation(new QuaternionF(x, y, z, w));

    /// <summary>
    /// Computes 3-DOF joint angles for a shoulder-like joint from <see cref="LocalOrientation"/>.
    /// </summary>
    /// <returns>
    /// Array of three angles in degrees: <c>[roll, pitch, yaw]</c> of the local orientation.
    /// </returns>
    public float[] InverseKinematicsArm()
    {
        const float rad2deg = 180f / MathF.PI;
        return new[]
        {
            QuaternionF.Roll(LocalOrientation)  * rad2deg,
            QuaternionF.Pitch(LocalOrientation) * rad2deg,
            QuaternionF.Yaw(LocalOrientation)   * rad2deg
        };
    }

    /// <summary>
    /// Computes 2-DOF joint angles for an elbow-like joint from <see cref="LocalPosition"/>
    /// and <see cref="LocalOrientation"/>.
    /// </summary>
    /// <returns>
    /// Array of two angles in degrees: <c>[flexion/extension, pronation/supination]</c>.
    /// </returns>
    public float[] InverseKinematicsElbow()
    {
        const float rad2deg = 180f / MathF.PI;
        return new[]
        {
            MathF.Atan2(-LocalPosition.Y, LocalPosition.X) * rad2deg,
            QuaternionF.Roll(LocalOrientation) * rad2deg
        };
    }

    /// <summary>
    /// Sets the DH-to-sensor mapping from Euler angles in degrees, in the
    /// <c>(roll, pitch, yaw)</c> order produced by <see cref="QuaternionF.ToEulerAngles"/>.
    /// </summary>
    public void SetDh(float roll, float pitch, float yaw) =>
        DhRelativeToGyro = QuaternionF.FromEulerDegrees(roll, pitch, yaw);

    /// <summary>
    /// Sets the DH-to-sensor mapping from explicit quaternion components <c>(W, X, Y, Z)</c>.
    /// </summary>
    /// <remarks>
    /// Normalised on the way in, and this is not cosmetic. The chain propagation in
    /// <see cref="UpdateOrientation(QuaternionF)"/> hands a child <c>O · D̄</c> and the child
    /// immediately recomputes <c>(O · D̄) · D</c>. Because <c>D̄ · D = |D|²</c> rather than 1,
    /// a non-unit <c>D</c> multiplies the child's quaternion by <c>|D|²</c> on <em>every</em>
    /// parent update: at the rates these rigs run, |D| = 0.975 drives the limb to the origin
    /// within a second, and |D| &gt; 1 overflows to infinity — where the finiteness guard in
    /// <c>BodyRig.UpdateSampleBuffer</c> then silently republishes stale values while the frame
    /// counter keeps rising, so the rig looks healthy and is frozen.
    /// Values reach here unvalidated from <c>LoadCalibration</c>, which parses four raw floats
    /// out of a text file.
    /// </remarks>
    public void SetDh(float w, float x, float y, float z) =>
        DhRelativeToGyro = new QuaternionF(x, y, z, w).Normalized();

    /// <summary>
    /// Tests whether this segment is a descendant of <paramref name="potentialAncestor"/>
    /// anywhere in the kinematic chain.
    /// </summary>
    public bool IsDescendantOf(BodySegment potentialAncestor)
    {
        // Depth-first over the subtree. Floyd's algorithm no longer applies now that a segment
        // can have several children — the structure is a tree, not a list — but a graph corrupted
        // by direct AddChild calls can still contain a loop, and this is the guard SetParent
        // relies on to reject one, so it must not be the thing that hangs on it.
        var visited = new HashSet<BodySegment>();
        var pending = new Stack<BodySegment>();
        pending.Push(potentialAncestor);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node)) continue;   // already walked, or a loop

            foreach (var child in node._children)
            {
                if (ReferenceEquals(child, this)) return true;
                pending.Push(child);
            }
        }

        return false;
    }

    /// <summary>
    /// Tests whether this segment is the <em>immediate</em> child of <paramref name="potentialParent"/>.
    /// </summary>
    public bool IsChildOf(BodySegment potentialParent) =>
        potentialParent._children.Any(c => ReferenceEquals(c, this));
}