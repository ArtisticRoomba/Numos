using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

namespace Numos.CoreSim.Tests.Euler;

public class EulerFaceTopologyTests
{
    private const int NegX = 0;
    private const int PosX = 1;
    private const int NegY = 2;
    private const int PosY = 3;
    private const int NegZ = 4;
    private const int PosZ = 5;

    [Test]
    public void Masks_ClassifySolidVoidAndChunkEdgeFaces()
    {
        var chunk = new AtmosChunk(3, 3, 3);
        chunk.SetChunkClassification(1);
        ushort solid = chunk.GetIndex(0, 1, 1);
        ushort sink = chunk.GetIndex(2, 1, 1);
        chunk.SetVoxelClassification(solid, VoxelClassification.RoomSolid);
        chunk.SetVoxelClassification(sink, VoxelClassification.RoomVoid);
        chunk.Wake();

        byte[] open = new byte[chunk.VoxelCount];
        byte[] voids = new byte[chunk.VoxelCount];
        EulerFaceTopology.Build(chunk, open, voids);

        ushort centre = chunk.GetIndex(1, 1, 1);
        ushort corner = chunk.GetIndex(0, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(Bits(open[centre]), Is.EqualTo(new[] { NegY, PosY, NegZ, PosZ }), "centre open faces");
            Assert.That(Bits(voids[centre]), Is.EqualTo(new[] { PosX }), "centre void faces");

            Assert.That(Bits(open[corner]), Is.EqualTo(new[] { PosX, PosY, PosZ }), "corner open faces");
            Assert.That(voids[corner], Is.Zero, "corner void faces");

            Assert.That(open[solid], Is.Zero);
            Assert.That(voids[solid], Is.Zero);
            Assert.That(open[sink], Is.Zero);
            Assert.That(voids[sink], Is.Zero);

            // The air voxel below the solid one sees a wall above it, not an open or void face.
            ushort belowSolid = chunk.GetIndex(0, 0, 1);
            Assert.That(EulerFaceTopology.IsOpen(open[belowSolid], PosY), Is.False);
            Assert.That(EulerFaceTopology.IsVoid(voids[belowSolid], PosY), Is.False);
            Assert.That(EulerFaceTopology.IsOpen(open[belowSolid], PosX), Is.True);
        });
    }

    [Test]
    public void Masks_AgreeAcrossEverySharedFace()
    {
        var chunk = new AtmosChunk(4, 3, 2);
        chunk.SetChunkClassification(1);
        chunk.SetVoxelClassification(chunk.GetIndex(1, 1, 0), VoxelClassification.RoomSolid);
        chunk.SetVoxelClassification(chunk.GetIndex(2, 2, 1), VoxelClassification.RoomVoid);
        chunk.Wake();

        byte[] open = new byte[chunk.VoxelCount];
        byte[] voids = new byte[chunk.VoxelCount];
        EulerFaceTopology.Build(chunk, open, voids);

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Int3 position = chunk.GetXyzInt3(voxelIndex);
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (!EulerFaceTopology.IsOpen(open[voxelIndex], direction))
                    continue;

                ushort neighbor = chunk.GetIndexUnsafe(position + NeighborResolution.NeighborDirections[direction]);
                Assert.That(
                    EulerFaceTopology.IsOpen(open[neighbor], direction ^ 1), Is.True,
                    $"voxel {voxelIndex} sees {neighbor} open in slot {direction}, but not the reverse");
            }
        }
    }

    private static int[] Bits(byte mask)
    {
        return Enumerable.Range(0, 6).Where(bit => (mask & (1 << bit)) != 0).ToArray();
    }
}
