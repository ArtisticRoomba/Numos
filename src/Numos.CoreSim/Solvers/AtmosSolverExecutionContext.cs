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
        AtmosSolverConfigSnapshot config, long tickCount, int tickRate = 20)
    {
        _world = world;
        Chunks = chunks;
        TickConfig = config;
        TickCount = tickCount;
        TickRate = tickRate;
        DeltaTime = 1f / tickRate;
        Interval = 1;
        SleepDelayTicks = BuiltInStageSchedule.SecondsToTicks(config.SleepDelay, tickRate);
    }

    /// <summary>
    ///     The sleep delay in ticks: <c>round(SleepDelay * TickRate)</c>, saturating at <see cref="int.MaxValue" />.
    /// </summary>
    /// <remarks>
    ///     Computed when the tick starts, so a changed tick rate or sleep delay applies on the next tick.
    /// </remarks>
    internal int SleepDelayTicks { get; }

    /// <summary>
    ///     Ticks the <c>euler-flow</c> stage has covered since its last settle pass. The stage reads and writes it on
    ///     the tick thread, and the kernel checkpoints it.
    /// </summary>
    internal int EulerSettleTicks
    {
        get => _world.EulerSettleTicks;
        set => _world.EulerSettleTicks = value;
    }

    /// <summary>
    ///     Position in the <c>euler-flow</c> stage's six-order sweep cycle, in [0, 6). The stage reads and writes it
    ///     on the tick thread, and the kernel checkpoints it.
    /// </summary>
    internal int EulerSweepPhase
    {
        get => _world.EulerSweepPhase;
        set => _world.EulerSweepPhase = value;
    }

    internal AtmosChunk[] Chunks { get; }
    /// <summary>
    ///     Normalized built-in solver settings captured before this tick began.
    /// </summary>
    internal AtmosSolverConfigSnapshot TickConfig { get; }

    internal long TickCount { get; }

    /// <summary>
    ///     The gas time the running stage advances, in seconds.
    /// </summary>
    internal Second DeltaTime { get; private set; }

    /// <summary>
    ///     How many ticks the running stage spans. One means the stage runs every tick.
    /// </summary>
    internal int Interval { get; private set; }

    /// <summary>
    ///     Ticks per second of atmospheric time.
    /// </summary>
    internal int TickRate { get; }

    /// <summary>
    ///     Sets the interval and gas time the next stage sees.
    /// </summary>
    internal void BeginStage(int interval, Second deltaTime)
    {
        Interval = interval;
        DeltaTime = deltaTime;
    }

    /// <summary>
    ///     Advances the revision of every awake chunk. The tick runs this before each stage that does not mark its
    ///     own writes.
    /// </summary>
    /// <remarks>
    ///     Covering only awake chunks is enough. A sleeping chunk that a stage writes must be woken, and
    ///     <see cref="AtmosChunk.Wake" /> advances the revision.
    /// </remarks>
    internal void MarkAwakeChunksChanged()
    {
        foreach (AtmosChunk chunk in Chunks)
        {
            if (chunk.IsAwake)
                chunk.MarkChanged();
        }
    }

    /// <summary>
    ///     Gets a built-in stage's shared data from <see cref="AtmosSolverConfigSnapshot.SolverData" />, creating it
    ///     on first use.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The cached value.</returns>
    /// <remarks>Call this on the tick thread before dispatching workers. The storage is not thread-safe.</remarks>
    internal T GetSolverData<T>(SolverDataSlot<T> slot) where T : class
    {
        return TickConfig.SolverData.GetOrCreate(
            slot.Descriptor,
            (Context: this, Slot: slot),
            static state => state.Slot.Factory(state.Context));
    }

    /// <summary>
    ///     Looks up a chunk by its grid position.
    /// </summary>
    /// <remarks>
    ///     This is the only kernel operation cross-chunk solvers need.
    ///     It is narrow on purpose: parallel solver stages must not touch the other members of
    ///     <see cref="AtmosKernel" />, so the context does not expose the kernel itself.
    /// </remarks>
    internal bool TryGetChunk(Int3 position, out AtmosChunk chunk)
    {
        return _world.TryGetChunk(position, out chunk);
    }
}