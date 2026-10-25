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
            int oddity = random.Next(100);
            if (oddity < 4)
                moles = 5e-5f;
            else if (oddity < 6)
                temperature = 3e35f;
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

    private static readonly float[] Oddities =
    [
        0f, -0f, 1f, -1f, 1e-30f, 1e30f, float.MaxValue, float.MinValue, float.Epsilon, float.PositiveInfinity,
        float.NegativeInfinity, float.NaN
    ];

    /// <summary>
    ///     Replaces a few percent of the fields of the read buffer with zeros, infinities, NaNs, and extremes.
    /// </summary>
    private static void Corrupt(EulerChunkWorkspace workspace, int seed)
    {
        var random = new Random(seed * 7919);
        EulerChunkState read = workspace.Read;
        float Pick() => Oddities[random.Next(Oddities.Length)];
        for (int voxel = 0; voxel < workspace.Chunk.VoxelCount; voxel++)
        {
            if (!workspace.AirMap[voxel] || random.Next(100) >= 8)
                continue;

            switch (random.Next(8))
            {
                case 0:
                    read.Species[0]![voxel] = Pick();
                    break;
                case 1:
                    read.MomentumX[voxel] = Pick();
                    break;
                case 2:
                    read.MomentumY[voxel] = Pick();
                    read.MomentumZ[voxel] = Pick();
                    break;
                case 3:
                    read.Energy[voxel] = Pick();
                    break;
                case 4:
                    read.HeatCapacity[voxel] = Pick();
                    break;
                case 5:
                    read.Cells[voxel].Pressure = Pick();
                    read.Cells[voxel].SoundSpeed = Pick();
                    break;
                case 6:
                    read.Cells[voxel].VelocityX = Pick();
                    read.Cells[voxel].Density = Pick();
                    break;
                default:
                    read.Cells[voxel].EnergyDensity = Pick();
                    read.Cells[voxel].InverseMass = Pick();
                    break;
            }
        }
    }

    private static (int Clamped, int NonFinite, int Negative, int Signal) Sweep(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot tickConfig, bool vectorFaces, bool vectorCells)
    {
        int clamped = 0;
        int nonFinite = 0;
        int negative = 0;
        int signal = 0;
        bool savedFaces = HllcFluxVector.Enabled;
        bool savedCells = EulerCellKernel.Enabled;
        HllcFluxVector.Enabled = vectorFaces;
        EulerCellKernel.Enabled = vectorCells;
        try
        {
            for (int round = 0; round < 2; round++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    EulerSweep.RunPencils(workspace, axis, 0, EulerSweep.PencilCount(workspace.Chunk, axis), 2e-5f, 1f, tickConfig);
                    for (int pencil = 0; pencil < EulerSweep.PencilCount(workspace.Chunk, axis); pencil++)
                    {
                        clamped += workspace.PencilCounters[pencil].ClampedVelocity;
                        nonFinite += workspace.PencilCounters[pencil].NonFiniteCells;
                        negative += workspace.PencilCounters[pencil].NegativeMoles;
                        signal = signal * 31 + Bits(workspace.PencilSignalSpeed[pencil]);
                    }

                    workspace.Swap();
                }
            }

            return (clamped, nonFinite, negative, signal);
        }
        finally
        {
            HllcFluxVector.Enabled = savedFaces;
            EulerCellKernel.Enabled = savedCells;
        }
    }

    /// <summary>
    ///     Bitwise equality, except that any two NaNs match, because the payload an operation on a NaN returns depends
    ///     on the operand order the compiler picks.
    /// </summary>
    private static bool Same(float expected, float actual)
    {
        return float.IsNaN(expected) ? float.IsNaN(actual) : Bits(expected) == Bits(actual);
    }

    private static bool SameDouble(double expected, double actual)
    {
        return double.IsNaN(expected)
            ? double.IsNaN(actual)
            : BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual);
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
    [TestCase(16, 0, 900f, 9)]
    [TestCase(8, 0, 3000f, 10)]
    public void VectorSweep_GivesTheSameBitsAsTheScalarSweep(int dimension, int solidPercent, float speed, int seed)
    {
        foreach ((bool faces, bool cells) in new[] { (true, false), (false, true), (true, true) })
            AssertSweepsMatch(dimension, solidPercent, speed, seed, faces, cells);
    }

    [TestCase(8, 0, 200f, 21)]
    [TestCase(8, 10, 600f, 22)]
    [TestCase(6, 0, 100f, 23)]
    [TestCase(16, 5, 400f, 24)]
    public void VectorSweep_GivesTheSameBitsOnCorruptedState(int dimension, int solidPercent, float speed, int seed)
    {
        foreach ((bool faces, bool cells) in new[] { (true, false), (false, true), (true, true) })
            AssertSweepsMatch(dimension, solidPercent, speed, seed, faces, cells, corrupt: true);
    }

    private static void AssertSweepsMatch(
        int dimension, int solidPercent, float speed, int seed, bool vectorFaces, bool vectorCells,
        bool corrupt = false)
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware: the sweep keeps the scalar solver.");
        AtmosSolverConfigSnapshot tickConfig = CreateConfig();
        EulerChunkWorkspace scalar = CreateWorkspace(tickConfig, seed, dimension, solidPercent, speed);
        EulerChunkWorkspace vector = CreateWorkspace(tickConfig, seed, dimension, solidPercent, speed);

        if (corrupt)
        {
            Corrupt(scalar, seed);
            Corrupt(vector, seed);
        }

        var expectedCounts = Sweep(scalar, tickConfig, vectorFaces: false, vectorCells: false);
        var actualCounts = Sweep(vector, tickConfig, vectorFaces, vectorCells);
        TestContext.Out.WriteLine($"counters {expectedCounts}");
        Assert.That(actualCounts, Is.EqualTo(expectedCounts));

        EulerChunkState expected = scalar.Read;
        EulerChunkState actual = vector.Read;
        int differences = 0;
        for (int voxel = 0; voxel < scalar.Chunk.VoxelCount; voxel++)
        {
            if (!scalar.AirMap[voxel])
                continue;

            EulerCellState e = expected.Cells[voxel];
            EulerCellState a = actual.Cells[voxel];
            bool same = SameDouble(expected.Energy[voxel], actual.Energy[voxel]) &&
                        SameDouble(expected.HeatCapacity[voxel], actual.HeatCapacity[voxel]) &&
                        Same(expected.MomentumX[voxel], actual.MomentumX[voxel]) &&
                        Same(expected.MomentumY[voxel], actual.MomentumY[voxel]) &&
                        Same(expected.MomentumZ[voxel], actual.MomentumZ[voxel]) &&
                        Same(expected.Mass[voxel], actual.Mass[voxel]) &&
                        Same(expected.TotalMoles[voxel], actual.TotalMoles[voxel]) &&
                        Same(e.Density, a.Density) && Same(e.VelocityX, a.VelocityX) &&
                        Same(e.VelocityY, a.VelocityY) && Same(e.VelocityZ, a.VelocityZ) &&
                        Same(e.Pressure, a.Pressure) && Same(e.EnergyDensity, a.EnergyDensity) &&
                        Same(e.SoundSpeed, a.SoundSpeed) && Same(e.InverseMass, a.InverseMass) &&
                        Same(e.Temperature, a.Temperature) && Same(e.SignalSpeed, a.SignalSpeed);
            for (int gas = 0; gas < 2 && same; gas++)
            {
                if (expected.Species[gas] != null)
                    same = Same(expected.Species[gas]![voxel], actual.Species[gas]![voxel]);
            }

            if (!same)
                differences++;
        }

        Assert.That(differences, Is.Zero, $"{differences} cells differ between the scalar and vector sweep (faces {vectorFaces}, cells {vectorCells})");
    }
}
