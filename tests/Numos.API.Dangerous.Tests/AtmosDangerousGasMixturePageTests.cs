using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousGasMixturePageTests
{
    [Test]
    public void Dangerous_ExposesTheLiveVolumeAndTemperatureOfAMixture()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var other = world.CreateGasMixture(1f, 290f);
        using var canister = world.CreateGasMixture(3f, 350f);
        canister.AddGas("TestGas0", 2f, 350f);

        float volume = -1f;
        float freeVolume = -1f;
        float temperature = -1f;
        float moles = -1f;
        int cellCount = -1;
        int voxelCount = -1;
        int pageIndex = -1;
        world.Solvers.Register(
            "tests/dangerous-gas-mixture-page/read",
            context =>
            {
                var cell = canister.CellRef.LocalVoxelIndex;
                Assert.That(context.GasMixtures.TryGetPage(canister.CellRef, out var page), Is.True);
                var live = page.Dangerous();
                volume = live.Volumes[cell];
                freeVolume = live.Volumes[5];
                temperature = live.Chunk.Temperature[cell];
                moles = live.Chunk.GetGasChannel(0).Moles[cell];
                cellCount = live.Volumes.Length;
                voxelCount = live.Chunk.VoxelCount;
                pageIndex = live.Chunk.Position.X;
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(volume, Is.EqualTo(3f));
            Assert.That(freeVolume, Is.Zero);
            Assert.That(temperature, Is.EqualTo(350f));
            Assert.That(moles, Is.EqualTo(2f));
            Assert.That(cellCount, Is.EqualTo(256));
            Assert.That(voxelCount, Is.EqualTo(256));
            Assert.That(pageIndex, Is.Zero);
        });
    }

    [Test]
    public void Dangerous_WritesReachTheMixture()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var canister = world.CreateGasMixture(3f, 350f);
        world.Solvers.Register(
            "tests/dangerous-gas-mixture-page/write",
            context =>
            {
                var live = context.GasMixtures[0].Dangerous();
                live.Chunk.Temperature[canister.CellRef.LocalVoxelIndex] = 400f;
                live.Volumes[canister.CellRef.LocalVoxelIndex] = 6f;
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(canister.Temperature, Is.EqualTo(400f));
            Assert.That(canister.Volume, Is.EqualTo(6f));
        });
    }

    [Test]
    public void GetGasMixturePage_ResolvesAMixtureCellAndLeavesGetChunkGridOnly()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        using var canister = world.CreateGasMixture(3f, 350f);
        var gridCell = simulation.GetCellRef(chunk, 1);

        float volume = -1f;
        float gridTemperature = -1f;
        Exception? gridCellAsMixture = null;
        Exception? mixtureAsChunk = null;
        world.Solvers.Register(
            "tests/dangerous-gas-mixture-page/resolve",
            context =>
            {
                var api = context.Dangerous();
                volume = api.GetGasMixturePage(canister.CellRef).Volumes[canister.CellRef.LocalVoxelIndex];
                gridTemperature = api.GetChunk(gridCell).Temperature[gridCell.LocalVoxelIndex];

                // The API is a ref struct, so a lambda cannot capture it.
                try
                {
                    _ = api.GetGasMixturePage(gridCell);
                }
                catch (Exception exception)
                {
                    gridCellAsMixture = exception;
                }

                try
                {
                    _ = api.GetChunk(canister.CellRef);
                }
                catch (Exception exception)
                {
                    mixtureAsChunk = exception;
                }
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(volume, Is.EqualTo(3f));
            Assert.That(gridTemperature, Is.GreaterThanOrEqualTo(0f));
            Assert.That(gridCellAsMixture, Is.TypeOf<ArgumentException>());
            Assert.That(mixtureAsChunk, Is.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void GetGasMixturePage_RejectsAReleasedMixture()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var canister = world.CreateGasMixture(3f, 350f);
        var cell = canister.CellRef;
        canister.Dispose();

        Exception? failure = null;
        world.Solvers.Register(
            "tests/dangerous-gas-mixture-page/released",
            context => failure = Capture(() => context.Dangerous().GetGasMixturePage(cell)));

        world.Tick();

        Assert.That(failure, Is.TypeOf<ArgumentException>());
    }

    [Test]
    public void GetGasMixturePage_RejectsAReferenceToAReusedSlot()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var canister = world.CreateGasMixture(3f, 350f);
        var staleCell = canister.CellRef;
        canister.Dispose();
        using var replacement = world.CreateGasMixture(5f, 350f);

        Exception? failure = null;
        float volume = -1f;
        world.Solvers.Register(
            "tests/dangerous-gas-mixture-page/reused",
            context =>
            {
                failure = Capture(() => context.Dangerous().GetGasMixturePage(staleCell));
                volume = context.Dangerous().GetGasMixturePage(replacement.CellRef)
                    .Volumes[replacement.CellRef.LocalVoxelIndex];
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(replacement.CellRef.LocalVoxelIndex, Is.EqualTo(staleCell.LocalVoxelIndex));
            Assert.That(failure, Is.TypeOf<ArgumentException>());
            Assert.That(volume, Is.EqualTo(5f));
        });
    }

    [Test]
    public void Dangerous_OnExpiredOrDefaultPage_Throws()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var canister = world.CreateGasMixture(3f, 350f);
        AtmosSolverGasMixturePage retained = default;
        world.Solvers.Register(
            "tests/dangerous-gas-mixture-page/retained",
            context => retained = context.GasMixtures[0]);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(() => retained.Dangerous().Volumes.Length, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverGasMixturePage).Dangerous().Volumes.Length, Throws.InvalidOperationException);
        });
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
