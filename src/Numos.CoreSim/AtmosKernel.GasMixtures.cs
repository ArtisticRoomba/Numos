using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Immutable snapshot of one voxel mixture, detached from the kernel's storage arrays.
/// </summary>
/// <remarks>
///     <paramref name="TotalMoles" /> and <paramref name="Pressure" /> are the values that the mixture's own total and
///     pressure members report. They sum the channels in page order. A sum over <paramref name="Gases" />, which is in
///     gas-ID order, can round differently.
/// </remarks>
internal readonly record struct VoxelGasMixtureState(
    CubicMetre Volume,
    Kelvin Temperature,
    KeyValuePair<int, Mole>[] Gases,
    Mole TotalMoles,
    Pascal Pressure);

/// <summary>
///     Generation-bound address used to preflight a voxel-mixture transaction.
/// </summary>
internal readonly record struct VoxelGasMixtureAddress(
    Int3 ChunkPosition,
    long ChunkGeneration,
    ushort LocalVoxelIndex);

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Runs a compound voxel-mixture operation while holding the kernel state lock.
    /// </summary>
    /// <remarks>
    ///     Used by the API facade to make snapshot, validation, and replacement one atomic operation.
    /// </remarks>
    internal void ExecuteMixtureTransaction(Action transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (StateGate)
        {
            transaction();
        }
    }

    /// <summary>
    ///     Validates an index and returns the generation-bound identity for a voxel mixture handle.
    /// </summary>
    internal (long Generation, ushort LocalVoxelIndex) GetVoxelMixtureIdentity(
        Int3 position,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetChunk(position);
            ValidateVoxelIndex(chunk, localVoxelIndex);
            return (chunk.Version.Generation, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Validates local coordinates and returns the generation-bound identity for a voxel mixture handle.
    /// </summary>
    internal (long Generation, ushort LocalVoxelIndex) GetVoxelMixtureIdentity(
        Int3 position,
        int x,
        int y,
        int z)
    {
        lock (StateGate)
        {
            var chunk = GetChunk(position);
            ushort localVoxelIndex = GetValidatedVoxelIndex(chunk, x, y, z);
            return (chunk.Version.Generation, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Gets the configured volume of a generation-current voxel.
    /// </summary>
    internal CubicMetre GetVoxelMixtureVolume(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            GetMixtureChunk(position, generation, localVoxelIndex);
            return _config.GetVoxelVolume();
        }
    }

    /// <summary>
    ///     Gets a voxel's stored temperature after validating its generation-bound address.
    /// </summary>
    internal Kelvin GetVoxelMixtureTemperature(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            return chunk.Temperature[localVoxelIndex];
        }
    }

    /// <summary>
    ///     Calculates a voxel's present pressure from its species amounts and stored temperature.
    /// </summary>
    internal Pascal GetVoxelMixturePressure(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            Mole totalMoles = GasMixtureCells.GetTotalMoles(chunk, localVoxelIndex);
            return AtmosSolverMath.CalculatePressure(
                _config,
                totalMoles,
                chunk.Temperature[localVoxelIndex],
                _config.PressurePerMoleKelvin);
        }
    }

    /// <summary>
    ///     Gets the sum of all gas-channel amounts stored at a voxel.
    /// </summary>
    internal Mole GetVoxelMixtureTotalMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            return GasMixtureCells.GetTotalMoles(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Counts gas channels with a positive amount at a voxel.
    /// </summary>
    internal int GetVoxelMixtureActiveGasCount(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            return GasMixtureCells.CountActiveGases(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Gets one gas amount, returning zero when that species has no channel in the chunk.
    /// </summary>
    internal Mole GetVoxelMixtureMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            return GasMixtureCells.GetGasMoles(chunk, localVoxelIndex, gasId);
        }
    }

    /// <summary>
    ///     Captures an ordered, detached snapshot of a voxel's positive gas species.
    /// </summary>
    internal VoxelGasMixtureState CaptureVoxelMixture(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            Mole totalMoles = GasMixtureCells.GetTotalMoles(chunk, localVoxelIndex);
            return new VoxelGasMixtureState(
                _config.GetVoxelVolume(),
                chunk.Temperature[localVoxelIndex],
                GasMixtureCells.CaptureGases(chunk, localVoxelIndex),
                totalMoles,
                AtmosSolverMath.CalculatePressure(
                    _config,
                    totalMoles,
                    chunk.Temperature[localVoxelIndex],
                    _config.PressurePerMoleKelvin));
        }
    }

    /// <summary>
    ///     Sets a voxel's temperature, wakes its chunk, and marks its chunk changed.
    /// </summary>
    internal void SetVoxelMixtureTemperature(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        Kelvin temperature)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            GasMixtureCells.ValidateGasVoxel(chunk, localVoxelIndex);
            chunk.Wake();
            chunk.Temperature[localVoxelIndex] = temperature;
            chunk.MarkChanged();
            RecordVoxelMixture(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Replaces one species amount and atomically refreshes cached heat capacity and pressure.
    /// </summary>
    internal void SetVoxelMixtureMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId,
        Mole moles)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(moles) && moles >= 0f);
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            GasMixtureCells.SetMoles(
                _config,
                _config.PressurePerMoleKelvin,
                chunk,
                localVoxelIndex,
                gasId,
                moles);

            chunk.Wake();
            RecordVoxelMixture(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Applies a signed mole adjustment, clamping the resulting amount at zero.
    /// </summary>
    internal void AdjustVoxelMixtureMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId,
        Mole deltaMoles)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(deltaMoles));
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            GasMixtureCells.AdjustMoles(
                _config,
                _config.PressurePerMoleKelvin,
                chunk,
                localVoxelIndex,
                gasId,
                deltaMoles);

            chunk.Wake();
            RecordVoxelMixture(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Adds gas at a temperature and heat-capacity-weights the resulting mixture temperature.
    /// </summary>
    internal void AddVoxelMixtureGas(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId,
        Mole moles,
        Kelvin temperature)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(moles) && moles > 0f);
        Debug.Assert(float.IsFinite(temperature) && temperature >= 0f);
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            GasMixtureCells.AddGas(
                _config,
                _config.PressurePerMoleKelvin,
                chunk,
                localVoxelIndex,
                gasId,
                moles,
                temperature);

            chunk.Wake();
            RecordVoxelMixture(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Removes every gas amount from a voxel and clears its cached mixture totals.
    /// </summary>
    internal void ClearVoxelMixture(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            GasMixtureCells.Clear(chunk, localVoxelIndex);
            chunk.Wake();
            RecordVoxelMixture(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Validates a multi-voxel mutation for generation validity and gas-bearing voxel eligibility.
    /// </summary>
    internal void ValidateVoxelMixtureMutations(VoxelGasMixtureAddress[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        lock (StateGate)
        {
            foreach (var address in addresses)
            {
                var chunk = GetMixtureChunk(
                    address.ChunkPosition,
                    address.ChunkGeneration,
                    address.LocalVoxelIndex);

                int classification = chunk.VoxelRoomMap[address.LocalVoxelIndex];
                if (classification == VoxelClassification.RoomSolid || classification == VoxelClassification.RoomVoid)
                    throw new InvalidOperationException("Solid and void voxels cannot contain a gas mixture.");
            }
        }
    }

    /// <summary>
    ///     Replaces the complete mixture of an eligible voxel from a detached state snapshot.
    /// </summary>
    internal void ReplaceVoxelMixture(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        Kelvin temperature,
        KeyValuePair<int, Mole>[] gases)
    {
        Debug.Assert(gases != null);

        lock (StateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            int classification = chunk.VoxelRoomMap[localVoxelIndex];
            Debug.Assert(classification != VoxelClassification.RoomSolid && classification != VoxelClassification.RoomVoid);

            GasMixtureCells.Replace(
                _config,
                _config.PressurePerMoleKelvin,
                chunk,
                localVoxelIndex,
                temperature,
                gases);

            chunk.Wake();
            RecordVoxelMixture(chunk, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Resolves a mixture address and rejects handles from a removed or replaced chunk generation.
    /// </summary>
    private AtmosChunk GetMixtureChunk(Int3 position, long generation, ushort localVoxelIndex)
    {
        var chunk = GetChunk(position);
        if (chunk.Version.Generation != generation)
        {
            throw new InvalidOperationException(
                "The voxel gas mixture is stale because its original chunk was unregistered or replaced.");
        }

        ValidateVoxelIndex(chunk, localVoxelIndex);
        return chunk;
    }
}
