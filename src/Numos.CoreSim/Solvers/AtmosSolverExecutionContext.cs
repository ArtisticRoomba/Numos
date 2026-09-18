using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Tick-scoped inputs shared by every solver stage.
/// </summary>
internal sealed class AtmosSolverExecutionContext
{
    internal AtmosSolverExecutionContext(
        AtmosKernel world, AtmosChunk[] chunks,
        AtmosSolverConfigSnapshot config, int tickCount, SolverDataStorage sharedData)
    {
        World = world;
        Chunks = chunks;
        TickConfig = config;
        TickCount = tickCount;
        SharedData = sharedData;
    }

    internal AtmosKernel World { get; }
    internal AtmosChunk[] Chunks { get; }
    /// <summary>
    ///     Normalized built-in solver settings captured before this tick began.
    /// </summary>
    internal AtmosSolverConfigSnapshot TickConfig { get; }

    internal int TickCount { get; }
    internal SolverDataStorage SharedData { get; }
}

/// <summary>
///     Holds one kernel's immutable inputs and callback snapshot during a coordinated world tick.
/// </summary>
internal readonly record struct AtmosWorldTickExecution(
    AtmosSolverExecutionContext Context);