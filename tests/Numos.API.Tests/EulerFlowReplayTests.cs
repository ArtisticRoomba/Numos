using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API.Tests;

/// <summary>
///     The Euler stage under the replay contract: momentum is authoritative state that rides in checkpoints and
///     replays, while sweep parity and the active chunk set are derived and rebuilt. Both are checked the only way
///     that counts, by comparing state hashes against an uninterrupted run.
/// </summary>
[TestFixture]
public sealed class EulerFlowReplayTests
{
    private const int Width = 4;
    private const int Height = 4;

    [Test]
    public void ReplayTo_ReproducesEveryRecordedPosition()
    {
        using var world = CreateWorld(out var simulation, out var left, out var right);
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        var expected = new List<(AtmosTimelinePosition Position, AtmosWorldStateHash Hash)>();

        Tick(world, 3, expected);
        simulation.SetVoxelMomentum(left, 1, 1, 0, 2f, -1f, 0f);
        Observe(world, expected);
        Tick(world, 2, expected);
        // Emptying a voxel next to pressurised gas puts an expansion into vacuum in the middle of the recording.
        simulation.GetVoxelGasMixture(right, 0).Clear();
        Observe(world, expected);
        Tick(world, 8, expected);

        var recording = world.StopRecording();
        Assert.That(recording.Operations, Has.Count.EqualTo(2));
        Assert.That(expected.Select(static entry => entry.Position).Distinct().Count(), Is.EqualTo(expected.Count));

        foreach ((AtmosTimelinePosition position, AtmosWorldStateHash hash) in expected)
        {
            world.ReplayTo(checkpoint, recording.Operations, position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash), $"replayed to {position}");
        }
    }

    [Test]
    public void RestoreCheckpoint_ResumesBitwiseWithMomentumInFlight()
    {
        using var reference = CreateWorld(out var referenceSimulation, out var referenceLeft, out _);
        using var restored = CreateWorld(out var restoredSimulation, out var restoredLeft, out _);
        for (int tick = 0; tick < 3; tick++)
        {
            reference.Tick();
            restored.Tick();
        }

        referenceSimulation.SetVoxelMomentum(referenceLeft, 1, 1, 0, 2f, -1f, 0f);
        restoredSimulation.SetVoxelMomentum(restoredLeft, 1, 1, 0, 2f, -1f, 0f);
        reference.Tick();
        restored.Tick();

        var snapshot = restored.CaptureCheckpoint();
        var velocityAtCheckpoint = restoredSimulation.GetChunkSnapshot(restoredLeft).VelocityX;
        Assert.That(velocityAtCheckpoint.Any(static velocity => velocity != 0f), "the checkpoint should hold gas in motion");

        for (int tick = 0; tick < 5; tick++)
            restored.Tick();

        restored.RestoreCheckpoint(snapshot);
        Assert.That(restored.ComputeStateHash(), Is.EqualTo(reference.ComputeStateHash()), "momentum round-trips");

        for (int tick = 0; tick < 6; tick++)
        {
            reference.Tick();
            restored.Tick();
            Assert.That(restored.ComputeStateHash(), Is.EqualTo(reference.ComputeStateHash()), $"tick {tick + 1} after restore");
        }
    }

    private static void Tick(AtmosWorld world, int count, List<(AtmosTimelinePosition, AtmosWorldStateHash)> expected)
    {
        for (int tick = 0; tick < count; tick++)
        {
            world.Tick();
            Observe(world, expected);
        }
    }

    private static void Observe(AtmosWorld world, List<(AtmosTimelinePosition, AtmosWorldStateHash)> expected)
    {
        expected.Add((world.TimelinePosition, world.ComputeStateHash()));
    }

    /// <summary>
    ///     Two 4×4×1 chunks of SI air side by side, the left one at twice the pressure, with a trace gas in one
    ///     voxel so species rows cross the chunk face. The sleep threshold is short enough for chunks to doze off
    ///     inside the recorded span once the step has equalised.
    /// </summary>
    private static AtmosWorld CreateWorld(out AtmosSimulation simulation, out AtmosChunkHandle left, out AtmosChunkHandle right)
    {
        var config = new AtmosConfig
        {
            VoxelVolume = 1f,
            DefaultDiffusionCoefficient = 0f,
            VacuumThreshold = 0f,
            SleepThreshold = 6,
            SleepEpsilon = 0.5f,
            GasRegistry =
            [
                new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = 20.786f, MolarMass = 0.02897f },
                new GasProperties { Name = "Trace", MolarHeatCapacityAtConstantVolume = 28f, MolarMass = 0.044f }
            ]
        };

        var world = new AtmosWorld(config);
        simulation = world.CreateSimulation(Width, Height, 1);
        left = simulation.CreateAndRegisterChunk(default);
        right = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(left, new VoxelClassification(1));
        simulation.SetChunkClassification(right, new VoxelClassification(1));
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            simulation.AddGasToVoxel(left, x, y, 0, "Air", 80f, 300f);
            simulation.AddGasToVoxel(right, x, y, 0, "Air", 40f, 300f);
        }

        simulation.AddGasToVoxel(left, Width - 1, 0, 0, "Trace", 5f, 300f);
        return world;
    }
}
