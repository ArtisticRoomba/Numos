using Numos.Collections;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    internal T GetOrCreateGasSolverData<T>(int gasId, object key, Func<GasProperties, T> factory) where T : notnull
    {
        lock (_stateGate)
        {
            if (!_isTickExecuting)
                _tickConfig.Capture(_config);

            return _tickConfig.GetOrCreateGasSolverData(gasId, key, factory);
        }
    }

    internal T[] GetOrCreateChunkSolverArray<T>(Int3 position, object key, bool captureForRollback, int? length)
    {
        lock (_stateGate)
        {
            return GetChunk(position).GetOrCreateSolverArray<T>(key, captureForRollback, length);
        }
    }

    internal FlatArray<T> GetOrCreateChunkSolverFlatArray<T>(Int3 position, object key, bool captureForRollback)
    {
        lock (_stateGate)
        {
            return GetChunk(position).GetOrCreateSolverFlatArray<T>(key, captureForRollback);
        }
    }
}