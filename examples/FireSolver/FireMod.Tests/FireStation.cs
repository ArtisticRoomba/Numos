using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace FireMod.Tests;

/// <summary>
/// Builds a two-chunk room of air with a hot patch in the first chunk, and scripts what the host does to it.
/// </summary>
internal static class FireStation
{
    /// <summary>
    /// The tick rate of the world. At 60 Hz the fire stage runs every third tick, so 300 ticks give it 100 runs.
    /// </summary>
    public const int TickRate = 60;

    public const int Ticks = 300;

    public const int IgniteTick = 60;

    public const int SettingTick = 150;

    public const int ExposureTick = 200;

    public const float LoweredIgnitionTemperature = 450f;

    public static readonly AtmosChunkHandle HotChunk = new(new Int3(0, 0, 0));

    public static readonly AtmosChunkHandle ColdChunk = new(new Int3(1, 0, 0));

    /// <summary>
    /// The local index of voxel (3, 3, 0). In <see cref="HotChunk"/> it sits in the hot patch.
    /// </summary>
    public const ushort CenterVoxel = 3 + 3 * 8;

    public static AtmosWorld Create()
    {
        var config = new AtmosConfig();
        config.GasRegistry.Add(new GasProperties
        {
            Name = "Oxygen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            MolarMass = 0.032f,
            DiffusionCoefficient = 0.02f
        });
        config.GasRegistry.Add(new GasProperties
        {
            Name = "Nitrogen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            MolarMass = 0.028f,
            DiffusionCoefficient = 0.02f
        });

        int oxygen = config.GasRegistry.GasIdToIndex("Oxygen");
        int nitrogen = config.GasRegistry.GasIdToIndex("Nitrogen");
        var world = new AtmosWorld(config, AtmosTransportModel.Euler, TickRate);
        AtmosSimulation station = world.CreateSimulation(8, 8, 1);
        foreach (AtmosChunkHandle chunk in (ReadOnlySpan<AtmosChunkHandle>)[HotChunk, ColdChunk])
        {
            station.CreateAndRegisterChunk(chunk.Position);
            station.SetChunkClassification(chunk, new VoxelClassification(1));
        }

        // One cubic metre of air at 293.15 K and one atmosphere holds about 41.6 mol.
        station.EditVoxelBox(
            new Int3(0, 0, 0),
            new Int3(15, 7, 0),
            new AtmosVoxelBoxEdit
            {
                Gases = [new AtmosGasAmount(oxygen, 8.73f), new AtmosGasAmount(nitrogen, 32.84f)],
                GasTemperatureK = 293.15f
            });

        // The patch expands and cools to about 500 K. That is below the default ignition temperature, so it starts to
        // burn only after the script lowers the setting.
        station.EditVoxelBox(new Int3(2, 2, 0), new Int3(4, 4, 0), new AtmosVoxelBoxEdit { TemperatureK = 1000f });

        world.Solvers.Add<FireSolver>();
        return world;
    }

    /// <summary>
    /// Builds one chunk of thin pure oxygen at 1000 K. The gas is uniform, so nothing flows until a voxel burns.
    /// </summary>
    /// <remarks>
    /// Each voxel holds 0.06 mol. Burning the default 0.05 mol of it with no ceiling heats the voxel by about 107,000 K.
    /// </remarks>
    public static AtmosWorld CreateThinOxygen()
    {
        var config = new AtmosConfig();
        config.GasRegistry.Add(new GasProperties
        {
            Name = "Oxygen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            MolarMass = 0.032f,
            DiffusionCoefficient = 0.02f
        });

        int oxygen = config.GasRegistry.GasIdToIndex("Oxygen");
        var world = new AtmosWorld(config, AtmosTransportModel.Euler, TickRate);
        AtmosSimulation simulation = world.CreateSimulation(8, 8, 1);
        simulation.CreateAndRegisterChunk(HotChunk.Position);
        simulation.SetChunkClassification(HotChunk, new VoxelClassification(1));
        simulation.EditVoxelBox(
            new Int3(0, 0, 0),
            new Int3(7, 7, 0),
            new AtmosVoxelBoxEdit { Gases = [new AtmosGasAmount(oxygen, 0.06f)], GasTemperatureK = 1000f });

        world.Solvers.Add<FireSolver>();
        return world;
    }

    /// <summary>
    /// Lights a cold voxel, lowers the ignition temperature, and fakes a long exposure in the hot patch.
    /// </summary>
    public static void Script(AtmosWorld world, long tick)
    {
        AtmosSimulation station = world.Simulations[0];
        switch (tick)
        {
            case IgniteTick:
                world.Solvers.Get<FireSolver>().Ignite(station.GetCellRef(ColdChunk, CenterVoxel));
                break;
            case SettingTick:
                world.Solvers.Get<FireSolver>().IgnitionTemperature = LoweredIgnitionTemperature;
                break;
            case ExposureTick:
                station.SetChunkData(HotChunk, Fire.Fields.Exposure, CenterVoxel, 10f);
                break;
        }
    }
}
