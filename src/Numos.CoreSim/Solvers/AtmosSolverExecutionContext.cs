namespace Numos.CoreSim.Solvers;

/// <summary>
///     Stable inputs shared by every solver stage in one tick.
/// </summary>
internal sealed class AtmosSolverExecutionContext
{
    internal AtmosSolverExecutionContext(AtmosKernel kernel, AtmosChunk[] chunks)
    {
        Kernel = kernel;
        Chunks = chunks;
    }

    internal AtmosKernel Kernel { get; }
    internal AtmosChunk[] Chunks { get; }
    internal int TickCount => Kernel.TickCount;
}