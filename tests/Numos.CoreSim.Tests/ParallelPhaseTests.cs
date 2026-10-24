using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Tests;

[NonParallelizable]
public class ParallelPhaseTests
{
    [TearDown]
    public void RestoreDefaults()
    {
        ParallelPhase.WorkerCountOverride = null;
        ParallelPhase.GrainsPerWorker = 4;
    }

    /// <summary>
    ///     Every item runs exactly once whatever the item count, worker limit, and grain size.
    /// </summary>
    [Test]
    public void Run_VisitsEveryItemOnce(
        [Values(1, 2, 7, 64, 1000, 4099)] int count,
        [Values(1, 2, 5, 64)] int workers,
        [Values(1, 4, 100)] int grainsPerWorker)
    {
        ParallelPhase.WorkerCountOverride = workers;
        ParallelPhase.GrainsPerWorker = grainsPerWorker;
        int[] visits = new int[count];

        ParallelPhase.Run(count, new CountAction(visits));

        Assert.That(visits, Is.All.EqualTo(1));
    }

    /// <summary>
    ///     A throwing item stops the phase, and the first exception reaches the caller unwrapped.
    /// </summary>
    [Test]
    public void Run_PropagatesItemException()
    {
        ParallelPhase.WorkerCountOverride = 8;
        int[] visits = new int[10_000];

        var thrown = Assert.Throws<InvalidOperationException>(
            () => ParallelPhase.Run(visits.Length, new ThrowingAction(visits, 4_321)));

        Assert.That(thrown!.Message, Is.EqualTo("item 4321"));

        // The pooled state must be reusable after a failure.
        Array.Clear(visits);
        ParallelPhase.Run(visits.Length, new CountAction(visits));
        Assert.That(visits, Is.All.EqualTo(1));
    }

    /// <summary>
    ///     Steady-state phases reuse pooled state, so repeated calls allocate only the thread pool's amortized queue segments.
    /// </summary>
    [Test]
    public void Run_DoesNotAllocateInSteadyState()
    {
        ParallelPhase.WorkerCountOverride = 4;
        int[] visits = new int[256];
        for (int warmup = 0; warmup < 5_000; warmup++)
            ParallelPhase.Run(visits.Length, new CountAction(visits));

        // Measure the calling thread only. Helper threads allocate nothing a phase could cause,
        // and other fixtures' background threads would add noise to a process-wide count.
        // Take the best of three attempts so a stray tiered-JIT allocation cannot fail the test.
        const int phases = 2_000;
        long allocated = long.MaxValue;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int phase = 0; phase < phases; phase++)
                ParallelPhase.Run(visits.Length, new CountAction(visits));
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // The thread pool queue allocates a segment now and then, which averages a few bytes per phase.
        // A per-phase closure or work item costs at least 32 bytes per phase, so 2,000 phases would exceed 64 KB.
        Assert.That(allocated, Is.LessThan(48 * 1024));
    }

    private readonly struct CountAction(int[] visits) : IAction
    {
        public void Invoke(int index)
        {
            Interlocked.Increment(ref visits[index]);
        }
    }

    private readonly struct ThrowingAction(int[] visits, int failingIndex) : IAction
    {
        public void Invoke(int index)
        {
            if (index == failingIndex)
                throw new InvalidOperationException($"item {index}");

            Interlocked.Increment(ref visits[index]);
        }
    }
}
