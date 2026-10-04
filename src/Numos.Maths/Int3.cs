namespace Numos.Maths;

/// <summary>
///     Mutable three-component integer vector used for chunk grid positions, local voxel coordinates, and dimensions.
/// </summary>
public struct Int3(int x, int y, int z) : IEquatable<Int3>
{
    public int X = x;
    public int Y = y;
    public int Z = z;

    public readonly static Int3 Zero = new(0, 0, 0);

    public readonly static Int3 NegX = new(-1, 0, 0);
    public readonly static Int3 PosX = new(1, 0, 0);
    public readonly static Int3 NegY = new(0, -1, 0);
    public readonly static Int3 PosY = new(0, 1, 0);
    public readonly static Int3 NegZ = new(0, 0, -1);
    public readonly static Int3 PosZ = new(0, 0, 1);

    /// <summary>
    ///     The six unit face offsets, ordered +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    /// <remarks>
    ///     This is one shared array. Don't write to it.
    /// </remarks>
    public readonly static Int3[] CardinalOffsets =
    [
        PosX, NegX, PosY, NegY, PosZ, NegZ
    ];

    public override bool Equals(object? obj)
    {
        return obj is Int3 other && Equals(other);
    }

    public bool Equals(Int3 other)
    {
        return X == other.X && Y == other.Y && Z == other.Z;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z);
    }

    public static bool operator ==(Int3 left, Int3 right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Int3 left, Int3 right)
    {
        return !left.Equals(right);
    }

    public static Int3 operator +(Int3 left, Int3 right)
    {
        return new Int3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    }

    public static Int3 operator -(Int3 left, Int3 right)
    {
        return new Int3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    }

    public static Int3 operator -(Int3 value)
    {
        return new Int3(-value.X, -value.Y, -value.Z);
    }

    public static Int3 operator *(Int3 value, int scalar)
    {
        return new Int3(value.X * scalar, value.Y * scalar, value.Z * scalar);
    }

    public static Int3 operator *(int scalar, Int3 value)
    {
        return value * scalar;
    }

    /// <summary>
    ///     Divides each component by <paramref name="scalar" /> using C# integer division, which truncates toward zero.
    /// </summary>
    /// <remarks>
    ///     Truncation is not floor division: <c>new Int3(-1, 0, 0) / 16</c> is <see cref="Zero" />, not
    ///     <c>(-1, 0, 0)</c>.
    /// </remarks>
    /// <exception cref="DivideByZeroException"><paramref name="scalar" /> is zero.</exception>
    public static Int3 operator /(Int3 value, int scalar)
    {
        return new Int3(value.X / scalar, value.Y / scalar, value.Z / scalar);
    }

    /// <summary>
    ///     Computes the component-wise C# remainder, whose sign follows <paramref name="left" />.
    /// </summary>
    /// <exception cref="DivideByZeroException">Any component of <paramref name="right" /> is zero.</exception>
    public static Int3 operator %(Int3 left, Int3 right)
    {
        return new Int3(left.X % right.X, left.Y % right.Y, left.Z % right.Z);
    }

    /// <summary>
    ///     Determines whether every coordinate is inside a pair of inclusive-minimum, exclusive-maximum bounds.
    /// </summary>
    /// <param name="minInclusive">The inclusive lower bound for every coordinate.</param>
    /// <param name="maxExclusive">The exclusive upper bound for every coordinate.</param>
    /// <returns><see langword="true" /> when every coordinate is within its corresponding bounds.</returns>
    public readonly bool IsWithin(Int3 minInclusive, Int3 maxExclusive)
    {
        return X >= minInclusive.X &&
               X < maxExclusive.X &&
               Y >= minInclusive.Y &&
               Y < maxExclusive.Y &&
               Z >= minInclusive.Z &&
               Z < maxExclusive.Z;
    }

    /// <summary>
    ///     Determines whether this coordinate is inside the zero-based, inclusive-minimum,
    ///     exclusive-maximum bounds.
    /// </summary>
    /// <param name="dimensions">The exclusive upper bound for every coordinate; the lower bound is zero.</param>
    /// <returns><see langword="true" /> when <c>0 &lt;= X &lt; dimensions.X</c> and likewise for Y and Z.</returns>
    public readonly bool IsWithin(Int3 dimensions)
    {
        return (uint)X < (uint)dimensions.X &&
               (uint)Y < (uint)dimensions.Y &&
               (uint)Z < (uint)dimensions.Z;
    }

    public override string ToString()
    {
        return $"{X}, {Y}, {Z}";
    }
}