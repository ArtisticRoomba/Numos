namespace Numos.Maths;

public struct Int3(int x, int y, int z) : IEquatable<Int3>
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