using JetBrains.Annotations;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    private readonly object _mixtureGate = new();

    /// <summary>
    ///     Gets the gate that serializes this simulation's lifecycle and mixture operations.
    /// </summary>
    /// <remarks>
    ///     Mixture operations take it after the world's mixture gate, and <see cref="Dispose" /> takes it after
    ///     <see cref="AtmosWorld.Gate" />. The world's private mixture gate documents the full order.
    /// </remarks>
    internal object MixtureGate => _mixtureGate;

    /// <summary>
    ///     Creates sandboxed live access to one voxel's gas mixture.
    /// </summary>
    /// <param name="chunk">A chunk registered with this simulation.</param>
    /// <param name="localVoxelIndex">The voxel's index within the chunk.</param>
    /// <returns>A live mixture whose reads and writes go straight to the voxel's stored state.</returns>
    /// <remarks>
    ///     The returned capability never exposes solver arrays. It is bound to the current chunk generation and goes
    ///     stale when that chunk is removed or replaced at the same grid position.
    ///     Numos records mutations through it like any other external voxel edit. Solid and void voxels can be read
    ///     but reject mutation.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="chunk" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="localVoxelIndex" /> is not less than the chunk's voxel count.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public IGasMixture GetVoxelGasMixture(AtmosChunkHandle chunk, ushort localVoxelIndex)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            var identity = _kernel.GetVoxelMixtureIdentity(chunk.Position, localVoxelIndex);
            return new VoxelGasMixture(this, chunk.Position, identity.Generation, identity.LocalVoxelIndex);
        }
    }

    /// <summary>
    ///     Creates sandboxed live access to one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="chunk">A chunk registered with this simulation.</param>
    /// <param name="x">The voxel's local X coordinate, in [0, width).</param>
    /// <param name="y">The voxel's local Y coordinate, in [0, height).</param>
    /// <param name="z">The voxel's local Z coordinate, in [0, depth).</param>
    /// <returns>A live mixture whose reads and writes go straight to the voxel's stored state.</returns>
    /// <remarks>
    ///     Behaves like <see cref="GetVoxelGasMixture(AtmosChunkHandle, ushort)" /> with the coordinates converted to
    ///     a local voxel index.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="chunk" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside the chunk's dimensions.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public IGasMixture GetVoxelGasMixture(AtmosChunkHandle chunk, int x, int y, int z)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            var identity = _kernel.GetVoxelMixtureIdentity(chunk.Position, x, y, z);
            return new VoxelGasMixture(this, chunk.Position, identity.Generation, identity.LocalVoxelIndex);
        }
    }

    internal int ResolveGasName(string gasName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(gasName);
        return Config.GasRegistry.GasIdToIndex(gasName);
    }
}
