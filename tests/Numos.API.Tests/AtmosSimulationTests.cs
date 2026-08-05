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