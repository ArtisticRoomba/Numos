using System.Numerics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

[NonParallelizable]
public class EulerSweepVectorTests
{
    private const float AirMolarHeatCapacity = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;
    private const float MolarMass = 0.02897f;

    private static AtmosSolverConfigSnapshot CreateConfig()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = 1f,
            GasRegistry =
            [
                new GasProperties
                {
                    Name = "Air", MolarMass = MolarMass, MolarHeatCapacityAtConstantVolume = AirMolarHeatCapacity
                },
                new GasProperties
                {
                    Name = "Heavy", MolarMass = 0.044f, MolarHeatCapacityAtConstantVolume = AirMolarHeatCapacity
                }
            ]
        }.CreateSnapshot();

        var tickConfig = new AtmosSolverConfigSnapshot();
        tickConfig.Capture(config);
        return tickConfig;
    }

    private static EulerChunkWorkspace CreateWorkspace(
        AtmosSolverConfigSnapshot tickConfig, int seed, int dimension, int solidPercent, float speed)
    {
        var random = new Random(seed);
        var chunk = new AtmosChunk(dimension, dimension, dimension);
        chunk.SetChunkClassification(1);
        chunk.Wake();
        for (ushort voxel = 0; voxel < chunk.VoxelCount; voxel++)
        {
            if (random.Next(100) < solidPercent)
                chunk.VoxelRoomMap[voxel] = VoxelClassification.RoomSolid;
            else if (random.Next(100) < solidPercent / 4)
                chunk.VoxelRoomMap[voxel] = VoxelClassification.RoomVoid;
        }

        chunk.RebuildActiveAirIndices();
        float standardMoles = AtmosPhysicalConstants.StandardAtmosphericPressure /
                              (AtmosPhysicalConstants.MolarGasConstant * 293f);
        for (int active = 0; active < chunk.ActiveAirCount; active++)
        {
            ushort voxel = chunk.ActiveAirIndices[active];
            float temperature = 150f + 300f * (float)random.NextDouble();
            float moles = standardMoles * (0.2f + 1.8f * (float)random.NextDouble());
            chunk.InjectGasToVoxel(voxel, 0, moles, temperature, AirMolarHeatCapacity, tickConfig.PressurePerMoleKelvin);
            if (random.Next(3) == 0)
                chunk.InjectGasToVoxel(
                    voxel, 1, moles * 0.3f, temperature, AirMolarHeatCapacity, tickConfig.PressurePerMoleKelvin);

            float mass = moles * (MolarMass + 0.01f);
            chunk.MomentumX[voxel] = mass * speed * (2f * (float)random.NextDouble() - 1f);
            chunk.MomentumY[voxel] = mass * speed * (2f * (float)random.NextDouble() - 1f);
            chunk.MomentumZ[voxel] = mass * speed * (2f * (float)random.NextDouble() - 1f);
        }

        var workspace = new EulerChunkWorkspace();
        workspace.Bind(chunk, tickConfig);
        EulerFaceTopology.Build(chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
        EulerFaceTopology.BuildAirMap(chunk, workspace.AirMap, workspace.RowAllAir);
        workspace.Gather(tickConfig);
        return workspace;
    }

    private static void Sweep(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot tickConfig, bool vector)
    {
        bool saved = HllcFluxVector.Enabled;
        HllcFluxVector.Enabled = vector;
        try
        {
            for (int round = 0; round < 2; round++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    EulerSweep.RunPencils(workspace, axis, 0, EulerSweep.PencilCount(workspace.Chunk, axis), 2e-5f, 1f, tickConfig);
                    workspace.Swap();
                }
            }
        }
        finally
        {
            HllcFluxVector.Enabled = saved;
        }
    }

    private static int Bits(float value)
    {
        return BitConverter.SingleToInt32Bits(value);
    }

    [TestCase(8, 0, 30f, 1)]
    [TestCase(8, 0, 400f, 2)]
    [TestCase(8, 10, 150f, 3)]
    [TestCase(8, 35, 400f, 4)]
    [TestCase(5, 0, 200f, 5)]
    [TestCase(5, 20, 600f, 6)]
    [TestCase(11, 15, 300f, 7)]
    [TestCase(3, 0, 300f, 8)]
    public void VectorFaceSolve_GivesTheSameSweepBitsAsTheScalarSolver(int dimension, int solidPercent, float speed, int seed)
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware: the sweep keeps the scalar solver.");
        AtmosSolverConfigSnapshot tickConfig = CreateConfig();
        EulerChunkWorkspace scalar = CreateWorkspace(tickConfig, seed, dimension, solidPercent, speed);
        EulerChunkWorkspace vector = CreateWorkspace(tickConfig, seed, dimension, solidPercent, speed);

        Sweep(scalar, tickConfig, vector: false);
        Sweep(vector, tickConfig, vector: true);

        EulerChunkState expected = scalar.Read;
        EulerChunkState actual = vector.Read;
        int differences = 0;
        for (int voxel = 0; voxel < scalar.Chunk.VoxelCount; voxel++)
        {
            if (!scalar.AirMap[voxel])
                continue;

            bool same = BitConverter.DoubleToInt64Bits(expected.Energy[voxel]) ==
                        BitConverter.DoubleToInt64Bits(actual.Energy[voxel]) &&
                        Bits(expected.MomentumX[voxel]) == Bits(actual.MomentumX[voxel]) &&
                        Bits(expected.MomentumY[voxel]) == Bits(actual.MomentumY[voxel]) &&
                        Bits(expected.MomentumZ[voxel]) == Bits(actual.MomentumZ[voxel]) &&
                        Bits(expected.Mass[voxel]) == Bits(actual.Mass[voxel]) &&
                        Bits(expected.TotalMoles[voxel]) == Bits(actual.TotalMoles[voxel]) &&
                        Bits(expected.Cells[voxel].Pressure) == Bits(actual.Cells[voxel].Pressure) &&
                        Bits(expected.Cells[voxel].Density) == Bits(actual.Cells[voxel].Density) &&
                        Bits(expected.Cells[voxel].SoundSpeed) == Bits(actual.Cells[voxel].SoundSpeed);
            for (int gas = 0; gas < 2 && same; gas++)
            {
                if (expected.Species[gas] != null)
                    same = Bits(expected.Species[gas]![voxel]) == Bits(actual.Species[gas]![voxel]);
            }

            if (!same)
                differences++;
        }

        Assert.That(differences, Is.Zero, $"{differences} cells differ between the scalar and vector face solve");
    }
}
