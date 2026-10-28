using System.Numerics;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosVoxelBoxEditTests
{
    private static readonly Int3 Dimensions = new(4, 4, 1);

    private static AtmosSimulation CreateSimulation()
    {
        return new AtmosSimulation(
            new AtmosConfig
            {
                GasRegistry =
                [
                    new GasProperties { Name = "A", DiffusionCoefficient = 0.02f, MolarMass = 0.029f },
                    new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 32f, MolarMass = 0.044f }
                ],
                SleepThreshold = 10
            },
            Dimensions.X,
            Dimensions.Y,
            Dimensions.Z);
    }

    // Three chunks around the origin. The chunk at (1, 1, 0) is missing. One chunk sleeps, one has a solid voxel and
    // one holds gas, so every part of an edit meets a different starting state.
    private static void Populate(AtmosSimulation simulation)
    {
        var origin = simulation.CreateAndRegisterChunk(default);
        var east = simulation.CreateAndRegisterChunk(Int3.PosX);
        var north = simulation.CreateAndRegisterChunk(Int3.PosY);
        foreach (var chunk in new[] { origin, east, north })
            simulation.SetChunkClassification(chunk, new VoxelClassification(1));

        simulation.SetVoxelClassification(east, 5, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(origin, 3, 0, 2f, 400f);
        simulation.AddGasToVoxel(origin, 6, 1, 1.5f, 250f);
        simulation.AddGasToVoxel(east, 9, 0, 0.5f, 310f);
        simulation.SleepChunk(north);
    }

    // The per-voxel calls that a box edit replaces, in the order and with the chunk-wide shortcut that
    // WorkbenchSession.EditRegion used before it called EditVoxelBox.
    private static void ApplyPerVoxel(AtmosSimulation simulation, Int3 min, Int3 max, in AtmosVoxelBoxEdit edit)
    {
        var positions = simulation.GetChunkHandles().Select(static handle => handle.Position)
            .OrderBy(static position => position.Z).ThenBy(static position => position.Y)
            .ThenBy(static position => position.X);

        foreach (var position in positions)
        {
            var chunk = new AtmosChunkHandle(position);
            var voxels = new List<ushort>();
            int x0 = Math.Max(min.X, position.X * Dimensions.X) - position.X * Dimensions.X;
            int x1 = Math.Min(max.X, (position.X + 1) * Dimensions.X - 1) - position.X * Dimensions.X;
            int y0 = Math.Max(min.Y, position.Y * Dimensions.Y) - position.Y * Dimensions.Y;
            int y1 = Math.Min(max.Y, (position.Y + 1) * Dimensions.Y - 1) - position.Y * Dimensions.Y;
            int z0 = Math.Max(min.Z, position.Z * Dimensions.Z) - position.Z * Dimensions.Z;
            int z1 = Math.Min(max.Z, (position.Z + 1) * Dimensions.Z - 1) - position.Z * Dimensions.Z;
            if (x0 > x1 || y0 > y1 || z0 > z1)
                continue;

            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                voxels.Add((ushort)(Dimensions.X * (y + Dimensions.Y * z) + x));

            if (edit.Classification is { } classification)
            {
                if (voxels.Count == Dimensions.X * Dimensions.Y * Dimensions.Z)
                {
                    simulation.SetChunkClassification(chunk, classification);
                }
                else
                {
                    foreach (var voxel in voxels)
                        simulation.SetVoxelClassification(chunk, voxel, classification);
                }
            }

            Assert.That(simulation.TryGetChunkSnapshot(chunk, default, AtmosChunkSnapshotFields.VoxelClassification, out var snapshot));
            if (edit.ClearGas)
            {
                foreach (var voxel in voxels)
                {
                    if (!IsSolidOrVoid(snapshot, voxel))
                        simulation.GetVoxelGasMixture(chunk, voxel).Clear();
                }
            }

            if (edit.TemperatureK is { } temperature)
            {
                foreach (var voxel in voxels)
                    simulation.SetVoxelTemperature(chunk, voxel, temperature);
            }

            float gasTemperature = edit.GasTemperatureK ?? edit.TemperatureK ?? simulation.Config.DefaultTemperatureFallback;
            foreach (var gas in edit.Gases ?? [])
            {
                foreach (var voxel in voxels)
                    simulation.AddGasToVoxel(chunk, voxel, gas.GasId, gas.Moles, gasTemperature);
            }

            if (edit.Velocity is { } velocity)
            {
                var config = simulation.World.Config;
                Assert.That(simulation.TryGetChunkSnapshot(
                    chunk,
                    default,
                    AtmosChunkSnapshotFields.Gases | AtmosChunkSnapshotFields.VoxelClassification,
                    out var gases));

                foreach (var voxel in voxels)
                {
                    if (IsSolidOrVoid(gases, voxel))
                        continue;

                    float mass = FlowFieldMath.GetDensity(gases.Gases, voxel, new AtmosConfig(config)) *
                                 config.GetVoxelVolume();

                    if (!(mass > 0f))
                        continue;

                    simulation.SetVoxelMomentum(chunk, voxel, mass * velocity.X, mass * velocity.Y, mass * velocity.Z);
                }
            }
        }
    }

    private static bool IsSolidOrVoid(AtmosChunkSnapshot snapshot, ushort voxel)
    {
        int room = snapshot.VoxelRoomMap[voxel];
        return room == VoxelClassification.RoomSolid || room == VoxelClassification.RoomVoid;
    }

    private static IEnumerable<TestCaseData> Edits()
    {
        yield return new TestCaseData(
            new AtmosVoxelBoxEdit
            {
                Classification = new VoxelClassification(1),
                ClearGas = true,
                TemperatureK = 301f,
                Gases = [new AtmosGasAmount(1, 0.5f), new AtmosGasAmount(0, 0.25f)],
                GasTemperatureK = 311f,
                Velocity = new Vector3(1.5f, -2.5f, 0.5f)
            }).SetName("AllParts");

        yield return new TestCaseData(
            new AtmosVoxelBoxEdit
            {
                Gases = [new AtmosGasAmount(0, 1f)],
                GasTemperatureK = 280f,
                Velocity = new Vector3(3f, 0f, 0f)
            }).SetName("GasAndVelocityOverExistingGas");

        yield return new TestCaseData(
            new AtmosVoxelBoxEdit { Classification = VoxelClassification.RoomSolid, TemperatureK = 320f })
            .SetName("SolidWithTemperature");

        yield return new TestCaseData(new AtmosVoxelBoxEdit { ClearGas = true }).SetName("ClearOnly");
        yield return new TestCaseData(new AtmosVoxelBoxEdit { TemperatureK = 100f }).SetName("TemperatureOnly");
        yield return new TestCaseData(
            new AtmosVoxelBoxEdit { Gases = [new AtmosGasAmount(1, 2f)], TemperatureK = 350f })
            .SetName("GasDefaultsToVoxelTemperature");
    }

    private static readonly (Int3 Min, Int3 Max)[] Boxes =
    [
        (new Int3(-2, 1, 0), new Int3(6, 6, 0)),
        (new Int3(0, 0, 0), new Int3(7, 3, 0)),
        (new Int3(1, 1, 0), new Int3(2, 2, 0)),
        (new Int3(0, 0, 0), new Int3(3, 3, 0))
    ];

    [TestCaseSource(nameof(Edits))]
    public void EditVoxelBox_MatchesThePerVoxelCalls(AtmosVoxelBoxEdit edit)
    {
        foreach (var (min, max) in Boxes)
        {
            using var perVoxel = CreateSimulation();
            using var boxed = CreateSimulation();
            Populate(perVoxel);
            Populate(boxed);
            Assert.That(boxed.World.ComputeStateHash(), Is.EqualTo(perVoxel.World.ComputeStateHash()));

            var before = perVoxel.World.ComputeStateHash();
            ApplyPerVoxel(perVoxel, min, max, edit);
            Assert.That(perVoxel.World.ComputeStateHash(), Is.Not.EqualTo(before), "the edit must change the state");
            boxed.EditVoxelBox(min, max, edit);
            Assert.That(
                boxed.World.ComputeStateHash(),
                Is.EqualTo(perVoxel.World.ComputeStateHash()),
                $"box {min} to {max}");

            for (int tick = 0; tick < 5; tick++)
            {
                perVoxel.World.Tick();
                boxed.World.Tick();
            }

            Assert.That(boxed.World.ComputeStateHash(), Is.EqualTo(perVoxel.World.ComputeStateHash()));
        }
    }

    [Test]
    public void EditVoxelBox_RecordsOneOperation()
    {
        using var simulation = CreateSimulation();
        Populate(simulation);
        simulation.World.StartRecording();
        var result = simulation.EditVoxelBox(
            new Int3(-2, 1, 0),
            new Int3(6, 6, 0),
            new AtmosVoxelBoxEdit
            {
                Classification = new VoxelClassification(1),
                TemperatureK = 301f,
                Gases = [new AtmosGasAmount(0, 0.25f)],
                Velocity = new Vector3(1f, 0f, 0f)
            });

        var recording = simulation.World.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(result.ChunksTouched, Is.EqualTo(3));
            Assert.That(recording.Operations, Has.Count.EqualTo(1));
            var wrapped = recording.Operations[0].Operation as AtmosWorldSimulationOperation;
            Assert.That(wrapped?.Operation, Is.InstanceOf<EditVoxelBoxOperation>());
        });
    }

    [Test]
    public void EditVoxelBox_ReplaysToTheLiveState()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        Populate(simulation);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        simulation.EditVoxelBox(
            new Int3(-2, 1, 0),
            new Int3(6, 6, 0),
            new AtmosVoxelBoxEdit
            {
                Classification = new VoxelClassification(1),
                ClearGas = true,
                TemperatureK = 301f,
                Gases = [new AtmosGasAmount(1, 0.5f), new AtmosGasAmount(0, 0.25f)],
                GasTemperatureK = 311f,
                Velocity = new Vector3(1.5f, -2.5f, 0.5f)
            });

        world.Tick();
        world.Tick();
        var head = world.CaptureCheckpoint();
        var recording = world.StopRecording();
        var liveHash = world.ComputeStateHash();

        world.ReplayTo(initial, recording.Operations, head.Position);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(liveHash));
    }

    [Test]
    public void EditVoxelBox_ReportsSkippedVoxelsAndLeavesMissingChunksAlone()
    {
        using var simulation = CreateSimulation();
        Populate(simulation);

        // The box covers 8 x 8 voxels. Chunks (0, 0), (1, 0), and (0, 1) hold 48 of them.
        var result = simulation.EditVoxelBox(
            new Int3(0, 0, 0),
            new Int3(7, 7, 0),
            new AtmosVoxelBoxEdit { TemperatureK = 280f });

        Assert.Multiple(() =>
        {
            Assert.That(result.VoxelsEdited, Is.EqualTo(48));
            Assert.That(result.VoxelsSkipped, Is.EqualTo(16));
            Assert.That(result.ChunksTouched, Is.EqualTo(3));
            Assert.That(simulation.GetChunkHandles(), Has.Length.EqualTo(3));
        });
    }

    [Test]
    public void EditVoxelBox_CountsVelocityVoxels()
    {
        using var simulation = CreateSimulation();
        Populate(simulation);
        var result = simulation.EditVoxelBox(
            new Int3(0, 0, 0),
            new Int3(3, 3, 0),
            new AtmosVoxelBoxEdit { Velocity = new Vector3(1f, 0f, 0f) });

        // Only the two gas-bearing voxels of the origin chunk carry momentum.
        Assert.That(result.VelocityVoxels, Is.EqualTo(2));
    }

    private static IEnumerable<TestCaseData> InvalidEdits()
    {
        yield return new TestCaseData(new Int3(2, 0, 0), new Int3(1, 0, 0), new AtmosVoxelBoxEdit())
            .SetName("MinAboveMax");

        yield return new TestCaseData(
            new Int3(0, 0, 0), new Int3(1, 1, 0), new AtmosVoxelBoxEdit { Gases = [new AtmosGasAmount(2, 1f)] })
            .SetName("UnknownGas");

        yield return new TestCaseData(
            new Int3(0, 0, 0), new Int3(1, 1, 0), new AtmosVoxelBoxEdit { Gases = [new AtmosGasAmount(0, 0f)] })
            .SetName("ZeroMoles");

        yield return new TestCaseData(
            new Int3(0, 0, 0), new Int3(1, 1, 0), new AtmosVoxelBoxEdit { Gases = [new AtmosGasAmount(0, float.NaN)] })
            .SetName("NonFiniteMoles");

        yield return new TestCaseData(
            new Int3(0, 0, 0), new Int3(1, 1, 0), new AtmosVoxelBoxEdit { TemperatureK = float.PositiveInfinity })
            .SetName("NonFiniteTemperature");

        yield return new TestCaseData(
            new Int3(0, 0, 0), new Int3(1, 1, 0), new AtmosVoxelBoxEdit { TemperatureK = -1f })
            .SetName("NegativeTemperature");

        yield return new TestCaseData(
            new Int3(0, 0, 0),
            new Int3(1, 1, 0),
            new AtmosVoxelBoxEdit { Gases = [new AtmosGasAmount(0, 1f)], GasTemperatureK = -5f })
            .SetName("NegativeGasTemperature");

        yield return new TestCaseData(
            new Int3(0, 0, 0), new Int3(1, 1, 0), new AtmosVoxelBoxEdit { Velocity = new Vector3(float.NaN, 0f, 0f) })
            .SetName("NonFiniteVelocity");
    }

    [TestCaseSource(nameof(InvalidEdits))]
    public void EditVoxelBox_RejectsAnInvalidEditBeforeChangingAnything(Int3 min, Int3 max, AtmosVoxelBoxEdit edit)
    {
        using var simulation = CreateSimulation();
        Populate(simulation);
        var before = simulation.World.ComputeStateHash();
        simulation.World.StartRecording();

        Assert.Throws<ArgumentException>(() => simulation.EditVoxelBox(min, max, edit));

        var recording = simulation.World.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.World.ComputeStateHash(), Is.EqualTo(before));
            Assert.That(recording.Operations, Is.Empty);
        });
    }
}
