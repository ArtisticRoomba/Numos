using System.Runtime.CompilerServices;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Finds awake chunks whose gas is perfectly uniform and at rest, so the <c>euler-flow</c> stage can leave them
///     without a workspace when nothing near them can change them this tick.
/// </summary>
/// <remarks>
///     <para>
///         A chunk is static when every air voxel has zero momentum and bit-identical pressure, temperature, and
///         moles of every gas. It must hold no trace cells and have no face into void. Each open face into a
///         registered neighbour chunk, asleep or awake, must meet a cell with the same bits.
///         No pressure threshold takes part. A sweep over such a chunk gives every face the flux of a resting gas,
///         so the chunk keeps its bits.
///     </para>
///     <para>
///         Static alone does not allow a skip. A split first-order sweep moves information one cell per axis sweep,
///         so a source can reach a chunk only if it lies within the substep count of cells. The stage checks that
///         distance after the pre-pass (see <see cref="EulerFlowSolver" />).
///     </para>
///     <para>
///         The test reads live chunk fields only and writes nothing, so chunks run in parallel and the result does
///         not depend on the worker count.
///     </para>
/// </remarks>
internal static class EulerStaticChunk
{
    /// <summary>
    ///     Whether the chunk is uniform, at rest, and bit-identical across every open chunk face.
    /// </summary>
    /// <param name="chunk">An awake chunk with at least one gas channel.</param>
    /// <param name="context">The tick context, used to find neighbour chunks.</param>
    /// <param name="signalSpeed">The signal speed a gather would give every cell, valid when the result is true.</param>
    /// <returns><see langword="true" /> when a sweep over the chunk would change no bit of it.</returns>
    /// <remarks>
    ///     One pass with an early exit on the first failure and no allocation. The first air voxel is the reference,
    ///     and every other cell is compared with it bit for bit.
    /// </remarks>
    internal static bool IsStatic(
        AtmosChunk chunk, AtmosSolverExecutionContext context, out MetrePerSecond signalSpeed)
    {
        signalSpeed = 0f;
        if (chunk.ActiveAirCount == 0)
            return false;

        ushort reference = chunk.ActiveAirIndices[0];
        Pascal pressure = chunk.TotalPressure[reference];
        Kelvin temperature = chunk.Temperature[reference];
        if (!float.IsFinite(pressure) || !float.IsFinite(temperature))
            return false;

        if (!EulerChunkWorkspace.TryGetRestSignalSpeed(chunk, reference, context.TickConfig, out signalSpeed))
            return false;

        int pressureBits = BitConverter.SingleToInt32Bits(pressure);
        int temperatureBits = BitConverter.SingleToInt32Bits(temperature);
        var neighbors = new NeighborRing();
        for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
        {
            if (context.TryGetChunk(chunk.GridPosition + NeighborResolution.NeighborDirections[direction], out AtmosChunk neighbor))
                neighbors[direction] = neighbor;
        }

        Int3 last = chunk.Dimensions - new Int3(1, 1, 1);
        int rowStride = chunk.Width;
        int slabStride = chunk.Width * chunk.Height;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (chunk.HasMomentum(voxelIndex))
                return false;

            if (!SameState(chunk, voxelIndex, pressureBits, temperatureBits, chunk, reference))
                return false;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                AtmosChunk other;
                ushort otherIndex;
                bool leaves = EulerFaceTopology.IsOnEdge(position, last, direction);
                if (leaves)
                {
                    AtmosChunk? across = neighbors[direction];
                    if (across == null)
                        continue;

                    other = across;
                    otherIndex = EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction);
                }
                else
                {
                    other = chunk;
                    otherIndex = (ushort)(voxelIndex + direction switch
                    {
                        0 => -1,
                        1 => 1,
                        2 => -rowStride,
                        3 => rowStride,
                        4 => -slabStride,
                        _ => slabStride
                    });
                }

                int room = other.VoxelRoomMap[otherIndex];
                if (room == VoxelClassification.RoomVoid)
                    return false;

                // Interior air neighbours are compared with the reference when the loop reaches them.
                if (!leaves || room == VoxelClassification.RoomSolid ||
                    !EulerFaceTopology.IsFaceOpen(chunk, voxelIndex, direction, other, otherIndex))
                    continue;

                if (!SameState(other, otherIndex, pressureBits, temperatureBits, chunk, reference) ||
                    !ExtraGasesAreEmpty(chunk, other, otherIndex))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether a cell has the reference pressure and temperature bits and the reference moles of every gas the
    ///     reference chunk lists. A gas the cell's chunk has no channel for counts as zero.
    /// </summary>
    private static bool SameState(
        AtmosChunk chunk, ushort voxelIndex, int pressureBits, int temperatureBits,
        AtmosChunk referenceChunk, ushort reference)
    {
        if (BitConverter.SingleToInt32Bits(chunk.TotalPressure[voxelIndex]) != pressureBits ||
            BitConverter.SingleToInt32Bits(chunk.Temperature[voxelIndex]) != temperatureBits)
            return false;

        bool sameChunk = ReferenceEquals(chunk, referenceChunk);
        for (int gas = 0; gas < referenceChunk.ActiveGasCount; gas++)
        {
            Mole expected = referenceChunk.ActiveGases[gas].Moles[reference];
            int channel = sameChunk ? gas : chunk.FindGasChannel(referenceChunk.ActiveGases[gas].GasId);
            Mole actual = channel >= 0 ? chunk.ActiveGases[channel].Moles[voxelIndex] : 0f;
            if (BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(expected))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether the cell holds no gas the reference chunk lacks a channel for.
    /// </summary>
    private static bool ExtraGasesAreEmpty(AtmosChunk referenceChunk, AtmosChunk chunk, ushort voxelIndex)
    {
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (referenceChunk.FindGasChannel(chunk.ActiveGases[gas].GasId) < 0 &&
                BitConverter.SingleToInt32Bits(chunk.ActiveGases[gas].Moles[voxelIndex]) != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The registered chunk in each direction slot, held on the stack.
    /// </summary>
    [InlineArray(6)]
    private struct NeighborRing
    {
        private AtmosChunk? _first;
    }
}
