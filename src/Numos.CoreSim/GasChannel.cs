using System.Buffers;
using Numos.Units;

namespace Numos.CoreSim;

/// <summary>
///     Represents a single gas type within an <see cref="AtmosChunk" /> using a Structure of Arrays (SoA) layout.
/// </summary>
/// <remarks>
/// <para>
///     If you've worked with old SS14 Atmospherics/SSAir, you are probably familiar with GasMixture and its
///     fixed-size Moles array. Old Atmos stored gases as Tiles dictionary -> TileAtmosphere -> GasMixture -> Moles,
///     which wasn't cache-friendly and made iterating over all gases in an area slow because of all the object
///     lookups.
/// </para>
/// <para>
///     A channel instead keeps one gas's amount for every voxel of a chunk in a single contiguous array, so memory
///     access is predictable. Looking at adjacent tiles is, after all, a fairly common op in Atmos.
/// </para>
/// </remarks>
internal struct GasChannel
{
    /// <summary>
    ///     The ID of the gas type this channel represents.
    /// </summary>
    public int GasId;

    /// <summary>
    ///     The amount of this gas in each voxel of the chunk, in moles (mol).
    /// </summary>
    /// <remarks>
    ///     While this is not marked as nullable, this field
    ///     can be null when the channel is not initialized via <see cref="Initialize" />. The array is rented from
    ///     <see cref="ArrayPool{T}.Shared" />, so it can be longer than the chunk's voxel count; only the first
    ///     voxel-count entries are meaningful.
    /// </remarks>
    [ElementQuantity("amount")]
    public Mole[] Moles;

    /// <summary>
    ///     Whether the <see cref="GasChannel" /> has been initialized and is ready for use.
    /// </summary>
    public bool IsInitialized => Moles != null;

    /// <summary>
    ///     Initializes the <see cref="GasChannel" /> with the specified gas ID and voxel count.
    /// </summary>
    /// <param name="gasId">The ID of the gas type this channel represents.</param>
    /// <param name="voxelCount">The number of voxels in the chunk.</param>
    /// TODO figure out if C#'s static null analysis can help with marking
    /// the above fields null as right now we have to track if they're null ourselves
    /// using init, it would be nice if we didn't have to worry about it past init.
    public void Initialize(int gasId, int voxelCount)
    {
        GasId = gasId;
        Moles = ArrayPool<Mole>.Shared.Rent(voxelCount);
        Array.Clear(Moles, 0, voxelCount);
    }

    /// <summary>
    ///     Releases the resources used by the <see cref="GasChannel" />, returning the moles array to the shared array pool.
    /// </summary>
    public void Release()
    {
        if (IsInitialized)
        {
            ArrayPool<Mole>.Shared.Return(Moles);
            Moles = null!;
        }
    }
}