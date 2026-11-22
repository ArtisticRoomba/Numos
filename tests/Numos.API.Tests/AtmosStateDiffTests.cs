using Numos.API.Dangerous;
using Numos.Collections;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Tests;

namespace Numos.API.Tests;

// The chunk field registry is process-wide, so the key is namespaced to this file.
internal static partial class StateDiffData
{
    public const string ExposureKey = "tests/state-diff/exposure";

    [ChunkData(ExposureKey, AccessorName = "DiffExposure")]
    public static partial ChunkField<float> Exposure { get; }
}

internal sealed partial class StateDiffFireSolver : IAtmosWorldSolver
{
    [NumosSerialize] private int _ticksSinceSpread;

    [NumosSerialize] public float SpreadRate { get; set; } = 0.5f;

    public int TicksSinceSpread
    {
        get => _ticksSinceSpread;
        set => _ticksSinceSpread = value;
    }

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

/// <summary>
///     Where <see cref="AtmosWorldCheckpoint.Diff" /> says two world states differ.
/// </summary>
[TestFixture]
public sealed class AtmosStateDiffTests
{
    private const string SolverName = "tests/state-diff/fire";
    private const string Chunk = "Simulations / 0 / Chunks / (0,0,0)";

    [Test]
    public void IdenticalStates_HaveNoDifferences()
    {
        var first = Capture();
        var second = Capture();

        Assert.Multiple(() =>
        {
            Assert.That(first.ComputeStateHash(), Is.EqualTo(second.ComputeStateHash()));
            Assert.That(first.Diff(second), Is.Empty);
            Assert.That(first.Diff(first), Is.Empty);
        });
    }

    [Test]
    public void VoxelTemperature_IsOneArrayElement()
    {
        var left = Capture(static fixture => Voxels(fixture).Temperature[3] = 300f);
        var right = Capture(static fixture => Voxels(fixture).Temperature[3] = 301.5f);

        Assert.That(
            left.Diff(right),
            Is.EqualTo(new[] { new AtmosStateDifference(Chunk + " / Temperatures[3]", "300 vs 301.5; 1 of 16 elements differ") }));
    }

    [Test]
    public void SignedZero_PrintsTheBits()
    {
        var left = Capture(static fixture => Voxels(fixture).Temperature[2] = 0f);
        var right = Capture(static fixture => Voxels(fixture).Temperature[2] = -0f);

        Assert.That(
            left.Diff(right).Single().Detail,
            Is.EqualTo("0 vs -0 (bits 0x00000000 vs 0x80000000); 1 of 16 elements differ"));
    }

    [Test]
    public void GasMoles_IsOneElementOfTheChannel()
    {
        var left = Capture();
        var right = Capture(static fixture => Voxels(fixture).GetGasChannel(0).Moles[5] = 2.5f);

        Assert.That(
            left.Diff(right),
            Is.EqualTo(new[] { new AtmosStateDifference(Chunk + " / Gases / 0 / Moles[5]", "2 vs 2.5; 1 of 16 elements differ") }));
    }

    [Test]
    public void ConfigScalar_IsNamed()
    {
        var left = Capture();
        var config = new TestAtmosConfig { BulkFlowCoefficient = 0.25f };
        var right = Capture(config: config);

        var difference = left.Diff(right).Single();
        Assert.Multiple(() =>
        {
            Assert.That(difference.Path, Is.EqualTo("Config / BulkFlowCoefficient"));
            Assert.That(difference.Detail, Does.EndWith(" vs 0.25"));
        });
    }

    [Test]
    public void SolverConfigurations_MatchByKey()
    {
        var slow = new TestAtmosConfig();
        slow.SolverConfigurations.Add(new EulerFlowConfig { CourantNumber = 0.4f });
        var standard = new TestAtmosConfig();
        standard.SolverConfigurations.Add(new EulerFlowConfig());

        Assert.Multiple(() =>
        {
            Assert.That(
                Capture(config: standard).Diff(Capture(config: slow)),
                Is.EqualTo(
                    new[] { new AtmosStateDifference("Config / SolverConfigurations / euler-flow / CourantNumber", "0.5 vs 0.4") }));

            Assert.That(
                Capture().Diff(Capture(config: slow)),
                Is.EqualTo(new[] { new AtmosStateDifference("Config / SolverConfigurations / euler-flow", "present only on the right") }));
        });
    }

