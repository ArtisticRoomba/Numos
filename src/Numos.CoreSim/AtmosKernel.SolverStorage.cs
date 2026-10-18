using Numos.CoreSim.Solvers;
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

    internal T[] GetOrCreateChunkField<T>(Int3 position, ChunkFieldDescriptor<T> field)
    {
        lock (StateGate)
        {
            return GetChunk(position).GetOrCreateField(field);
        }
    }
}