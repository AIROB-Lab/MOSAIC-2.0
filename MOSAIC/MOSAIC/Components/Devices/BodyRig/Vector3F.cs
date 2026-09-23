using System;

namespace MOSAIC.Components.BodyRig;

/// <summary>
/// An immutable single-precision 3-D vector used for positions and link lengths
/// in body-segment kinematics.
/// </summary>
/// <remarks>
/// Consider replacing with <see cref="System.Numerics.Vector3"/> if you do not need
/// the immutable semantics or custom formatting defined here.
/// </remarks>
public readonly struct Vector3F : IEquatable<Vector3F>
{
    /// <summary>X component.</summary>
    public float X { get; }

    /// <summary>Y component.</summary>
    public float Y { get; }

    /// <summary>Z component.</summary>
    public float Z { get; }

    /// <summary>The zero vector (0, 0, 0).</summary>
    public static readonly Vector3F Zero = new(0, 0, 0);

    /// <summary>
    /// Initialises a new 3-D vector with the specified components.
    /// </summary>
    public Vector3F(float x, float y, float z) { X = x; Y = y; Z = z; }

    /// <summary>Indexer: 0 → X, 1 → Y, 2 → Z.</summary>
    /// <exception cref="IndexOutOfRangeException">Thrown if <paramref name="index"/> is not 0, 1, or 2.</exception>
    public float this[int index] => index switch
    {
        0 => X, 1 => Y, 2 => Z,
        _ => throw new IndexOutOfRangeException($"Vector3F index must be 0–2, got {index}.")
    };

    /// <summary>Negates all components.</summary>
    public static Vector3F operator -(Vector3F v) => new(-v.X, -v.Y, -v.Z);

    /// <summary>Component-wise addition.</summary>
    public static Vector3F operator +(Vector3F a, Vector3F b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Component-wise subtraction.</summary>
    public static Vector3F operator -(Vector3F a, Vector3F b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Scalar multiplication.</summary>
    public static Vector3F operator *(Vector3F v, float s) =>
        new(v.X * s, v.Y * s, v.Z * s);

    /// <summary>Scalar multiplication (scalar on the left).</summary>
    public static Vector3F operator *(float s, Vector3F v) => v * s;

    /// <summary>Scalar division.</summary>
    public static Vector3F operator /(Vector3F v, float s) =>
        new(v.X / s, v.Y / s, v.Z / s);

    /// <summary>Returns the Euclidean length (L2 norm).</summary>
    public float Magnitude() => MathF.Sqrt(X * X + Y * Y + Z * Z);

    /// <summary>Returns a unit-length vector in the same direction.</summary>
    public Vector3F Normalized() => this / Magnitude();

    /// <summary>Dot product of two vectors.</summary>
    public static float Dot(Vector3F a, Vector3F b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Cross product of two vectors.</summary>
    public static Vector3F Cross(Vector3F a, Vector3F b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <inheritdoc/>
    public bool Equals(Vector3F other) =>
        X == other.X && Y == other.Y && Z == other.Z;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Vector3F v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Vector3F a, Vector3F b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Vector3F a, Vector3F b) => !a.Equals(b);

    /// <inheritdoc/>
    public override string ToString() => $"{X,6:0.00},{Y,6:0.00},{Z,6:0.00}";
}