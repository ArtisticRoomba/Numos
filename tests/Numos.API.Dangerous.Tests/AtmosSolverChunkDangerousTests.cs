using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosSolverChunkDangerousTests
{
    private static readonly ChunkField<float> Exposure =
        ChunkField<float>.Register("tests/dangerous-solver-chunk/exposure");

    [Test]
    public void Dangerous_ViewsTheSameChunkAsTheSolverView()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 2);
        var handle = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(handle, 3, "TestGas0", 1f, 450f);
        int temperatureLength = -1;
        Int3 position = new(int.MinValue, int.MinValue, int.MinValue);

        world.Solvers.Register(
            "tests/dangerous-solver-chunk/shared",
            context =>
            {
                var chunks = context.Chunks(simulation);
                Parallel.For(
                    0,
                    chunks.Count,
                    index =>
                    {
                        // Custom fields stay on the safe view; the dangerous one adds the built-in storage.
                        var chunk = chunks[index];
                        var live = chunk.Dangerous();
                        chunk.Get(Exposure)[3] = live.Temperature[3];
                        temperatureLength = live.Temperature.Length;
                        position = live.Position;
                    });
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(temperatureLength, Is.EqualTo(8));
            Assert.That(position, Is.EqualTo(handle.Position));
            Assert.That(
                simulation.GetChunkField(handle, Exposure)[3],
                Is.EqualTo(simulation.GetChunkSnapshot(handle).Temperature[3]).And.GreaterThan(0f));
        });
    }

    [Test]
    public void Dangerous_OnExpiredOrDefaultView_Throws()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        AtmosSolverChunk retained = default;

        world.Solvers.Register("tests/dangerous-solver-chunk/retained", context => retained = context.Chunks(simulation)[0]);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(() => retained.Dangerous().VoxelCount, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverChunk).Dangerous().VoxelCount, Throws.InvalidOperationException);
        });
    }
}