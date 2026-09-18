using Numos.Collections;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    internal T GetOrCreateSolverData<T>(object key, Func<T> factory) where T : notnull
    {
        lock (StateGate)
        {
            return _solverData.GetOrCreate(key, factory);
        }
    }

    internal T GetOrCreateGasSolverData<T>(int gasId, object key, Func<GasProperties, T> factory) where T : notnull
    {
        lock (StateGate)
        {
            if (!_isTickExecuting)
                CurrentTickConfig.Capture(_config);

            return CurrentTickConfig.GetOrCreateGasSolverData(gasId, key, factory);
        }
    }

    internal T[] GetOrCreateChunkSolverArray<T>(Int3 position, object key, bool captureForRollback, int? length)
    {
        lock (StateGate)
        {
            return GetChunk(position).GetOrCreateSolverArray<T>(key, captureForRollback, length);
        }
    }

    internal FlatArray<T> GetOrCreateChunkSolverFlatArray<T>(Int3 position, object key, bool captureForRollback)
    {
        lock (StateGate)
        {
            return GetChunk(position).GetOrCreateSolverFlatArray<T>(key, captureForRollback);
        }
    }
}