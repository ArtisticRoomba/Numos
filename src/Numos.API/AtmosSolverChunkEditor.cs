using JetBrains.Annotations;
using Numos.CoreSim;

namespace Numos.API;

/// <summary>
///     Writable views over the gas, temperature, and momentum of one chunk during a world solver stage.
/// </summary>
/// <remarks>
///     <para>
///         Get one from <see cref="AtmosSolverChunk.Edit" />. That call marks the chunk, and Numos repairs it after
///         the stage returns: it wakes the chunk, rebuilds pressure and heat capacity, empties voxels without gas, and
///         advances the revision. <see cref="AtmosSolverChunk.Edit" /> lists every step.
///     </para>
///     <para>
///         The editor is a <see langword="ref" /> struct, so it cannot outlive the callback that made it. It takes no
///         lock. Writes are not recorded and must be deterministic, because replay re-runs the stage.
///     </para>
/// </remarks>
[PublicAPI]
public readonly ref struct AtmosSolverChunkEditor
{
    private readonly AtmosChunk? _chunk;
    private readonly int _gasCount;

    internal AtmosSolverChunkEditor(AtmosChunk chunk, int gasCount)
    {
        _chunk = chunk;
        _gasCount = gasCount;
    }

    /// <summary>
    ///     Gets the number of voxels, which is also the length of every span on the editor.
    /// </summary>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public int VoxelCount => Chunk.VoxelCount;

    /// <summary>
    ///     Gets the temperature of each voxel, in kelvins, for writing.
    /// </summary>
    /// <remarks>
    ///     The repair recomputes pressure from the new temperature. Heat capacity does not depend on temperature.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> Temperature => Chunk.Temperature.AsSpan();

    /// <summary>
    ///     Gets the total x-momentum of the gas in each voxel, in kilogram metres per second, for writing.
    /// </summary>
    /// <remarks>
    ///     The value is the momentum of the voxel's whole gas content, not a velocity. The repair zeroes it in every
    ///     voxel that ends the stage without gas.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> MomentumX => Chunk.MomentumX.AsSpan();

    /// <summary>
    ///     Gets the total y-momentum of the gas in each voxel, for writing. Same contract as <see cref="MomentumX" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> MomentumY => Chunk.MomentumY.AsSpan();

    /// <summary>
    ///     Gets the total z-momentum of the gas in each voxel, for writing. Same contract as <see cref="MomentumX" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> MomentumZ => Chunk.MomentumZ.AsSpan();

    private AtmosChunk Chunk => _chunk ?? throw SolverGasChannels.DefaultEditorException();

    /// <summary>
    ///     Gets the moles of one gas in each voxel for writing, and creates the gas channel when the chunk has none.
    /// </summary>
    /// <param name="gasId">The registered gas ID.</param>
    /// <returns>A live span with one entry per voxel.</returns>
    /// <remarks>
    ///     <para>
    ///         A new channel starts at zero in every voxel and joins the end of the chunk's channel list, as it does
    ///         when <see cref="AtmosSimulation.AddGasToVoxel(AtmosChunkHandle, ushort, int, float, float)" /> first adds
    ///         the gas. The channel stays after the stage, so it changes the state hash even if every value stays zero.
    ///         To only read a gas, use <see cref="AtmosSolverChunk.Moles" />, which never creates a channel.
    ///     </para>
    ///     <para>
    ///         The lookup scans the chunk's channels. Call this once per chunk and gas, outside the voxel loop. The
    ///         repair treats a voxel whose moles sum to zero or less as empty. Keep each value finite and
    ///         nonnegative.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gasId" /> is not a registered gas.</exception>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> Moles(int gasId)
    {
        return SolverGasChannels.Edit(Chunk, gasId, _gasCount);
    }
}

/// <summary>
///     Writable views over the gas and temperature of one gas mixture page during a world solver stage.
/// </summary>
/// <remarks>
///     <para>
///         Get one from <see cref="AtmosSolverGasMixturePage.Edit" />. After the stage returns, Numos gives every
///         allocated cell of a marked page the pressure, heat capacity, and vacuum flag that its gas, temperature, and
///         volume imply. Gas written into a free cell is cleared. The page revision advances.
///     </para>
///     <para>
///         The rules of <see cref="AtmosSolverChunkEditor" /> apply. A mixture has no momentum, so the editor has no
///         momentum spans.
///     </para>
/// </remarks>
[PublicAPI]
public readonly ref struct AtmosSolverGasMixturePageEditor
{
    private readonly AtmosChunk? _page;
    private readonly int _gasCount;

    internal AtmosSolverGasMixturePageEditor(AtmosChunk page, int gasCount)
    {
        _page = page;
        _gasCount = gasCount;
    }

    /// <summary>
    ///     Gets the number of cells, which is also the length of every span on the editor.
    /// </summary>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public int CellCount => Page.VoxelCount;

    /// <summary>
    ///     Gets the temperature of each cell, in kelvins, for writing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> Temperature => Page.Temperature.AsSpan();

    private AtmosChunk Page => _page ?? throw SolverGasChannels.DefaultEditorException();

    /// <summary>
    ///     Gets the moles of one gas in each cell for writing, and creates the gas channel when the page has none.
    /// </summary>
    /// <param name="gasId">The registered gas ID.</param>
    /// <returns>A live span with one entry per cell.</returns>
    /// <remarks>
    ///     A new channel changes the state hash as <see cref="AtmosSolverChunkEditor.Moles" /> describes. Check
    ///     <see cref="AtmosSolverGasMixturePage.IsAllocated" /> before you write a cell. The lookup scans the page's
    ///     channels, so call this outside the cell loop.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gasId" /> is not a registered gas.</exception>
    /// <exception cref="InvalidOperationException">The editor is default.</exception>
    public Span<float> Moles(int gasId)
    {
        return SolverGasChannels.Edit(Page, gasId, _gasCount);
    }
}

/// <summary>
///     Gas channel lookups shared by the solver views and editors.
/// </summary>
internal static class SolverGasChannels
{
    internal static ReadOnlySpan<float> Read(AtmosChunk chunk, int gasId)
    {
        int channel = chunk.FindGasChannel(gasId);
        return channel < 0 ? ReadOnlySpan<float>.Empty : chunk.ActiveGases[channel].Moles.AsSpan(0, chunk.VoxelCount);
    }

    internal static Span<float> Edit(AtmosChunk chunk, int gasId, int gasCount)
    {
        if ((uint)gasId >= (uint)gasCount)
            throw new ArgumentOutOfRangeException(nameof(gasId), gasId, "Gas ID must refer to a registered gas.");

        int channel = chunk.GetOrCreateGasChannel(gasId);
        return chunk.ActiveGases[channel].Moles.AsSpan(0, chunk.VoxelCount);
    }

    internal static InvalidOperationException DefaultEditorException()
    {
        return new InvalidOperationException(
            "This solver editor is uninitialized. Obtain editors from AtmosSolverChunk.Edit or AtmosSolverGasMixturePage.Edit.");
    }
}
