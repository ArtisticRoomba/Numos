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
    ///     state is pooled per <typeparamref name="TAction" />. On a starved thread pool, helpers start late or never,
    ///     and the caller finishes the phase alone. If items throw, the phase stops handing out new items, waits for
    ///     the running ones, and rethrows the first exception.
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

        Phase<TAction> phase = Phase<TAction>.Rent();
        phase.Start(action, workItemCount, grain, helpers);
        phase.SpawnHelpers();
        phase.Work();

        // Only items already claimed by running workers remain, so the wait is short.
        var spinner = new SpinWait();
        while (Volatile.Read(ref phase.Done) < workItemCount)
            spinner.SpinOnce(-1);

        Exception? error = phase.Finish();
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
    ///     <para>
    ///         A queued helper can start long after its phase ended, so the object cannot wait for every helper
    ///         before it returns to the pool. The caller owns the object only between <see cref="Rent" /> and
    ///         <see cref="Finish" />. A late helper finds no running phase and exits, or it joins the phase the
    ///         object runs by then. Joining is safe because every phase on one object shares <typeparamref name="TAction" />
    ///         and its items are independent.
    ///     </para>
    ///     <para>
    ///         The caller queues only a few helpers. Each helper that starts while items remain queues more, so the
    ///         wake-up cost spreads over the workers like the replicating tasks of <see cref="Parallel.For(int,int,Action{int})" />.
    ///         Queued helpers that have not started count against the helper budget. A starved pool therefore
    ///         receives no new helpers.
    ///     </para>
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
        private int _maxHelpers;
        private Exception? _error;

        private int _done;

        /// <summary>
        ///     1 while a phase runs on this object, otherwise 0. <see cref="Start" /> sets it after all other fields.
        /// </summary>
        private int _live;

        /// <summary>
        ///     The helpers queued on the thread pool that have not started. The count persists across phases.
        /// </summary>
        private int _queued;

        /// <summary>
        ///     The helpers that have started and not yet left <see cref="IThreadPoolWorkItem.Execute" />.
        /// </summary>
        private int _active;

        /// <summary>
        ///     The number of items that finished, failed, or were abandoned after a failure.
        /// </summary>
        internal ref int Done => ref _done;

        /// <summary>
        ///     Takes a pooled phase object, or creates one when the pool is empty.
        /// </summary>
        /// <returns>A phase object that the caller owns until <see cref="Finish" />.</returns>
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

            // Published last, so a helper that sees 1 also sees every field above.
            Volatile.Write(ref _live, 1);
        }

        /// <summary>
        ///     Queues up to <see cref="SpawnFanOut" /> more helpers while the helper budget lasts.
        /// </summary>
        /// <remarks>
        ///     The budget check reads racy counters. A wrong answer changes the number of participants and never the result.
        /// </remarks>
        internal void SpawnHelpers()
        {
            for (int i = 0; i < SpawnFanOut; i++)
            {
                int queued = Interlocked.Increment(ref _queued);
                if (queued + Volatile.Read(ref _active) > _maxHelpers)
                {
                    Interlocked.Decrement(ref _queued);
                    return;
                }

                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _active);
            if (Volatile.Read(ref _live) == 1)
            {
                if (Volatile.Read(ref _next) < _count)
                    SpawnHelpers();

                Work();
            }

            Interlocked.Decrement(ref _active);
        }

        /// <summary>
        ///     Ends the phase, returns the object to the pool, and returns the first item exception.
        /// </summary>
        /// <returns>The first exception an item threw, or <see langword="null" />.</returns>
        /// <remarks>
        ///     Call it once <see cref="Done" /> reaches the item count. A helper inside the phase has no item left to
        ///     claim, so it leaves after one failed claim. A preempted helper can still delay the caller by a scheduler slice.
        ///     The pool drops the object when it is full. Late helpers that still hold it see no running phase and exit.
        /// </remarks>
        internal Exception? Finish()
        {
            // The full fence pairs with the helper's increment of _active followed by its read of _live.
            // Either the helper sees 0 and leaves, or this method sees the helper in _active and waits for it.
            Interlocked.Exchange(ref _live, 0);

            var spinner = new SpinWait();
            while (Volatile.Read(ref _active) != 0)
                spinner.SpinOnce(-1);

            Exception? error = Volatile.Read(ref _error);
            _action = default;
            _error = null;

            int start = Environment.CurrentManagedThreadId;
            for (int i = 0; i < Pool.Length; i++)
            {
                if (Interlocked.CompareExchange(ref Pool[(start + i) % Pool.Length], this, null) == null)
                    break;
            }

            return error;
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