    [Test]
    public void SolverEnabledFlag_IsNamedBySolver()
    {
        var left = Capture();
        var right = Capture(static fixture => fixture.World.Solvers.SetEnabled(SolverName, false));

        Assert.That(
            left.Diff(right),
            Is.EqualTo(new[] { new AtmosStateDifference("Solvers / " + SolverName + " / Enabled", "true vs false") }));
    }

    [Test]
    public void SolverStateField_IsNamedByMember()
    {
        var left = Capture();
        var right = Capture(static fixture => fixture.Solver.TicksSinceSpread = 5);

        var difference = left.Diff(right).Single();
        Assert.Multiple(() =>
        {
            Assert.That(difference.Path, Is.EqualTo("SolverStates / " + SolverName + " / _ticksSinceSpread"));
            Assert.That(difference.Detail, Does.StartWith("0 vs 5 as int32"));
        });
    }

    [Test]
    public void RollbackChunkField_IsOneElementOfTheArray()
    {
        // A chunk allocates a field on first use, so both sides touch it.
        var left = Capture(static fixture => Exposure(fixture)[7] = 0f);
        var right = Capture(static fixture => Exposure(fixture)[7] = 2f);

        Assert.That(
            left.Diff(right),
            Is.EqualTo(
                new[]
                {
                    new AtmosStateDifference(
                        Chunk + " / SolverArrays / " + StateDiffData.ExposureKey + "[7]",
                        "0 vs 2; 1 of 16 elements differ")
                }));
    }

    [Test]
    public void OneMoreTick_ReportsThePositionFirst()
    {
        var left = Capture();
        var right = Capture(static fixture => fixture.World.Tick());

        var differences = left.Diff(right);
        Assert.That(differences[0], Is.EqualTo(new AtmosStateDifference("Position / Tick", "0 vs 1")));
    }

    [Test]
    public void ExtraChunk_IsOneEntry()
    {
        var left = Capture();
        var right = Capture(static fixture => fixture.Simulation.CreateAndRegisterChunk(new Int3(1, 0, 0)));

        Assert.That(
            left.Diff(right),
            Is.EqualTo(new[] { new AtmosStateDifference("Simulations / 0 / Chunks / (1,0,0)", "present only on the right") }));
    }

    [Test]
    public void ExtraSimulation_IsOneEntryPlusItsRegistrySlot()
    {
        var left = Capture(static fixture => fixture.World.CreateSimulation(2, 2, 2));
        var right = Capture();

        // The world keeps a generation per simulation slot, and that list is hashed state of its own.
        Assert.That(
            left.Diff(right),
            Is.EqualTo(
                new[]
                {
                    new AtmosStateDifference("Simulations / 1", "present only on the left"),
                    new AtmosStateDifference("SimulationGenerations", "2 vs 1 elements")
                }));
    }

    [Test]
    public void GasMixtureTemperature_IsInItsPage()
    {
        var left = Capture();
        var right = Capture(static fixture => fixture.Mixture.Temperature = 310f);

        var difference = left.Diff(right).Single();
        Assert.Multiple(() =>
        {
            Assert.That(difference.Path, Is.EqualTo("GasMixtures / Pages / 0 / Temperatures[0]"));
            Assert.That(difference.Detail, Does.EndWith(" vs 310; 1 of 256 elements differ"));
        });
    }

    [Test]
    public void UnmarkedCounterThatFeedsGas_DivergesInTheChunkItWrites()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        foreach (string stage in AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Euler))
            world.Solvers.SetEnabled(stage, false);

        var simulation = world.CreateSimulation(4, 2, 2);
        var near = simulation.CreateAndRegisterChunk(Int3.Zero);
        var far = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.SetChunkClassification(near, new VoxelClassification(1));
        simulation.SetChunkClassification(far, new VoxelClassification(1));
        simulation.AddGasToVoxel(near, 0, "TestGas0", 1f, 300f);
        simulation.AddGasToVoxel(far, 0, "TestGas0", 1f, 300f);

