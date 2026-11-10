using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Solvers;

namespace Numos.API.Dangerous;

/// <summary>
///     One work item of a <see cref="AtmosParallel.Run{TAction}" /> phase.
/// </summary>
/// <remarks>
///     Implement this as a <see langword="struct" /> so the phase invokes it without boxing.
/// </remarks>
public interface IAtmosParallelAction
{
    /// <summary>
    ///     Runs one work item.
    /// </summary>
    /// <param name="index">The item index, in [0, count) of the phase.</param>
    /// <remarks>
    ///     Items run in no fixed order and on any participating thread. An item must write only outputs that belong to
    ///     its own index and must not read what another item of the same phase writes.
    /// </remarks>
    void Invoke(int index);
}

/// <summary>
///     Runs work items on the same parallel scheduler as the built-in solver stages.
/// </summary>
/// <remarks>
///     Use this in a custom stage instead of <see cref="Parallel.For(int, int, Action{int})" />. The scheduler pools its
///     state per action type, so a steady-state phase allocates nothing. The worker limit also follows the limit
///     that tests and benchmarks set for the built-in stages. A phase that runs in a replayed callback must give the
///     same result for any worker count and any item order.
/// </remarks>
public static class AtmosParallel
{
    /// <summary>
    ///     The most threads, the caller included, that a phase uses. It is at least one.
    /// </summary>
    /// <remarks>
    ///     Every built-in stage sizes its schedule from this value, <c>euler-flow</c> included. Size a tile schedule
    ///     from it too, so a test that limits the workers limits the schedule as well.
    /// </remarks>
    public static int WorkerCount => ParallelPhase.WorkerCount;

    /// <summary>
    ///     Runs <paramref name="count" /> work items and returns after every one has finished.
    /// </summary>
    /// <typeparam name="TAction">The struct that runs one item.</typeparam>
    /// <param name="count">The number of items. Zero or less runs nothing.</param>
    /// <param name="action">The action, copied once per participating thread.</param>
    /// <remarks>
    ///     <para>
    ///         The calling thread works on the phase too. Items are handed out in small groups from a shared counter,
    ///         so item costs may differ. When an item throws, the phase stops handing out items, waits for the running
    ///         ones, and rethrows the first exception.
    ///     </para>
    ///     <para>
    ///         Do not call this from inside another phase's item to wait on that phase's results. Do not touch
    ///         simulation state that an item of the same phase also writes.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     readonly struct Decay(AtmosSolverChunks chunks) : IAtmosParallelAction
    ///     {
    ///         public void Invoke(int index) => chunks[index].Get(FireData.Exposure).AsSpan().Clear();
    ///     }
    ///
    ///     AtmosParallel.Run(chunks.Count, new Decay(chunks));
    ///     </code>
    /// </example>
    public static void Run<TAction>(int count, TAction action)
        where TAction : struct, IAtmosParallelAction
    {
        ParallelPhase.Run(count, new Adapter<TAction>(action));
    }

    private struct Adapter<TAction>(TAction action) : IAction
        where TAction : struct, IAtmosParallelAction
    {
        private TAction _action = action;

        public void Invoke(int i)
        {
            _action.Invoke(i);
        }
    }
}
