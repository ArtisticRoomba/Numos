using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim;

internal readonly record struct VoxelGasMixtureState(
    float Volume,
    float Temperature,
    KeyValuePair<int, float>[] Gases);

internal readonly record struct VoxelGasMixtureAddress(
    Int3 ChunkPosition,
    long ChunkGeneration,
    ushort LocalVoxelIndex);

internal sealed partial class AtmosKernel
{
    internal void ExecuteMixtureTransaction(Action transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_stateGate)
        {
            transaction();
        }
    }

    internal (long Generation, ushort LocalVoxelIndex) GetVoxelMixtureIdentity(
        Int3 position,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            ValidateVoxelIndex(chunk, localVoxelIndex);
            return (chunk.Version.Generation, localVoxelIndex);
        }
    }

    internal (long Generation, ushort LocalVoxelIndex) GetVoxelMixtureIdentity(
        Int3 position,
        int x,
        int y,
        int z)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            ushort localVoxelIndex = GetValidatedVoxelIndex(chunk, x, y, z);
            return (chunk.Version.Generation, localVoxelIndex);
        }
    }

    internal float GetVoxelMixtureVolume(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            GetMixtureChunk(position, generation, localVoxelIndex);
            return GetVoxelVolume();
        }
    }

    internal float GetVoxelMixtureTemperature(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            return chunk.Temperature[localVoxelIndex];
        }
    }

    internal float GetVoxelMixturePressure(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            float totalMoles = GetVoxelTotalMoles(chunk, localVoxelIndex);
            return CalculatePressure(totalMoles, chunk.Temperature[localVoxelIndex]);
        }
    }

    internal float GetVoxelMixtureTotalMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            return GetVoxelTotalMoles(chunk, localVoxelIndex);
        }
    }

    internal int GetVoxelMixtureActiveGasCount(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            var count = 0;
            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                if (chunk.ActiveGases[gas].Moles[localVoxelIndex] > 0f)
                    count++;
            }

            return count;
        }
    }

    internal float GetVoxelMixtureMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                if (chunk.ActiveGases[gas].GasId == gasId)
                    return chunk.ActiveGases[gas].Moles[localVoxelIndex];
            }

            return 0f;
        }
    }

    internal VoxelGasMixtureState CaptureVoxelMixture(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            var gases = new KeyValuePair<int, float>[chunk.ActiveGasCount];
            var gasCount = 0;
            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                float moles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
                if (moles <= 0f)
                    continue;
                gases[gasCount++] = new KeyValuePair<int, float>(chunk.ActiveGases[gas].GasId, moles);
            }

            if (gasCount != gases.Length)
                Array.Resize(ref gases, gasCount);
            Array.Sort(gases, static (left, right) => left.Key.CompareTo(right.Key));

            return new VoxelGasMixtureState(
                GetVoxelVolume(),
                chunk.Temperature[localVoxelIndex],
                gases);
        }
    }

    internal void SetVoxelMixtureTemperature(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        float temperature)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            int roomId = GetGasRoomId(chunk, localVoxelIndex);
            chunk.WakeRoom(roomId);
            chunk.Temperature[localVoxelIndex] = temperature;
            chunk.MarkChanged();
        }
    }

    internal void SetVoxelMixtureMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId,
        float moles)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(moles) && moles >= 0f);
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            int roomId = GetGasRoomId(chunk, localVoxelIndex);
            VoxelGasMixtureTotals totals = CalculateVoxelMixtureTotals(
                chunk,
                localVoxelIndex,
                chunk.Temperature[localVoxelIndex],
                gasId,
                moles);

            chunk.WakeRoom(roomId);
            SetVoxelGasMoles(chunk, localVoxelIndex, gasId, moles);
            ApplyVoxelMixtureTotals(chunk, localVoxelIndex, totals);
        }
    }

    internal void AdjustVoxelMixtureMoles(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId,
        float deltaMoles)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(deltaMoles));
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            float currentMoles = GetVoxelGasMoles(chunk, localVoxelIndex, gasId);
            double adjusted = currentMoles + (double)deltaMoles;
            if (!double.IsFinite(adjusted) || adjusted > float.MaxValue)
                throw new InvalidOperationException("The adjusted gas amount exceeds the supported range.");

            float moles = (float)Math.Max(0d, adjusted);
            int roomId = GetGasRoomId(chunk, localVoxelIndex);
            VoxelGasMixtureTotals totals = CalculateVoxelMixtureTotals(
                chunk,
                localVoxelIndex,
                chunk.Temperature[localVoxelIndex],
                gasId,
                moles);

            chunk.WakeRoom(roomId);
            SetVoxelGasMoles(chunk, localVoxelIndex, gasId, moles);
            ApplyVoxelMixtureTotals(chunk, localVoxelIndex, totals);
        }
    }

    internal void AddVoxelMixtureGas(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        int gasId,
        float moles,
        float temperature)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(moles) && moles > 0f);
        Debug.Assert(float.IsFinite(temperature) && temperature >= 0f);
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            float currentGasMoles = GetVoxelGasMoles(chunk, localVoxelIndex, gasId);
            double combinedGasMoles = currentGasMoles + (double)moles;
            if (!double.IsFinite(combinedGasMoles) || combinedGasMoles > float.MaxValue)
                throw new InvalidOperationException("A merged gas amount exceeds the supported range.");

            VoxelGasMixtureTotals currentTotals = CalculateVoxelMixtureTotals(
                chunk,
                localVoxelIndex,
                chunk.Temperature[localVoxelIndex]);
            float currentHeatCapacity = currentTotals.HeatCapacity;
            float incomingHeatCapacity = moles * GetMolarHeatCapacityAtConstantVolume(gasId);
            double combinedHeatCapacity = currentHeatCapacity + (double)incomingHeatCapacity;
            float mixedTemperature = combinedHeatCapacity > 0d
                ? (float)((currentHeatCapacity * GetEffectiveTemperature(chunk.Temperature[localVoxelIndex]) +
                           incomingHeatCapacity * GetEffectiveTemperature(temperature)) /
                          combinedHeatCapacity)
                : temperature;

            int roomId = GetGasRoomId(chunk, localVoxelIndex);
            VoxelGasMixtureTotals totals = CalculateVoxelMixtureTotals(
                chunk,
                localVoxelIndex,
                mixedTemperature,
                gasId,
                (float)combinedGasMoles);

            chunk.WakeRoom(roomId);
            SetVoxelGasMoles(chunk, localVoxelIndex, gasId, (float)combinedGasMoles);
            chunk.Temperature[localVoxelIndex] = mixedTemperature;
            ApplyVoxelMixtureTotals(chunk, localVoxelIndex, totals);
        }
    }

    internal void ClearVoxelMixture(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            int roomId = GetGasRoomId(chunk, localVoxelIndex);
            chunk.WakeRoom(roomId);
            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
                chunk.ActiveGases[gas].Moles[localVoxelIndex] = 0f;

            ApplyVoxelMixtureTotals(chunk, localVoxelIndex, default);
        }
    }

    internal void ValidateVoxelMixtureMutations(VoxelGasMixtureAddress[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        lock (_stateGate)
        {
            var requiredRooms = new Dictionary<(Int3 Position, long Generation), HashSet<int>>();
            foreach (var address in addresses)
            {
                var chunk = GetMixtureChunk(
                    address.ChunkPosition,
                    address.ChunkGeneration,
                    address.LocalVoxelIndex);
                int roomId = chunk.VoxelRoomMap[address.LocalVoxelIndex];
                if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
                    throw new InvalidOperationException("Solid and void voxels cannot contain a gas mixture.");

                var key = (address.ChunkPosition, address.ChunkGeneration);
                if (!requiredRooms.TryGetValue(key, out var rooms))
                {
                    rooms = [];
                    if (chunk.IsAwake)
                    {
                        for (var room = 0; room < chunk.ActiveRoomCount; room++)
                            rooms.Add(chunk.ActiveRoomIds[room]);
                    }
                    requiredRooms.Add(key, rooms);
                }

                rooms.Add(roomId);
                if (rooms.Count > chunk.MaxActiveRooms)
                {
                    throw new InvalidOperationException(
                        "The gas-mixture transaction would exceed the chunk's active-room capacity.");
                }
            }
        }
    }

    internal void ReplaceVoxelMixture(
        Int3 position,
        long generation,
        ushort localVoxelIndex,
        float temperature,
        KeyValuePair<int, float>[] gases)
    {
        Debug.Assert(gases != null);

        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            int roomId = chunk.VoxelRoomMap[localVoxelIndex];
            Debug.Assert(roomId != VoxelClassification.RoomSolid && roomId != VoxelClassification.RoomVoid);

            double totalMoles = 0d;
            double totalHeatCapacity = 0d;
            foreach (var (gasId, moles) in gases)
            {
                totalMoles += moles;
                totalHeatCapacity += (double)moles * GetMolarHeatCapacityAtConstantVolume(gasId);
            }

            Debug.Assert(double.IsFinite(totalMoles) && totalMoles <= float.MaxValue);
            Debug.Assert(double.IsFinite(totalHeatCapacity) && totalHeatCapacity <= float.MaxValue);

            chunk.WakeRoom(roomId);
            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
                chunk.ActiveGases[gas].Moles[localVoxelIndex] = 0f;

            foreach (var (gasId, moles) in gases)
            {
                int gasChannel = chunk.GetOrCreateGasChannel(gasId);
                chunk.ActiveGases[gasChannel].Moles[localVoxelIndex] = moles;
            }

            chunk.Temperature[localVoxelIndex] = temperature;
            chunk.TotalHeatCapacity[localVoxelIndex] = (float)totalHeatCapacity;
            chunk.TotalPressure[localVoxelIndex] = CalculatePressure((float)totalMoles, temperature);
            chunk.MarkChanged();
        }
    }

    private float GetVoxelTotalMoles(AtmosChunk chunk, ushort localVoxelIndex)
    {
        double totalMoles = 0d;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[localVoxelIndex];
        return (float)totalMoles;
    }

    private static float GetVoxelGasMoles(AtmosChunk chunk, ushort localVoxelIndex, int gasId)
    {
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].GasId == gasId)
                return chunk.ActiveGases[gas].Moles[localVoxelIndex];
        }

        return 0f;
    }

    private static void SetVoxelGasMoles(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles)
    {
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].GasId != gasId)
                continue;
            chunk.ActiveGases[gas].Moles[localVoxelIndex] = moles;
            return;
        }

        if (moles <= 0f)
            return;

        int channel = chunk.GetOrCreateGasChannel(gasId);
        chunk.ActiveGases[channel].Moles[localVoxelIndex] = moles;
    }

    private static int GetGasRoomId(AtmosChunk chunk, ushort localVoxelIndex)
    {
        int roomId = chunk.VoxelRoomMap[localVoxelIndex];
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            throw new InvalidOperationException("Solid and void voxels cannot contain a gas mixture.");

        if (chunk.IsAwake)
        {
            for (var room = 0; room < chunk.ActiveRoomCount; room++)
            {
                if (chunk.ActiveRoomIds[room] == roomId)
                    return roomId;
            }

            if (chunk.ActiveRoomCount >= chunk.MaxActiveRooms)
            {
                throw new InvalidOperationException(
                    "The gas-mixture operation would exceed the chunk's active-room capacity.");
            }
        }

        return roomId;
    }

    private VoxelGasMixtureTotals CalculateVoxelMixtureTotals(
        AtmosChunk chunk,
        ushort localVoxelIndex,
        float temperature,
        int overrideGasId = -1,
        float overrideMoles = 0f)
    {
        double totalMoles = 0d;
        double totalHeatCapacity = 0d;
        var foundOverride = false;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            float moles = gasId == overrideGasId
                ? overrideMoles
                : chunk.ActiveGases[gas].Moles[localVoxelIndex];
            foundOverride |= gasId == overrideGasId;
            if (moles <= 0f)
                continue;

            totalMoles += moles;
            totalHeatCapacity += (double)moles * GetMolarHeatCapacityAtConstantVolume(gasId);
        }

        if (!foundOverride && overrideGasId >= 0 && overrideMoles > 0f)
        {
            totalMoles += overrideMoles;
            totalHeatCapacity +=
                (double)overrideMoles * GetMolarHeatCapacityAtConstantVolume(overrideGasId);
        }

        if (!double.IsFinite(totalMoles) || totalMoles > float.MaxValue)
            throw new InvalidOperationException("The mixture's total moles exceed the supported range.");
        if (!double.IsFinite(totalHeatCapacity) || totalHeatCapacity > float.MaxValue)
            throw new InvalidOperationException("The mixture's heat capacity exceeds the supported range.");

        double pressure = totalMoles * AtmosPhysicalConstants.MolarGasConstant *
                          GetEffectiveTemperature(temperature) / GetVoxelVolume();
        if (!double.IsFinite(pressure) || pressure > float.MaxValue)
            throw new InvalidOperationException("The mixture's pressure exceeds the supported range.");

        return new VoxelGasMixtureTotals(
            (float)totalHeatCapacity,
            (float)pressure);
    }

    private static void ApplyVoxelMixtureTotals(
        AtmosChunk chunk,
        ushort localVoxelIndex,
        VoxelGasMixtureTotals totals)
    {
        chunk.TotalHeatCapacity[localVoxelIndex] = totals.HeatCapacity;
        chunk.TotalPressure[localVoxelIndex] = totals.Pressure;
        chunk.MarkChanged();
    }

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

    private readonly record struct VoxelGasMixtureTotals(
        float HeatCapacity,
        float Pressure);
}