        // Outside the checkpoint, so a restore does not rewind it.
        int counter = 0;
        world.Solvers.Register(
            "tests/state-diff/leaky",
            context =>
            {
                counter++;
                foreach (var chunk in context.Chunks(simulation))
                {
                    if (chunk.Position == new Int3(1, 0, 0))
                        chunk.Dangerous().GetGasChannel(0).Moles[6] += 0.25f * counter;
                }
            });

        var start = world.CaptureCheckpoint();
        for (int tick = 0; tick < 5; tick++)
            world.Tick();

        var first = world.CaptureCheckpoint();
        world.RestoreCheckpoint(start);
        for (int tick = 0; tick < 5; tick++)
            world.Tick();

        var second = world.CaptureCheckpoint();
        var differences = first.Diff(second);

        Assert.Multiple(() =>
        {
            Assert.That(first.ComputeStateHash(), Is.Not.EqualTo(second.ComputeStateHash()));
            Assert.That(differences, Is.Not.Empty);
            Assert.That(differences[0].Path, Is.EqualTo("Simulations / 0 / Chunks / (1,0,0) / Gases / 0 / Moles[6]"));
        });
    }

    [Test]
    public void MaxDifferences_CapsTheList()
    {
        var left = Capture();
        var right = Capture(
            static fixture =>
            {
                var voxels = Voxels(fixture);
                voxels.Temperature[0] = 1f;
                voxels.GetGasChannel(0).Moles[1] = 1f;
                fixture.Solver.TicksSinceSpread = 3;
                fixture.World.Solvers.SetEnabled(SolverName, false);
            });

        Assert.Multiple(() =>
        {
            Assert.That(left.Diff(right), Has.Count.EqualTo(4));
            Assert.That(left.Diff(right, 2), Has.Count.EqualTo(2));
            Assert.That(left.Diff(right, 1), Has.Count.EqualTo(1));
            Assert.That(() => left.Diff(right, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void RandomVoxelEdits_DiffIsEmptyExactlyWhenTheHashesMatch()
    {
        var baseline = Capture();
        float[] values = [0f, -0f, 300f, 300.5f, float.NaN];
        var random = new Random(1234);
        int equal = 0;
        int different = 0;
        for (int trial = 0; trial < 24; trial++)
        {
            int voxel = random.Next(16);
            float value = values[random.Next(values.Length)];
            bool moles = random.Next(2) == 0;
            var edited = Capture(fixture =>
            {
                var voxels = Voxels(fixture);
                if (moles)
                    voxels.GetGasChannel(0).Moles[voxel] = value;
                else
                    voxels.Temperature[voxel] = value;
            });

            bool sameHash = baseline.ComputeStateHash() == edited.ComputeStateHash();
            var differences = baseline.Diff(edited);
            Assert.That(differences.Count == 0, Is.EqualTo(sameHash), $"Trial {trial}");
            if (sameHash)
            {
                equal++;
                continue;
            }

            different++;
            string array = moles ? Chunk + " / Gases / 0 / Moles" : Chunk + " / Temperatures";
            Assert.That(differences, Is.EqualTo(new[] { new AtmosStateDifference($"{array}[{voxel}]", differences[0].Detail) }), $"Trial {trial}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(equal, Is.GreaterThan(0));
            Assert.That(different, Is.GreaterThan(0));
        });
    }

    private static AtmosDangerousChunk Voxels(Fixture fixture) => fixture.Simulation.Dangerous().GetChunk(fixture.Chunk);

    private static FlatArray<float> Exposure(Fixture fixture) =>
        fixture.Simulation.Dangerous().GetChunkField(fixture.Chunk, StateDiffData.Exposure);

    private static AtmosWorldCheckpoint Capture(Action<Fixture>? edit = null, AtmosConfig? config = null)
    {
        using var world = new AtmosWorld(config ?? new TestAtmosConfig());
        var simulation = world.CreateSimulation(4, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 5, "TestGas0", 2f, 300f);
        var solver = new StateDiffFireSolver();
        world.Solvers.Register(SolverName, solver);
        var mixture = world.CreateGasMixture(1f);
        edit?.Invoke(new Fixture(world, simulation, chunk, solver, mixture));
        return world.CaptureCheckpoint();
    }

    private sealed record Fixture(
        AtmosWorld World, AtmosSimulation Simulation, AtmosChunkHandle Chunk, StateDiffFireSolver Solver, GasMixture Mixture);
}
