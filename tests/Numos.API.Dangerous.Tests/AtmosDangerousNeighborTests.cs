using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousNeighborTests
{
    private const int Size = 3;

    private static (AtmosSimulation Simulation, AtmosChunkHandle Handle) CreateChunk()
    {
        var simulation = new AtmosSimulation(new TestAtmosConfig(), Size, Size, Size);
        return (simulation, simulation.CreateAndRegisterChunk(default));
    }

    private static (int Count, ushort[] Indices, AtmosVoxelNeighborKind[] Kinds) Resolve(
        AtmosSimulation simulation, AtmosChunkHandle handle, Int3 position)
    {
        var indices = new ushort[AtmosDangerousChunk.NeighborDirectionCount];
        var kinds = new AtmosVoxelNeighborKind[AtmosDangerousChunk.NeighborDirectionCount];
        int count = simulation.Dangerous().GetChunk(handle).ResolveNeighbors(position, indices, kinds);
        return (count, indices, kinds);
    }

    [Test]
    public void DirectionTables_UseTheDocumentedOrderAndPairInverses()
    {
        var directions = AtmosDangerousChunk.NeighborDirections;
        var opposites = AtmosDangerousChunk.OppositeNeighborDirections;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                directions.ToArray(),
                Is.EqualTo(new[] { Int3.NegX, Int3.PosX, Int3.NegY, Int3.PosY, Int3.NegZ, Int3.PosZ }));
            Assert.That(directions.Length, Is.EqualTo(AtmosDangerousChunk.NeighborDirectionCount));
            Assert.That(opposites.Length, Is.EqualTo(AtmosDangerousChunk.NeighborDirectionCount));
            for (int slot = 0; slot < directions.Length; slot++)
                Assert.That(directions[opposites[slot]], Is.EqualTo(-directions[slot]), $"slot {slot}");
        }
    }

    [Test]
    public void ResolveNeighbors_AtAnInteriorVoxel_FindsSixAirNeighbors()
    {
        var (simulation, handle) = CreateChunk();
        using var _ = simulation;
        var chunk = simulation.Dangerous().GetChunk(handle);
        var center = new Int3(1, 1, 1);

        var (count, indices, kinds) = Resolve(simulation, handle, center);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(6));
            Assert.That(kinds, Is.All.EqualTo(AtmosVoxelNeighborKind.Air));
            for (int slot = 0; slot < indices.Length; slot++)
            {
                var neighbor = center + AtmosDangerousChunk.NeighborDirections[slot];
                Assert.That(
                    indices[slot],
                    Is.EqualTo(chunk.GetVoxelIndex(neighbor.X, neighbor.Y, neighbor.Z)),
                    $"slot {slot}");
            }
        }
    }

    [Test]
    public void ResolveNeighbors_BesideASolidAndAVoid_ClassifiesEachDirection()
    {
        var (simulation, handle) = CreateChunk();
        using var _ = simulation;
        simulation.SetVoxelClassification(handle, 2, 1, 1, new VoxelClassification(VoxelClassification.RoomSolid));
        simulation.SetVoxelClassification(handle, 1, 2, 1, new VoxelClassification(VoxelClassification.RoomVoid));
        var chunk = simulation.Dangerous().GetChunk(handle);

        var (count, indices, kinds) = Resolve(simulation, handle, new Int3(1, 1, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(5));
            Assert.That(
                kinds,
                Is.EqualTo(new[]
                {
                    AtmosVoxelNeighborKind.Air,
                    AtmosVoxelNeighborKind.Blocked,
                    AtmosVoxelNeighborKind.Air,
                    AtmosVoxelNeighborKind.Void,
                    AtmosVoxelNeighborKind.Air,
                    AtmosVoxelNeighborKind.Air
                }));
            Assert.That(indices[3], Is.EqualTo(chunk.GetVoxelIndex(1, 2, 1)));
        }
    }

    [Test]
    public void ResolveNeighbors_AtAChunkCorner_BlocksTheDirectionsThatLeaveTheChunk()
    {
        var (simulation, handle) = CreateChunk();
        using var _ = simulation;

        var (count, _, kinds) = Resolve(simulation, handle, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(
                kinds,
                Is.EqualTo(new[]
                {
                    AtmosVoxelNeighborKind.Blocked,
                    AtmosVoxelNeighborKind.Air,
                    AtmosVoxelNeighborKind.Blocked,
                    AtmosVoxelNeighborKind.Air,
                    AtmosVoxelNeighborKind.Blocked,
                    AtmosVoxelNeighborKind.Air
                }));
        }
    }

    [Test]
    public void ResolveNeighbors_OverwritesKindsLeftFromEarlierCalls()
    {
        var (simulation, handle) = CreateChunk();
        using var _ = simulation;
        var indices = new ushort[6];
        var kinds = new AtmosVoxelNeighborKind[6];
        Array.Fill(kinds, AtmosVoxelNeighborKind.Void);

        int count = simulation.Dangerous().GetChunk(handle).ResolveNeighbors(default, indices, kinds);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(kinds[0], Is.EqualTo(AtmosVoxelNeighborKind.Blocked));
        }
    }

    [Test]
    public void ResolveNeighbors_WithAShortSpan_Throws()
    {
        var (simulation, handle) = CreateChunk();
        using var _ = simulation;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => simulation.Dangerous().GetChunk(handle).ResolveNeighbors(default, new ushort[5], new AtmosVoxelNeighborKind[6]),
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                () => simulation.Dangerous().GetChunk(handle).ResolveNeighbors(default, new ushort[6], new AtmosVoxelNeighborKind[5]),
                Throws.TypeOf<ArgumentException>());
        }
    }

    [Test]
    public void IsBoundary_IsTrueOnlyOnChunkFaces()
    {
        var (simulation, handle) = CreateChunk();
        using var _ = simulation;
        var chunk = simulation.Dangerous().GetChunk(handle);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk.IsBoundary(new Int3(1, 1, 1)), Is.False);
            Assert.That(chunk.IsBoundary(new Int3(0, 1, 1)), Is.True);
            Assert.That(chunk.IsBoundary(new Int3(1, Size - 1, 1)), Is.True);
            Assert.That(chunk.IsBoundary(new Int3(1, 1, Size - 1)), Is.True);
            Assert.That(chunk.IsBoundary(default), Is.True);
        }
    }

    [Test]
    public void IsBoundary_InAOneVoxelChunk_IsAlwaysTrue()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);

        Assert.That(simulation.Dangerous().GetChunk(handle).IsBoundary(default), Is.True);
    }
}
