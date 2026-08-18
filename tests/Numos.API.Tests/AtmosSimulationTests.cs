using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSimulationTests
{
    [Test]
    public void Facade_CreatesAndMutatesChunkWithoutExposingKernelState()
    {
        using var simulation = new AtmosSimulation(3, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(4, 5, 6));

        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 1, 0, 0, 1, 2f, 300f);

        var snapshot = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.ChunkCount, Is.EqualTo(1));
            Assert.That(snapshot.GridPosition, Is.EqualTo(chunk.Position));
            Assert.That(snapshot.VoxelRoomMap,
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
        for (var z = 0; z < dimensions.Z; z++)
        for (var y = 0; y < dimensions.Y; y++)
        for (var x = 0; x < dimensions.X; x++)
        {
            int index = x + dimensions.X * (y + dimensions.Y * z);
            bool isBoundary = x == 0 || x == dimensions.X - 1 ||
                              y == 0 || y == dimensions.Y - 1 ||
                              z == 0 || z == dimensions.Z - 1;
            Assert.That(snapshot.VoxelRoomMap[index],
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
        Assert.That(snapshot.VoxelRoomMap,
            Is.EqualTo(new[]
            {
                -2, -2, -2,
                -2, 7, -2,
                -2, -2, -2
            }));
    }

    [Test]
    public void SealedSingleLayerChunk_DoesNotLoseGasThroughZPlane()
    {
        using var simulation = new AtmosSimulation(16, 16, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.SetChunkBoundaryClassification(chunk, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(chunk, 8, 8, 0, 0, 500f, 293.15f);

        for (var tick = 0; tick < 20; tick++)
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
        var config = new AtmosConfig
        {
            VacuumThreshold = 0f,
            MinFlowCutoff = 0f,
            SleepThreshold = int.MaxValue
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 1, 2f, 300f);

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