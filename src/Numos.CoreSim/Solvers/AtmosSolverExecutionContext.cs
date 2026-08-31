using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Tick-scoped inputs shared by every solver stage.
/// </summary>
internal sealed class AtmosSolverExecutionContext
{
    internal AtmosSolverExecutionContext(
        IAtmosSolverWorld world, AtmosChunk[] chunks,
        AtmosSolverConfigSnapshot config, int tickCount)
    {
        World = world;
        Chunks = chunks;
        TickConfig = config;
        TickCount = tickCount;
    }

    internal IAtmosSolverWorld World { get; }
    internal AtmosChunk[] Chunks { get; }
    /// <summary>Normalized built-in solver settings captured before this tick began.</summary>
    internal AtmosSolverConfigSnapshot TickConfig { get; }

    internal int TickCount { get; }
}

/// <summary>
///     Minimal world operations needed by cross-chunk solvers.
/// </summary>
internal interface IAtmosSolverWorld
{
    // TODO slate for removal, this should be an internal API call.
    bool TryGetChunk(Int3 position, out AtmosChunk chunk);

    // TODO slate for removal, this was a hardcoded profiling counter that got atomized into an interface,
    // this doesnt really belong here
    void AddBoundaryProcessingTicks(long elapsedTicks);
}