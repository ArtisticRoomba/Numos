using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    /// <summary>
    ///     Edits every voxel of a box in global voxel coordinates and records one replay operation.
    /// </summary>
    /// <param name="min">The inclusive minimum corner, in global voxel coordinates.</param>
    /// <param name="max">The inclusive maximum corner, in global voxel coordinates.</param>
    /// <param name="edit">The changes to apply. See <see cref="AtmosVoxelBoxEdit" /> for the order.</param>
    /// <returns>Counts of edited and skipped voxels.</returns>
    /// <remarks>
    ///     <para>
    ///         A global voxel coordinate is the chunk grid position times the chunk dimensions, plus the local
    ///         coordinate. The box can span many chunks. Voxels in chunks that do not exist are skipped and counted in
    ///         <see cref="AtmosVoxelBoxEditResult.VoxelsSkipped" />. The edit never creates a chunk.
    ///     </para>
    ///     <para>
    ///         The result equals the same edit made with one per-voxel call for each part and voxel, except for the
    ///         cost. The edit takes the kernel lock once per box and records one operation, where the per-voxel calls
    ///         record one operation each. Gas removal skips solid and void voxels, where the per-voxel removal call
    ///         throws.
    ///     </para>
    ///     <para>
    ///         Numos validates the box and the edit before it changes any state. An invalid edit throws and leaves the
    ///         simulation and the recording as they were.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="min" /> exceeds <paramref name="max" /> on an axis, a gas ID is not registered, an amount
    ///     is not positive and finite, a temperature is not finite or is negative, or a velocity component is not
    ///     finite.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public AtmosVoxelBoxEditResult EditVoxelBox(Int3 min, Int3 max, in AtmosVoxelBoxEdit edit)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            var parts = AtmosVoxelBoxParts.None;
            VoxelClassification classification = default;
            if (edit.Classification is { } assigned)
            {
                parts |= AtmosVoxelBoxParts.Classification;
                classification = assigned;
            }

            if (edit.ClearGas)
                parts |= AtmosVoxelBoxParts.ClearGas;

            float voxelTemperature = 0f;
            if (edit.TemperatureK is { } temperature)
            {
                parts |= AtmosVoxelBoxParts.Temperature;
                voxelTemperature = temperature;
            }

            var gases = edit.Gases is { Count: > 0 } list ? Array.AsReadOnly(list.ToArray()) : [];
            float gasTemperature = 0f;
            if (gases.Count > 0)
            {
                gasTemperature = edit.GasTemperatureK ??
                                 edit.TemperatureK ??
                                 _kernel.GetAtmosConfig().DefaultTemperatureFallback;
            }

            float velocityX = 0f;
            float velocityY = 0f;
            float velocityZ = 0f;
            if (edit.Velocity is { } velocity)
            {
                parts |= AtmosVoxelBoxParts.Velocity;
                velocityX = velocity.X;
                velocityY = velocity.Y;
                velocityZ = velocity.Z;
            }

            var counts = _kernel.EditVoxelBox(new EditVoxelBoxOperation(
                min,
                max,
                parts,
                classification,
                voxelTemperature,
                gasTemperature,
                velocityX,
                velocityY,
                velocityZ,
                gases));

            return new AtmosVoxelBoxEditResult(
                counts.VoxelsEdited,
                counts.VoxelsSkipped,
                counts.ChunksTouched,
                counts.VelocityVoxels);
        }
    }
}
