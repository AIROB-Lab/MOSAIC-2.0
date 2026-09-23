using System;

namespace MOSAIC.Components.BodyRig;

/// <summary>
/// An immutable single-precision quaternion used for 3-D orientation representation
/// in body-segment kinematics.
/// </summary>
/// <remarks>
/// <para>
/// Convention: Hamilton product with layout <c>(X, Y, Z, W)</c> where <c>W</c> is the
/// scalar (real) component and <c>(X, Y, Z)</c> is the vector (imaginary) part.
/// </para>
/// <para>
/// Consider replacing with <see cref="System.Numerics.Quaternion"/> if you do not need
/// the custom Euler decomposition or operator overloads defined here.
/// </para>
/// </remarks>
public readonly struct QuaternionF : IEquatable<QuaternionF>
{
    /// <summary>Imaginary X component.</summary>
    public float X { get; }

    /// <summary>Imaginary Y component.</summary>
    public float Y { get; }

    /// <summary>Imaginary Z component.</summary>
    public float Z { get; }

    /// <summary>Scalar (real) W component.</summary>
    public float W { get; }

    /// <summary>The identity quaternion (0, 0, 0, 1).</summary>
    public static readonly QuaternionF Identity = new(0, 0, 0, 1);

    /// <summary>
    /// Initialises a new quaternion with the specified components.
    /// </summary>
    /// <param name="x">Imaginary X.</param>
    /// <param name="y">Imaginary Y.</param>
    /// <param name="z">Imaginary Z.</param>
    /// <param name="w">Scalar W.</param>
    public QuaternionF(float x, float y, float z, float w)
    {
        X = x; Y = y; Z = z; W = w;
    }

    /// <summary>Negates all four components.</summary>
    public static QuaternionF operator -(QuaternionF q) =>
        new(-q.X, -q.Y, -q.Z, -q.W);

    /// <summary>Component-wise addition.</summary>
    public static QuaternionF operator +(QuaternionF a, QuaternionF b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);

    /// <summary>Component-wise subtraction.</summary>
    public static QuaternionF operator -(QuaternionF a, QuaternionF b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);

    /// <summary>Scalar multiplication.</summary>
    public static QuaternionF operator *(QuaternionF q, float s) =>
        new(q.X * s, q.Y * s, q.Z * s, q.W * s);

    /// <summary>Scalar multiplication (scalar on the left).</summary>
    public static QuaternionF operator *(float s, QuaternionF q) => q * s;

    /// <summary>Scalar division.</summary>
    public static QuaternionF operator /(QuaternionF q, float s) =>
        new(q.X / s, q.Y / s, q.Z / s, q.W / s);

    /// <summary>Hamilton quaternion product.</summary>
    public static QuaternionF operator *(QuaternionF a, QuaternionF b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>
    /// Rotates a 3-D vector by this quaternion: <c>q * v * q⁻¹</c>.
    /// </summary>
    /// <param name="q">The rotation quaternion (should be unit length).</param>
    /// <param name="v">The vector to rotate.</param>
    /// <returns>The rotated vector.</returns>
    public static Vector3F operator *(QuaternionF q, Vector3F v)
    {
        var qv = new QuaternionF(v.X, v.Y, v.Z, 0);
        var r = q * qv * q.Conjugate();
        return new Vector3F(r.X, r.Y, r.Z);
    }

    /// <summary>Returns the magnitude (L2 norm) of this quaternion.</summary>
    public float Magnitude() =>
        MathF.Sqrt(W * W + X * X + Y * Y + Z * Z);

    /// <summary>
    /// Returns a unit-length quaternion in the same direction, or
    /// <see cref="Identity"/> if this quaternion has (near-)zero magnitude.
    /// </summary>
    public QuaternionF Normalized()
    {
        float m = Magnitude();
        return m > float.Epsilon ? this / m : Identity;
    }

    /// <summary>Returns the conjugate <c>(-X, -Y, -Z, W)</c>.</summary>
    public QuaternionF Conjugate() => new(-X, -Y, -Z, W);

    /// <summary>Returns the unit-length normalised copy (static convenience).</summary>
    public static QuaternionF Normalize(QuaternionF q) => q.Normalized();

    /// <summary>
    /// Extracts the roll angle (rotation about the X axis) in radians.
    /// </summary>
    public static float Roll(QuaternionF q) =>
        MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z),
                     1f - 2f * (q.X * q.X + q.Y * q.Y));

    /// <summary>
    /// Extracts the pitch angle (rotation about the Y axis) in radians.
    /// Clamped to ±π/2 to avoid NaN from <see cref="MathF.Asin"/>.
    /// </summary>
    public static float Pitch(QuaternionF q)
    {
        float sinp = 2f * (q.W * q.Y - q.Z * q.X);
        return MathF.Abs(sinp) >= 1f
            ? MathF.CopySign(MathF.PI / 2f, sinp)
            : MathF.Asin(sinp);
    }

    /// <summary>
    /// Extracts the yaw angle (rotation about the Z axis) in radians.
    /// </summary>
    public static float Yaw(QuaternionF q) =>
        MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y),
                     1f - 2f * (q.Y * q.Y + q.Z * q.Z));

    /// <summary>
    /// Decomposes the quaternion into Euler angles (roll, pitch, yaw) in radians,
    /// using the intrinsic Z-Y-X (aerospace) convention. Inverse of
    /// <see cref="FromEulerDegrees(float,float,float)"/> (which takes degrees).
    /// </summary>
    /// <returns>A <see cref="Vector3F"/> with <c>(roll, pitch, yaw)</c>.</returns>
    public Vector3F ToEulerAngles() => new(Roll(this), Pitch(this), Yaw(this));

    /// <summary>
    /// Creates a quaternion from Euler angles in <strong>degrees</strong>.
    /// Intrinsic Z-Y-X (aerospace) order: yaw (Z), then pitch (Y), then roll (X),
    /// i.e. <c>q = q_z(yaw) · q_y(pitch) · q_x(roll)</c>.
    /// </summary>
    /// <param name="rollDeg">Rotation about X in degrees.</param>
    /// <param name="pitchDeg">Rotation about Y in degrees.</param>
    /// <param name="yawDeg">Rotation about Z in degrees.</param>
    /// <returns>A unit quaternion representing the combined rotation.</returns>
    /// <remarks>
    /// This is the exact inverse of <see cref="ToEulerAngles"/>: for any angles that avoid
    /// gimbal lock, <c>FromEulerDegrees(r, p, y).ToEulerAngles()</c> returns <c>(r, p, y)</c>
    /// in radians. The argument order therefore matches <see cref="ToEulerAngles"/>'s
    /// <c>(roll, pitch, yaw)</c> result order.
    /// </remarks>
    public static QuaternionF FromEulerDegrees(float rollDeg, float pitchDeg, float yawDeg)
    {
        float roll  = rollDeg  * MathF.PI / 180f;
        float pitch = pitchDeg * MathF.PI / 180f;
        float yaw   = yawDeg   * MathF.PI / 180f;

        float cr = MathF.Cos(roll  * 0.5f), sr = MathF.Sin(roll  * 0.5f);
        float cp = MathF.Cos(pitch * 0.5f), sp = MathF.Sin(pitch * 0.5f);
        float cy = MathF.Cos(yaw   * 0.5f), sy = MathF.Sin(yaw   * 0.5f);

        return new QuaternionF(
            x: sr * cp * cy - cr * sp * sy,
            y: cr * sp * cy + sr * cp * sy,
            z: cr * cp * sy - sr * sp * cy,
            w: cr * cp * cy + sr * sp * sy);
    }

    /// <summary>
    /// Creates a quaternion from Euler angles stored in a <see cref="Vector3F"/> (degrees).
    /// </summary>
    /// <param name="angles">
    /// Euler angles as <c>(roll, pitch, yaw)</c> in degrees — the same component order
    /// <see cref="ToEulerAngles"/> produces.
    /// </param>
    public static QuaternionF FromEulerDegrees(Vector3F angles) =>
        FromEulerDegrees(angles.X, angles.Y, angles.Z);

    /// <inheritdoc/>
    public bool Equals(QuaternionF other) =>
        X == other.X && Y == other.Y && Z == other.Z && W == other.W;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is QuaternionF q && Equals(q);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(QuaternionF a, QuaternionF b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(QuaternionF a, QuaternionF b) => !a.Equals(b);

    /// <inheritdoc/>
    public override string ToString() => $"{X},{Y},{Z},{W}";
}