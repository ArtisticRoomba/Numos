using System.Collections.Concurrent;
using Numos.CoreSim.Datatypes.Events;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Tick-scoped inputs shared by every solver stage.
/// </summary>
internal sealed class AtmosSolverExecutionContext
{
    internal AtmosSolverExecutionContext(IAtmosSolverWorld world, AtmosChunk[] chunks,
        AtmosSolverConfigSnapshot config, AtmosConfig configuration, int tickCount)
    {
        World = world;
        Chunks = chunks;
        TickConfig = config;
        Configuration = configuration;
        TickCount = tickCount;
    }

    internal IAtmosSolverWorld World { get; }
    internal AtmosChunk[] Chunks { get; }
    /// <summary>Normalized built-in solver settings captured before this tick began.</summary>
    internal AtmosSolverConfigSnapshot TickConfig { get; }

    /// <summary>The live public configuration reference captured before this tick began.</summary>
    internal AtmosConfig Configuration { get; }
    internal int TickCount { get; }
    internal ConcurrentQueue<(Int3 Key, BoundaryFlowEvent Event)> BoundaryEvents { get; } = new();
    internal ConcurrentQueue<(Int3 Key, ThermalBoundaryEvent Event)> ThermalBoundaryEvents { get; } = new();
}

/// <summary>
///     Minimal world operations needed by cross-chunk solvers.
/// </summary>
internal interface IAtmosSolverWorld
{
    bool TryGetChunk(Int3 position, out AtmosChunk chunk);
    void AddBoundaryProcessingTicks(long elapsedTicks);
}
