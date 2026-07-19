namespace Numos.Maths;

/// <summary>
///     3D integer datatype.
/// </summary>
public struct Int3(int x, int y, int z) : IEquatable<Int3> // TODO expand math ops
{
    public int X = x;
    public int Y = y;
    public int Z = z;

    public override bool Equals(object? obj)
    {
        return obj is Int3 other && Equals(other);
    }

    public bool Equals(Int3 other)
    {
        return X == other.X && Y == other.Y && Z == other.Z;
    }

    // TODO PERF replace with native xxh3
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
}