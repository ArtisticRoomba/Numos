using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.State;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Holds the detached state of a world's standalone gas mixtures: every page, cell volume, slot generation, and
///     allocation flag.
/// </summary>
/// <remarks>
///     Generated from the <see cref="NumosSerializeAttribute" /> members of <see cref="AtmosGasMixturePool" />. The
///     pool restores itself, because it has to rebuild its derived counters. Time and configuration belong to the
///     containing world checkpoint.
/// </remarks>
[NumosSnapshotOf(typeof(AtmosGasMixturePool))]
internal sealed partial class AtmosGasMixturePoolCheckpoint
{
    /// <summary>
    ///     Checks the structure and the cell invariants that the pool operations maintain.
    /// </summary>
    /// <exception cref="InvalidDataException">The data cannot describe a pool.</exception>
    internal void Validate()
    {
        const int pageSize = AtmosGasMixturePool.PageSize;
        long cells = (long)Pages.Count * pageSize;
        if (Volumes.Count != cells || Generations.Count != cells || Allocated.Count != cells)
            throw new InvalidDataException("The gas mixture pool arrays do not match its page count.");

        for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
        {
            var page = Pages[pageIndex];
            if (page.Dimensions != new Int3(pageSize, 1, 1) || page.Position != new Int3(pageIndex, 0, 0))
                throw new InvalidDataException("A gas mixture pool page has the wrong dimensions or position.");

            // Pool pages never sleep, and the restored hash must equal the checkpoint's.
            if (!page.IsAwake)
                throw new InvalidDataException("A gas mixture pool page is asleep.");

            for (int cell = 0; cell < pageSize; cell++)
            {
                int slot = pageIndex * pageSize + cell;
                float volume = Volumes[slot];
                if (Generations[slot] == 0)
                    throw new InvalidDataException("A gas mixture slot has generation zero.");

                if (Allocated[slot])
                {
                    if (!float.IsFinite(volume) || volume <= 0f ||
                        page.Classifications[cell] != VoxelClassification.RoomUnassigned)
                    {
                        throw new InvalidDataException(
                            $"Gas mixture slot {slot} is allocated with volume {volume} and classification {page.Classifications[cell]}.");
                    }
                }
                else if (volume != 0f || page.Classifications[cell] != VoxelClassification.RoomSolid)
                {
                    throw new InvalidDataException(
                        $"Gas mixture slot {slot} is free with volume {volume} and classification {page.Classifications[cell]}.");
                }
            }
        }
    }

    partial void OnDecoded()
    {
        Validate();
    }
}
