using Numos.API.Dangerous;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class NeighborKindMappingTests
{
    /// <summary>
    ///     <c>AtmosDangerousChunk.ResolveNeighbors</c> casts its public kind span to the internal kind span without
    ///     a copy, so every pair of members must share a value, and the types must share a size.
    /// </summary>
    [Test]
    public void AtmosVoxelNeighborKind_MatchesTheInternalNeighborKind()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Enum.GetNames<AtmosVoxelNeighborKind>(),
                Is.EquivalentTo(Enum.GetNames<NeighborKind>()));

            foreach (var name in Enum.GetNames<NeighborKind>())
            {
                Assert.That(
                    (byte)Enum.Parse<AtmosVoxelNeighborKind>(name),
                    Is.EqualTo((byte)Enum.Parse<NeighborKind>(name)),
                    name);
            }

            Assert.That(Enum.GetUnderlyingType(typeof(AtmosVoxelNeighborKind)), Is.EqualTo(typeof(byte)));
            Assert.That(Enum.GetUnderlyingType(typeof(NeighborKind)), Is.EqualTo(typeof(byte)));
        });
    }
}
