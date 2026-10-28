using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
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
///         so a source can reach a chunk only if it lies within the substep count of cells along a path of open
///         faces. The stage checks that after the pre-pass (see <see cref="EulerFlowSolver" />).
///     </para>
///     <para>
///         The test reads live chunk fields only and writes nothing to the chunk, so chunks run in parallel and
///         the result does not depend on the worker count.
///     </para>
///     <para>
///         The interior and void passes read only the chunk's own fields. While the chunk's
///         <see cref="AtmosChunk.Version" /> holds, those fields hold too, so the caller keeps the version at
///         which both passed and the test skips them. The signal speed and the face passes still run, because
///         they read the config and the neighbours, which change on their own.
///     </para>
/// </remarks>
internal static class EulerStaticChunk
{
    /// <summary>
    ///     The largest channel map, in ints, that <see cref="IsStatic" /> keeps on the stack.
    ///     A chunk with more than 85 gas channels takes an array instead.
    /// </summary>
    private const int MaximumStackChannelMapLength = 1024;

    /// <summary>
    ///     Whether the chunk is uniform, at rest, and bit-identical across every open chunk face.
    /// </summary>
    /// <param name="chunk">An awake chunk with at least one gas channel.</param>
    /// <param name="context">The tick context, used to find neighbour chunks.</param>
    /// <param name="interiorPassedAt">
    ///     The chunk version at which the interior and void passes last passed, or <see langword="default" />. The
    ///     test skips both passes when it equals the current version, and stores the current version when both pass.
    /// </param>
    /// <param name="signalSpeed">The signal speed a gather would give every cell, valid when the result is true.</param>
    /// <returns><see langword="true" /> when a sweep over the chunk would change no bit of it.</returns>
    /// <remarks>
    ///     No allocation. The first air voxel is the reference, and every other cell is compared with it bit for
    ///     bit. The interior, the void test, and each registered face are separate passes with early exits. A
    ///     failed pass leaves <paramref name="interiorPassedAt" /> unchanged.
    /// </remarks>
    internal static bool IsStatic(
        AtmosChunk chunk, AtmosSolverExecutionContext context, ref AtmosChunkVersion interiorPassedAt,
        out MetrePerSecond signalSpeed)
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
        int stride = chunk.ActiveGasCount;
        for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
        {
            if (!context.TryGetChunk(chunk.GridPosition + NeighborResolution.NeighborDirections[direction], out AtmosChunk neighbor))
                continue;

            neighbors[direction] = neighbor;
            stride = Math.Max(stride, neighbor.ActiveGasCount);
        }

        int scratchLength = FaceGasChannels.GetScratchLength(stride);
        Span<int> scratch = scratchLength <= MaximumStackChannelMapLength
            ? stackalloc int[scratchLength]
            : new int[scratchLength];

        var channels = new FaceGasChannels(chunk, stride, scratch);

        AtmosChunkVersion version = chunk.Version;
        if (interiorPassedAt != version)
        {
            if (!InteriorIsUniformAndAtRest(chunk, reference, pressureBits, temperatureBits) ||
                !HasNoVoidNeighbor(chunk))
                return false;

            interiorPassedAt = version;
        }

