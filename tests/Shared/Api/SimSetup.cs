using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.Tests;

/// <summary>
///     Builds simulation layouts the API-level tests share.
/// </summary>
internal static class SimSetup
{
    internal const int RoomId = 1;

    internal static AtmosChunkHandle CreateOpenChunk(
        AtmosSimulation simulation, Int3 position,
        VoxelClassification? classification = null)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, classification ?? new VoxelClassification(RoomId));
        return chunk;
    }

    /// <summary>
    ///     Creates a solid chunk whose only open voxel is (<paramref name="x"/>, <paramref name="y"/>,
    ///     <paramref name="z"/>).
    /// </summary>
    internal static AtmosChunkHandle CreateIsolatedVoxel(
        AtmosSimulation simulation, Int3 position,
        int x, int y, int z, VoxelClassification classification,
        float temperature = SimConfigs.DefaultTemperature)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, x, y, z, classification);
        simulation.SetVoxelTemperature(chunk, x, y, z, temperature);
        return chunk;
    }

    internal static void SetAllTemperatures(
        AtmosSimulation simulation, AtmosChunkHandle chunk,
        int width, int height, int depth, float temperature = SimConfigs.DefaultTemperature)
    {
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            simulation.SetVoxelTemperature(chunk, x, y, z, temperature);
    }

    internal static void Tick(this AtmosSimulation simulation, int count)
    {
        for (int tick = 0; tick < count; tick++)
            simulation.Tick();
    }

    internal static void Tick(this AtmosWorld world, int count)
    {
        for (int tick = 0; tick < count; tick++)
            world.Tick();
    }
}