using System.Text.Json.Serialization;
using Numos.Maths;

namespace Numos.Headless.Protocol;

/// <summary>A JSON-friendly three-dimensional integer coordinate.</summary>
public readonly record struct Coordinate
{
    public Coordinate(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    [JsonRequired]
    public int X { get; init; }

    [JsonRequired]
    public int Y { get; init; }

    [JsonRequired]
    public int Z { get; init; }

    internal readonly Int3 ToInt3() => new(X, Y, Z);

    internal static Coordinate From(Int3 value) => new(value.X, value.Y, value.Z);
}

