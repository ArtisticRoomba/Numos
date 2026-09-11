namespace Numos.CoreSim.Benchmarks.Infrastructure;

public enum BoundaryTopology
{
    Isolated,
    Line,
    Grid2D,
    Grid3D
}

public enum ReactionWorkload
{
    None,
    Inactive,
    ActiveSparse,
    ActiveDense
}

public enum GradientStrength
{
    None,
    Small,
    Large
}

internal sealed record ScalingWorkloadOptions
{
    internal int ChunkWidth { get; init; } = 8;
    internal int ChunkHeight { get; init; } = 8;
    internal int ChunkDepth { get; init; } = 8;
    internal int RegisteredChunkCount { get; init; } = 4;
    internal int AwakeChunkCount { get; init; } = 4;
    internal double ActiveVoxelFraction { get; init; } = 1d;
    internal int GasCount { get; init; } = 8;
    internal int ReactionCount { get; init; }
    internal ReactionWorkload ReactionWorkload { get; init; }
    internal int CondensingGasCount { get; init; }
    internal double SupersaturatedVoxelFraction { get; init; }
    internal BoundaryTopology BoundaryTopology { get; init; } = BoundaryTopology.Line;
    internal GradientStrength PressureGradient { get; init; } = GradientStrength.Large;
    internal GradientStrength TemperatureGradient { get; init; } = GradientStrength.Large;
    internal int RandomSeed { get; init; } = 1729;

    internal int VoxelCount => checked(ChunkWidth * ChunkHeight * ChunkDepth);
    internal int ActiveVoxelCount => (int)Math.Round(VoxelCount * ActiveVoxelFraction, MidpointRounding.AwayFromZero);
}