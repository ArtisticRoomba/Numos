using System.Buffers;

namespace Numos.CoreSim;

/// <summary>
///     Represents a single gas type within an <see cref="AtmosChunk" /> using a Structure of Arrays (SoA) layout.
/// </summary>
/// <para>
///     If you've worked with old SS14 Atmospherics/SSAir, you are probably familiar with the concept of a GasMixture
///     and the underlying fixed-size array Moles.
///     In old Atmos, gases were stored in the structure of Tiles dictionary -> TileAtmosphere -> GasMixture -> Moles.
///     This wasn't cache-friendly and made iterating over all gases in a given area slow, since you had to do
///     a lot of object lookups.
/// </para>
/// <para>
///     An SoA layout is a way of organizing data in memory such that all the values
///     of a single field are stored contiguously.
///     This can improve cache performance and make it easier to perform operations on large datasets
///     (since memory access patterns are more predictable).
///     This is that implementation.
///     Looking at adjacent tiles is, after all, a fairly common op in Atmos.
/// </para>
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
    ///     can be null when the channel is not initialized via <see cref="Initialize" />.
    /// </remarks>
    public float[] Moles;

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
        Moles = ArrayPool<float>.Shared.Rent(voxelCount);
        Array.Clear(Moles, 0, voxelCount);
    }

    /// <summary>
    ///     Releases the resources used by the <see cref="GasChannel" />, returning the moles array to the shared array pool.
    /// </summary>
    public void Release()
    {
        if (IsInitialized)
        {
            ArrayPool<float>.Shared.Return(Moles);
            Moles = null!;
        }
    }
}