        for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
        {
            AtmosChunk? across = neighbors[direction];
            if (across != null &&
                !FaceMatchesNeighbor(chunk, direction, across, reference, pressureBits, temperatureBits, channels))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether every air voxel has no momentum and the reference pressure, temperature, and moles.
    /// </summary>
    /// <remarks>
    ///     When every voxel is air the arrays are contiguous, so the bits are compared as spans. Momentum uses float
    ///     equality like <see cref="AtmosChunk.HasMomentum" />, where <c>-0f</c> is rest and NaN is not.
    /// </remarks>
    private static bool InteriorIsUniformAndAtRest(
        AtmosChunk chunk, ushort reference, int pressureBits, int temperatureBits)
    {
        if (chunk.ActiveAirCount != chunk.VoxelCount)
        {
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                if (chunk.HasMomentum(voxelIndex) ||
                    !SameState(chunk, voxelIndex, pressureBits, temperatureBits, chunk, reference, default))
                    return false;
            }

            return true;
        }

        int count = chunk.VoxelCount;
        if (!IsAllZero(chunk.MomentumX.AsSpan()[..count]) ||
            !IsAllZero(chunk.MomentumY.AsSpan()[..count]) ||
            !IsAllZero(chunk.MomentumZ.AsSpan()[..count]))
            return false;

        if (MemoryMarshal.Cast<Pascal, int>(chunk.TotalPressure.AsSpan()[..count]).IndexOfAnyExcept(pressureBits) >= 0 ||
            MemoryMarshal.Cast<Kelvin, int>(chunk.Temperature.AsSpan()[..count]).IndexOfAnyExcept(temperatureBits) >= 0)
            return false;

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole[] moles = chunk.ActiveGases[gas].Moles;
            int expected = BitConverter.SingleToInt32Bits(moles[reference]);
            if (MemoryMarshal.Cast<Mole, int>(moles.AsSpan(0, count)).IndexOfAnyExcept(expected) >= 0)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether every value equals zero by float comparison. A NaN is not zero.
    /// </summary>
    private static bool IsAllZero(ReadOnlySpan<float> values)
    {
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int lastVector = values.Length - Vector<float>.Count;
            for (; index <= lastVector; index += Vector<float>.Count)
            {
                if (!Vector.EqualsAll(new Vector<float>(values.Slice(index)), Vector<float>.Zero))
                    return false;
            }
        }

        for (; index < values.Length; index++)
        {
            if (values[index] != 0f)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether no air voxel has a void neighbour inside the chunk. Faces that leave the chunk are tested by
    ///     <see cref="FaceMatchesNeighbor" />.
    /// </summary>
    private static bool HasNoVoidNeighbor(AtmosChunk chunk)
    {
        if (!chunk.VoxelRoomMap.AsSpan()[..chunk.VoxelCount].Contains(VoxelClassification.RoomVoid))
            return true;

        Int3 last = chunk.Dimensions - new Int3(1, 1, 1);
        int rowStride = chunk.Width;
        int slabStride = chunk.Width * chunk.Height;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Int3 position = chunk.GetXyzInt3(voxelIndex);
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (EulerFaceTopology.IsOnEdge(position, last, direction))
                    continue;

                int otherIndex = voxelIndex + direction switch
                {
                    0 => -1,
                    1 => 1,
                    2 => -rowStride,
                    3 => rowStride,
                    4 => -slabStride,
                    _ => slabStride
                };

                if (chunk.VoxelRoomMap[otherIndex] == VoxelClassification.RoomVoid)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether every air voxel on one chunk face meets a non-void cell, and an air cell across an open face has
    ///     the reference state and no extra gas.
    /// </summary>
    private static bool FaceMatchesNeighbor(
        AtmosChunk chunk, int direction, AtmosChunk neighbor, ushort reference, int pressureBits,
        int temperatureBits, FaceGasChannels channels)
    {
        FacePlane plane = EulerFaceTopology.GetFacePlane(chunk, direction);
        for (int outer = 0; outer < plane.OuterCount; outer++)
        {
            for (int inner = 0; inner < plane.InnerCount; inner++)
            {
                ushort voxelIndex = plane.VoxelIndex(inner, outer);
                int ownRoom = chunk.VoxelRoomMap[voxelIndex];
                if (ownRoom == VoxelClassification.RoomSolid || ownRoom == VoxelClassification.RoomVoid)
                    continue;

                ushort otherIndex = EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction);
                int room = neighbor.VoxelRoomMap[otherIndex];
                if (room == VoxelClassification.RoomVoid)
                    return false;

                if (room == VoxelClassification.RoomSolid ||
                    !EulerFaceTopology.IsFaceOpen(chunk, voxelIndex, direction, neighbor, otherIndex))
                    continue;

                if (!SameState(
                        neighbor, otherIndex, pressureBits, temperatureBits, chunk, reference,
                        channels.OwnToNeighbor(direction, neighbor)) ||
                    !ExtraGasesAreEmpty(neighbor, otherIndex, channels.NeighborToOwn(direction, neighbor)))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether a cell has the reference pressure and temperature bits and the reference moles of every gas the
    ///     reference chunk lists. A gas the cell's chunk has no channel for counts as zero.
    /// </summary>
    /// <remarks>
    ///     <paramref name="referenceChannels" /> holds the channel of <paramref name="chunk" /> for each channel of
    ///     <paramref name="referenceChunk" />, or -1. It is not read when both chunks are the same.
    /// </remarks>
    private static bool SameState(
        AtmosChunk chunk, ushort voxelIndex, int pressureBits, int temperatureBits,
        AtmosChunk referenceChunk, ushort reference, ReadOnlySpan<int> referenceChannels)
    {
        if (BitConverter.SingleToInt32Bits(chunk.TotalPressure[voxelIndex]) != pressureBits ||
            BitConverter.SingleToInt32Bits(chunk.Temperature[voxelIndex]) != temperatureBits)
            return false;

        bool sameChunk = ReferenceEquals(chunk, referenceChunk);
        for (int gas = 0; gas < referenceChunk.ActiveGasCount; gas++)
        {
            Mole expected = referenceChunk.ActiveGases[gas].Moles[reference];
            int channel = sameChunk ? gas : referenceChannels[gas];
            Mole actual = channel >= 0 ? chunk.ActiveGases[channel].Moles[voxelIndex] : 0f;
            if (BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(expected))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether the cell holds no gas the reference chunk lacks a channel for.
    /// </summary>
    /// <remarks>
    ///     <paramref name="referenceChannels" /> holds the channel of the reference chunk for each channel of
    ///     <paramref name="chunk" />, or -1.
    /// </remarks>
    private static bool ExtraGasesAreEmpty(AtmosChunk chunk, ushort voxelIndex, ReadOnlySpan<int> referenceChannels)
    {
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (referenceChannels[gas] < 0 &&
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
