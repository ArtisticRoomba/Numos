using System.Collections.Concurrent;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Connects parallel producers to sequential boundary stages without coupling the solver instances.
/// </summary>
internal static class BoundaryEvents<T>
{
    private readonly static object Key = new();

    internal static ConcurrentQueue<(int TickCount, Int3 Key, T Event)> Get(AtmosSolverExecutionContext context)
    {
        return context.SharedData.GetOrCreate(Key, static () => new ConcurrentQueue<(int TickCount, Int3 Key, T Event)>());
    }
}