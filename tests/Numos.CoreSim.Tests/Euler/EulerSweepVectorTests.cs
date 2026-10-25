using System.Numerics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

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
        AtmosChunk chunk = CreateChunk(tickConfig, seed, dimension, solidPercent, speed);
        var workspace = new EulerChunkWorkspace();
        workspace.Bind(chunk, tickConfig);
        EulerFaceTopology.Build(chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
        EulerFaceTopology.BuildAirMap(chunk, workspace.AirMap, workspace.RowAllAir);
        workspace.Gather(tickConfig);
        return workspace;
    }

    private static AtmosChunk CreateChunk(
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

        return chunk;
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

    /// <summary>
    ///     A grid of chunks, each with a workspace unless it is left inactive. An inactive chunk is a wall with a
    ///     wake test.
    /// </summary>
    private static EulerChunkWorkspace[] CreateGrid(
        AtmosSolverConfigSnapshot tickConfig, Int3 size, int seed, int dimension, int solidPercent, float speed,
        int inactivePercent, bool corrupt)
    {
        var random = new Random(seed * 31 + 5);
        var chunks = new AtmosChunk[size.X * size.Y * size.Z];
        var workspaces = new EulerChunkWorkspace?[chunks.Length];
        int Slot(Int3 position) => position.X + size.X * (position.Y + size.Y * position.Z);
        for (int z = 0; z < size.Z; z++)
        for (int y = 0; y < size.Y; y++)
        for (int x = 0; x < size.X; x++)
        {
            var position = new Int3(x, y, z);
            int slot = Slot(position);
            // The middle chunk stays active, so it has a neighbour on both sides of every axis.
            bool middle = x == size.X / 2 && y == size.Y / 2 && z == size.Z / 2;
            bool active = random.Next(100) >= inactivePercent || middle;
            chunks[slot] = CreateChunk(tickConfig, seed * 1000 + slot, dimension, solidPercent, speed);
            chunks[slot].GridPosition = position;
            if (!active)
                continue;

            workspaces[slot] = new EulerChunkWorkspace();
            workspaces[slot]!.Bind(chunks[slot], tickConfig);
        }

        for (int slot = 0; slot < chunks.Length; slot++)
        {
            if (workspaces[slot] is not { } own)
                continue;

            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                Int3 target = chunks[slot].GridPosition + NeighborResolution.NeighborDirections[direction];
                if (target.X < 0 || target.Y < 0 || target.Z < 0 ||
                    target.X >= size.X || target.Y >= size.Y || target.Z >= size.Z)
                    continue;

                own.NeighborChunks[direction] = chunks[Slot(target)];
                own.NeighborWorkspaces[direction] = workspaces[Slot(target)];
            }
        }

        var result = new List<EulerChunkWorkspace>();
        foreach (EulerChunkWorkspace? workspace in workspaces)
        {
            if (workspace == null)
                continue;

            EulerFaceTopology.Build(workspace.Chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
            EulerFaceTopology.BuildAirMap(workspace.Chunk, workspace.AirMap, workspace.RowAllAir);
            workspace.AddGas(0, tickConfig);
            workspace.AddGas(1, tickConfig);
            workspace.Gather(tickConfig);
            if (corrupt)
                Corrupt(workspace, seed + result.Count);

            result.Add(workspace);
        }

        return result.ToArray();
    }

    private static (int Clamped, int NonFinite, int Negative, int Signal) SweepGrid(
        EulerChunkWorkspace[] workspaces, AtmosSolverConfigSnapshot tickConfig, bool vectorFaces, bool vectorCells)
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
                    // Every chunk sweeps before any swaps, because a face reads the neighbour's read buffer.
                    foreach (EulerChunkWorkspace workspace in workspaces)
                    {
                        int pencils = EulerSweep.PencilCount(workspace.Chunk, axis);
                        EulerSweep.RunPencils(workspace, axis, 0, pencils, 2e-5f, 1f, tickConfig);
                        for (int pencil = 0; pencil < pencils; pencil++)
                        {
                            clamped += workspace.PencilCounters[pencil].ClampedVelocity;
                            nonFinite += workspace.PencilCounters[pencil].NonFiniteCells;
                            negative += workspace.PencilCounters[pencil].NegativeMoles;
                            signal = signal * 31 + Bits(workspace.PencilSignalSpeed[pencil]);
                        }
                    }

                    foreach (EulerChunkWorkspace workspace in workspaces)
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

    [TestCase(6, 0, 0, 200f, 1)]
    [TestCase(6, 0, 30, 400f, 2)]
    [TestCase(6, 10, 0, 150f, 3)]
    [TestCase(8, 5, 25, 600f, 4)]
    [TestCase(5, 0, 0, 3000f, 5)]
    [TestCase(4, 20, 40, 300f, 6)]
    [TestCase(8, 0, 0, 30f, 7)]
    public void VectorEdgeFaces_GiveTheSameBitsAsTheScalarSweepAcrossChunks(
        int dimension, int solidPercent, int inactivePercent, float speed, int seed)
    {
        foreach (bool corrupt in new[] { false, true })
            AssertGridSweepsMatch(new Int3(2, 3, 3), dimension, solidPercent, inactivePercent, speed, seed, corrupt);
    }

    private static void AssertGridSweepsMatch(
        Int3 size, int dimension, int solidPercent, int inactivePercent, float speed, int seed, bool corrupt)
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware: the sweep keeps the scalar solver.");
        AtmosSolverConfigSnapshot tickConfig = CreateConfig();
        EulerChunkWorkspace[] scalar =
            CreateGrid(tickConfig, size, seed, dimension, solidPercent, speed, inactivePercent, corrupt);
        EulerChunkWorkspace[] vector =
            CreateGrid(tickConfig, size, seed, dimension, solidPercent, speed, inactivePercent, corrupt);

        var expectedCounts = SweepGrid(scalar, tickConfig, vectorFaces: false, vectorCells: false);
        var actualCounts = SweepGrid(vector, tickConfig, vectorFaces: true, vectorCells: true);
        Assert.That(actualCounts, Is.EqualTo(expectedCounts));

        int differences = 0;
        for (int index = 0; index < scalar.Length; index++)
        {
            EulerChunkWorkspace expectedWorkspace = scalar[index];
            EulerChunkWorkspace actualWorkspace = vector[index];
            Assert.That(actualWorkspace.WakeRequests, Is.EqualTo(expectedWorkspace.WakeRequests));
            EulerChunkState expected = expectedWorkspace.Read;
            EulerChunkState actual = actualWorkspace.Read;
            for (int voxel = 0; voxel < expectedWorkspace.Chunk.VoxelCount; voxel++)
            {
                if (!expectedWorkspace.AirMap[voxel])
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
                            Same(e.Density, a.Density) && Same(e.Pressure, a.Pressure) &&
                            Same(e.VelocityX, a.VelocityX) && Same(e.VelocityY, a.VelocityY) &&
                            Same(e.VelocityZ, a.VelocityZ) && Same(e.EnergyDensity, a.EnergyDensity);
                for (int gas = 0; gas < 2 && same; gas++)
                {
                    if (expected.Species[gas] != null)
                        same = Same(expected.Species[gas]![voxel], actual.Species[gas]![voxel]);
                }

                if (!same)
                    differences++;
            }
        }

        Assert.That(differences, Is.Zero, $"{differences} cells differ between the scalar and vector sweep");
    }
}
