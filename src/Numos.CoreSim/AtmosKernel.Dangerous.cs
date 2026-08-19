using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    internal AtmosConfig DangerousConfiguration => _config;

    /// <summary>
    ///     Injects gas from a solver stage using the normalized heat-capacity and pressure values captured for
    ///     the current tick.
    /// </summary>
    internal void DangerousInjectGasDuringTick(AtmosChunk chunk, ushort localVoxelIndex, int gasId,
        float moles, float temperature)
    {
        int roomId = chunk.VoxelRoomMap[localVoxelIndex];
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return;

        chunk.WakeRoom(roomId);
        GasInjectionSolver.InjectDuringTick(chunk, localVoxelIndex, gasId, moles, temperature, _tickConfig);
    }
}