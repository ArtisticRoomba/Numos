using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Gets host solver data for a slot, creating it on first use.
    /// </summary>
    /// <param name="slotId">The slot id from <see cref="SolverDataSlotIds" />.</param>
    /// <param name="name">The slot's diagnostic name.</param>
    /// <param name="configScoped">
    ///     <see langword="true" /> to store the value with the tick configuration, so it is rebuilt after the
    ///     configuration changes.
    /// </param>
    /// <param name="state">Passed to <paramref name="factory" />.</param>
    /// <param name="factory">Builds the value on a miss.</param>
    /// <returns>The cached value.</returns>
    /// <remarks>
    ///     Outside a tick, config-scoped data first catches the tick configuration up with the applied one, so the
    ///     value matches what the next tick would see. During a tick the applied configuration can't change (the
    ///     world defers it to the next tick boundary), so both agree there too.
    /// </remarks>
    internal T GetSolverData<T, TState>(
        int slotId, string name, bool configScoped, TState state, Func<TState, T> factory) where T : class
    {
        lock (StateGate)
        {
            if (!configScoped)
                return _solverData.GetOrCreate(slotId, name, state, factory);

            if (!_isTickExecuting)
                CurrentTickConfig.Capture(_config);

            return CurrentTickConfig.ConfigScopedData.GetOrCreate(slotId, name, state, factory);
        }
    }

    internal T[] GetOrCreateChunkField<T>(Int3 position, ChunkFieldDescriptor<T> field)
    {
        lock (StateGate)
        {
            return GetChunk(position).GetOrCreateField(field);
        }
    }
}