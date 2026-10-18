using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Gets host solver data for a slot, creating it on first use.
    /// </summary>
    /// <param name="descriptor">The slot's descriptor.</param>
    /// <param name="state">Passed to <paramref name="factory" />.</param>
    /// <param name="factory">Builds the value on a miss.</param>
    /// <returns>The cached value.</returns>
    /// <remarks>
    ///     Outside a tick this first catches the tick configuration up with the applied one, dropping the data if the
    ///     configuration changed, so the value matches what the next tick would see. During a tick the applied
    ///     configuration can't change (the world defers it to the next tick boundary), so both agree there too.
    /// </remarks>
    internal T GetSolverData<T, TState>(SolverDataDescriptor descriptor, TState state, Func<TState, T> factory)
        where T : class
    {
        lock (StateGate)
        {
            if (!_isTickExecuting)
                CurrentTickConfig.Capture(_config);

            return CurrentTickConfig.SolverData.GetOrCreate(descriptor, state, factory);
        }
    }

    /// <summary>
    ///     Drops all solver data without disposing it.
    /// </summary>
    private void ClearSolverData()
    {
        CurrentTickConfig.SolverData.Clear();
    }

    internal T[] GetOrCreateChunkField<T>(Int3 position, ChunkFieldDescriptor<T> field)
    {
        lock (StateGate)
        {
            return GetChunk(position).GetOrCreateField(field);
        }
    }
}