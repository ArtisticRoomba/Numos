using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSimulationTests
{
    [Test]
    public void AddGasToVoxel_ResolvesNamesUsingCurrentRegistrationOrder()
    {
        var config = new AtmosConfig
        {
            GasRegistry = [TestGases.Create("Second"), TestGases.Create("First")]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));

        simulation.AddGasToVoxel(chunk, 0, "First", 2f, 300f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, "Second", 3f, 300f);

        var mixture = simulation.GetVoxelGasMixture(chunk, 0);
        Assert.Multiple(() =>
        {
            Assert.That(mixture.GetMoles("First"), Is.EqualTo(2f));
            Assert.That(mixture.GetMoles("Second"), Is.EqualTo(3f));
            Assert.That(mixture.GetSnapshot().GetMoles(1), Is.EqualTo(2f));
        });
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(7)]
    [TestCase(int.MaxValue)]
    public void AddGasToVoxel_RejectsUnregisteredIdsWithoutMutation(int gasId)
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        var before = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, gasId, 1f, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>());

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 0, 0, gasId, 1f, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>());

            Assert.That(simulation.GetChunkSnapshot(chunk).Gases, Is.Empty);
            Assert.That(simulation.GetChunkSnapshot(chunk).Temperature, Is.EqualTo(before.Temperature));
        });
    }

    [TestCase("missing")]
    [TestCase("testgas0")]
    public void AddGasToVoxel_RejectsUnknownNames(string gasName)
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, gasName, 1f, 300f),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 0, 0, gasName, 1f, 300f),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(simulation.GetChunkSnapshot(chunk).Gases, Is.Empty);
        });
    }

    [Test]
    public void Facade_CreatesAndMutatesChunkWithoutExposingKernelState()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 3, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(4, 5, 6));

        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 1, 0, 0, "TestGas1", 2f, 300f);

        var snapshot = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.ChunkCount, Is.EqualTo(1));
            Assert.That(snapshot.GridPosition, Is.EqualTo(chunk.Position));
            Assert.That(
                snapshot.VoxelRoomMap,
                Is.EqualTo(new[] { VoxelClassification.RoomSolid, 7, VoxelClassification.RoomSolid }));

            Assert.That(snapshot.Temperature[1], Is.EqualTo(300f));
            Assert.That(snapshot.Gases, Has.Length.EqualTo(1));
            Assert.That(snapshot.Gases[0].Moles[1], Is.EqualTo(2f));
        });
    }

    [Test]
    public void SetChunkBoundaryClassification_SealsOuterFacesAndPreservesInterior()
    {
        using var simulation = new AtmosSimulation(4, 4, 3);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));

        simulation.SetChunkBoundaryClassification(chunk, VoxelClassification.RoomSolid);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        var dimensions = snapshot.Dimensions;
        for (int z = 0; z < dimensions.Z; z++)
        for (int y = 0; y < dimensions.Y; y++)
        for (int x = 0; x < dimensions.X; x++)
        {
            int index = x + dimensions.X * (y + dimensions.Y * z);
            bool isBoundary = x == 0 ||
                              x == dimensions.X - 1 ||
                              y == 0 ||
                              y == dimensions.Y - 1 ||
                              z == 0 ||
                              z == dimensions.Z - 1;

            Assert.That(
                snapshot.VoxelRoomMap[index],
                Is.EqualTo(isBoundary ? VoxelClassification.RoomSolid : 7));
        }
    }

    [Test]
    public void SetChunkBoundaryClassification_SingleLayerChunkSealsPerimeterOnly()
    {
        using var simulation = new AtmosSimulation(3, 3, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));

        simulation.SetChunkBoundaryClassification(chunk, VoxelClassification.RoomSolid);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(
            snapshot.VoxelRoomMap,
            Is.EqualTo(
                new[]
                {
                    -2, -2, -2,
                    -2, 7, -2,
                    -2, -2, -2
                }));
    }

    [Test]
    public void SealedSingleLayerChunk_DoesNotLoseGasThroughZPlane()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 16, 16, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.SetChunkBoundaryClassification(chunk, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(chunk, 8, 8, 0, "TestGas0", 500f, 293.15f);

        for (int tick = 0; tick < 20; tick++)
            simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        float totalMoles = snapshot.Gases.Single().Moles.Sum();
        Assert.That(totalMoles, Is.GreaterThan(499.5f));
    }

    [Test]
    public void CoreSim_HidesMutableKernelTypes()
    {
        var configType = typeof(AtmosConfig);
        var kernelType = configType.Assembly.GetType("Numos.CoreSim.AtmosKernel");
        var chunkType = configType.Assembly.GetType("Numos.CoreSim.AtmosChunk");

        Assert.Multiple(() =>
        {
            Assert.That(kernelType, Is.Not.Null);
            Assert.That(kernelType!.IsNotPublic, Is.True);
            Assert.That(chunkType, Is.Not.Null);
            Assert.That(chunkType!.IsNotPublic, Is.True);
        });
    }

    [Test]
    public void DuplicatePosition_IsRejectedWithoutReplacingTheOwnedChunk()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var position = new Int3(0, 0, 0);
        simulation.CreateAndRegisterChunk(position);

        Assert.That(() => simulation.CreateAndRegisterChunk(position), Throws.InvalidOperationException);
        Assert.That(simulation.ChunkCount, Is.EqualTo(1));
    }

    [Test]
    public void Tick_DelegatesToKernelUsingTheFacadeConfiguration()
    {
        var config = new TestAtmosConfig
        {
            VacuumThreshold = 0f,
            SleepThreshold = int.MaxValue
        };

        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, "TestGas1", 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(simulation.TickCount, Is.EqualTo(1));
            Assert.That(snapshot.Gases[0].Moles[0], Is.LessThan(2f));
            Assert.That(snapshot.Gases[0].Moles[1], Is.GreaterThan(0f));
        });
    }

    [Test]
    public void DisposedFacade_RejectsFurtherUse()
    {
        var simulation = new AtmosSimulation();
        simulation.Dispose();

        Assert.That(simulation.Tick, Throws.TypeOf<ObjectDisposedException>());
    }
}