using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

public class FaceGasChannelsTests
{
    [Test]
    public void Maps_FollowGasIdsAcrossDifferentChannelOrders()
    {
        var chunk = new AtmosChunk(2, 2, 2);
        chunk.GetOrCreateGasChannel(1);
        chunk.GetOrCreateGasChannel(2);
        chunk.GetOrCreateGasChannel(3);
        var neighbor = new AtmosChunk(2, 2, 2);
        neighbor.GetOrCreateGasChannel(3);
        neighbor.GetOrCreateGasChannel(1);
        neighbor.GetOrCreateGasChannel(4);
        neighbor.GetOrCreateGasChannel(5);

        int stride = Math.Max(chunk.ActiveGasCount, neighbor.ActiveGasCount);
        Span<int> scratch = stackalloc int[FaceGasChannels.GetScratchLength(stride)];
        var channels = new FaceGasChannels(chunk, stride, scratch);
        int[] ownToNeighbor = channels.OwnToNeighbor(3, neighbor).ToArray();
        int[] neighborToOwn = channels.NeighborToOwn(3, neighbor).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ownToNeighbor, Is.EqualTo(new[] { 1, -1, 0 }));
            Assert.That(neighborToOwn, Is.EqualTo(new[] { 2, 0, -1, -1 }));
        });
    }

    [Test]
    public void Maps_KeepEveryDirectionApart()
    {
        var chunk = new AtmosChunk(2, 2, 2);
        chunk.GetOrCreateGasChannel(1);
        chunk.GetOrCreateGasChannel(2);
        var east = new AtmosChunk(2, 2, 2);
        east.GetOrCreateGasChannel(2);
        var west = new AtmosChunk(2, 2, 2);
        west.GetOrCreateGasChannel(1);
        west.GetOrCreateGasChannel(2);

        Span<int> scratch = stackalloc int[FaceGasChannels.GetScratchLength(2)];
        var channels = new FaceGasChannels(chunk, 2, scratch);
        int[] eastOut = channels.OwnToNeighbor(1, east).ToArray();
        int[] westOut = channels.OwnToNeighbor(0, west).ToArray();
        int[] eastOutAgain = channels.OwnToNeighbor(1, east).ToArray();
        int[] eastIn = channels.NeighborToOwn(1, east).ToArray();
        int[] westIn = channels.NeighborToOwn(0, west).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(eastOut, Is.EqualTo(new[] { -1, 0 }));
            Assert.That(westOut, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(eastOutAgain, Is.EqualTo(eastOut));
            Assert.That(eastIn, Is.EqualTo(new[] { 1 }));
            Assert.That(westIn, Is.EqualTo(new[] { 0, 1 }));
        });
    }
}
