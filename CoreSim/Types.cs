namespace Numos;

public struct Int3 : IEquatable<Int3>
{
    public int X;
    public int Y;
    public int Z;

    public Int3(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public override bool Equals(object obj)
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

public struct Vector3
{
    public float X;
    public float Y;
    public float Z;

    public Vector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public struct AtmosChunkSnapshot
{
    public Int3 GridPosition;
    public float[] TotalPressure;
    public float[] Temperature;
    public GasSnapshot[] Gases;
    public int[] VoxelRoomMap;
    public bool IsSnapshotValid => TotalPressure != null && Temperature != null;
}

public struct GasSnapshot
{
    public int GasId;
    public float[] Moles;
}

public struct PrecipitationEvent
{
    public ushort LocalVoxelIndex;
    public int LiquidID;
    public float MolesToSpawn;
    public float InheritedTemp;
}

public struct BoundaryFlowEvent
{
    public ushort LocalVoxelIndex;
    public float Pressure;
    public float Temperature;
}

public struct ThermalBoundaryEvent
{
    public ushort LocalVoxelIndex;
    public float Temperature;
}

public struct GasInjectionEvent
{
    public Vector3 Position;
    public int GasId;
    public float Moles;
    public float Temperature;
}