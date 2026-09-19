using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Tick-scoped inputs shared by every solver stage.
/// </summary>
internal sealed class AtmosSolverExecutionContext
{
    private readonly AtmosKernel _world;

    internal AtmosSolverExecutionContext(
        AtmosKernel world, AtmosChunk[] chunks,
        AtmosSolverConfigSnapshot config, int tickCount, SolverDataStorage sharedData)
    {
        _world = world;
        Chunks = chunks;
        TickConfig = config;
        TickCount = tickCount;
        SharedData = sharedData;
    }

    internal AtmosChunk[] Chunks { get; }
    /// <summary>
    ///     Normalized built-in solver settings captured before this tick began.
    /// </summary>
    internal AtmosSolverConfigSnapshot TickConfig { get; }

    internal int TickCount { get; }
    internal SolverDataStorage SharedData { get; }

    /// <summary>
    ///     Looks up a chunk by its grid position.
    /// </summary>
    /// <remarks>
    ///     The only kernel operation cross-chunk solvers need — deliberately narrow rather than exposing
    ///     <see cref="AtmosKernel" /> itself, since parallel solver stages must not touch its other members.
    /// </remarks>
    internal bool TryGetChunk(Int3 position, out AtmosChunk chunk)
    {
        return _world.TryGetChunk(position, out chunk);
    }
}

/// <summary>
///     Holds one kernel's immutable inputs and callback snapshot during a coordinated world tick.
/// </summary>
internal readonly record struct AtmosWorldTickExecution(
    AtmosSolverExecutionContext Context);