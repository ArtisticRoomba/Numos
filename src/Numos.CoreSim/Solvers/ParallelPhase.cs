using System.Runtime.ExceptionServices;
using CommunityToolkit.HighPerformance.Helpers;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Barrier-separated parallel phases and the tile arithmetic the voxel solvers schedule them with.
/// </summary>
/// <remarks>
///     <para>
///         A phase hands out its work items dynamically. Every participant claims the next grain of items from one
///         shared counter until the counter passes the item count. A static split into one contiguous batch per core
///         makes the slowest batch set the phase length, and chunk costs differ a lot (an all-air chamber chunk
///         against a mostly-solid wall chunk). Dynamic claiming lets a worker that finished early take more items.
///     </para>
///     <para>
///         Which worker runs which item is not deterministic. An item must therefore write only outputs that belong
///         to it and must not read anything another item of the same phase writes. Per-thread scratch is fine
///         when an item fully initializes what it reads. Accumulators keyed by worker or batch are not.
///     </para>
/// </remarks>
internal static class ParallelPhase
{
    /// <summary>
    ///     Replaces <see cref="Environment.ProcessorCount" /> as the participant limit of a phase. Tests and
    ///     benchmarks set it. A value of 1 runs every phase sequentially on the calling thread.
    /// </summary>
    internal static int? WorkerCountOverride;

    /// <summary>
    ///     Target number of grains per participant. A larger value balances load better and claims more often.
    /// </summary>
    internal static int GrainsPerWorker = 4;

    /// <summary>
    ///     Runs one parallel phase, returning only after every work item has completed.
    /// </summary>
    /// <typeparam name="TAction">The allocation-free action used for each work item.</typeparam>
    /// <param name="workItemCount">The number of work items in the phase.</param>
    /// <param name="action">The operation to perform for each work item.</param>
    /// <remarks>
    ///     The calling thread works on the phase too. A steady-state call allocates nothing, because the shared phase
    ///     state is pooled per <typeparamref name="TAction" />. If items throw, the phase stops handing out new
    ///     items, waits for the running ones, and rethrows the first exception.
    /// </remarks>
    internal static void Run<TAction>(int workItemCount, TAction action)
        where TAction : struct, IAction
    {
        if (workItemCount <= 0)
            return;

        int workers = Math.Max(1, WorkerCountOverride ?? Environment.ProcessorCount);
        if (workers == 1 || workItemCount == 1)
        {
            for (int i = 0; i < workItemCount; i++)
                action.Invoke(i);
            return;
        }

        int grain = Math.Max(1, workItemCount / (workers * Math.Max(1, GrainsPerWorker)));
        int helpers = Math.Min(workers - 1, DivideRoundUp(workItemCount, grain) - 1);

        var phase = Phase<TAction>.Rent();
        phase.Start(action, workItemCount, grain, helpers);
        phase.SpawnHelpers();
        phase.Work();

        // Only items already claimed by running workers remain, so the wait is short.
        var spinner = new SpinWait();
        while (Volatile.Read(ref phase.Done) < workItemCount)
            spinner.SpinOnce(-1);

        Exception? error = phase.Error;
        phase.Release();
        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>
    ///     Divides an item count into complete and partial work items without overflowing the numerator.
    /// </summary>
    /// <param name="itemCount">The number of items to divide.</param>
    /// <param name="itemSize">The positive maximum size of each work item.</param>
    /// <returns>The number of work items required to cover <paramref name="itemCount" />.</returns>
    internal static int DivideRoundUp(int itemCount, int itemSize)
    {
        return itemCount == 0 ? 0 : (itemCount - 1) / itemSize + 1;
    }

    /// <summary>
    ///     The shared state of one phase. The same object is queued once per helper thread.
    /// </summary>
    /// <remarks>
    ///     A helper that starts after the items ran out still touches the object. The reference count keeps the
    ///     object out of the pool until the caller and every queued helper have released it.
    ///     The caller queues only a few helpers. Each helper that starts while items remain queues more, so the
    ///     wake-up cost spreads over the workers like the replicating tasks of <see cref="Parallel.For(int,int,Action{int})" />.
    /// </remarks>
    private sealed class Phase<TAction> : IThreadPoolWorkItem
        where TAction : struct, IAction
    {
        private const int SpawnFanOut = 2;

        private static readonly Phase<TAction>?[] Pool = new Phase<TAction>?[16];

        private TAction _action;
        private int _count;
        private int _grain;
        private int _next;
        private int _references;
        private int _maxHelpers;
        private int _spawned;
        private Exception? _error;

        private int _done;

        /// <summary>
        ///     The number of items that finished, failed, or were abandoned after a failure.
        /// </summary>
        internal ref int Done => ref _done;

        /// <summary>
        ///     The first exception an item threw. Valid once <see cref="Done" /> reaches the item count.
        /// </summary>
        internal Exception? Error => Volatile.Read(ref _error);

        internal static Phase<TAction> Rent()
        {
            int start = Environment.CurrentManagedThreadId;
            for (int i = 0; i < Pool.Length; i++)
            {
                Phase<TAction>? phase = Interlocked.Exchange(ref Pool[(start + i) % Pool.Length], null);
                if (phase != null)
                    return phase;
            }

            return new Phase<TAction>();
        }

        internal void Start(in TAction action, int count, int grain, int helpers)
        {
            _action = action;
            _count = count;
            _grain = grain;
            _next = 0;
            _done = 0;
            _error = null;
            _maxHelpers = helpers;
            _spawned = 0;
            _references = 1;
        }

        /// <summary>
        ///     Queues up to <see cref="SpawnFanOut" /> more helpers while the helper budget lasts.
        /// </summary>
        /// <remarks>
        ///     The caller holds a reference, so the count cannot reach zero while this method adds one.
        /// </remarks>
        internal void SpawnHelpers()
        {
            for (int i = 0; i < SpawnFanOut; i++)
            {
                if (Interlocked.Increment(ref _spawned) > _maxHelpers)
                    return;

                Interlocked.Increment(ref _references);
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            if (Volatile.Read(ref _next) < _count)
                SpawnHelpers();

            Work();
            Release();
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;

            _action = default;
            _error = null;
            int start = Environment.CurrentManagedThreadId;
            for (int i = 0; i < Pool.Length; i++)
            {
                if (Interlocked.CompareExchange(ref Pool[(start + i) % Pool.Length], this, null) == null)
                    return;
            }
        }

        internal void Work()
        {
            TAction action = _action;
            int count = _count;
            int grain = _grain;
            while (true)
            {
                int start = Interlocked.Add(ref _next, grain) - grain;
                if (start >= count)
                    return;

                int end = Math.Min(start + grain, count);
                int index = start;
                try
                {
                    for (; index < end; index++)
                        action.Invoke(index);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _error, exception, null);

                    // Stop new claims. Only the first failure sees a frontier below count, so it alone
                    // accounts for the items nobody will claim.
                    int frontier = Interlocked.Exchange(ref _next, count);
                    int abandoned = (end - start) + Math.Max(0, count - frontier);
                    Interlocked.Add(ref _done, abandoned);
                    return;
                }

                Interlocked.Add(ref _done, end - start);
            }
        }
    }
}
