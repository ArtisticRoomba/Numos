using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim;

internal readonly record struct VoxelGasMixtureMetrics(
    float Volume,
    float Temperature,
    float Pressure,
    float TotalMoles,
    int ActiveGasCount);

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

    internal VoxelGasMixtureMetrics GetVoxelMixtureMetrics(
        Int3 position,
        long generation,
        ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            double totalMoles = 0d;
            var activeGasCount = 0;
            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                float moles = MathF.Max(0f, chunk.ActiveGases[gas].Moles[localVoxelIndex]);
                if (moles <= 0f)
                    continue;
                totalMoles += moles;
                activeGasCount++;
            }

            float storedTemperature = chunk.Temperature[localVoxelIndex];
            float pressure = CalculatePressure((float)totalMoles, storedTemperature);
            return new VoxelGasMixtureMetrics(
                GetVoxelVolume(),
                storedTemperature,
                pressure,
                (float)totalMoles,
                activeGasCount);
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
                    return MathF.Max(0f, chunk.ActiveGases[gas].Moles[localVoxelIndex]);
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
                float moles = MathF.Max(0f, chunk.ActiveGases[gas].Moles[localVoxelIndex]);
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
        ArgumentNullException.ThrowIfNull(gases);
        if (!float.IsFinite(temperature) || temperature < 0f)
            throw new ArgumentOutOfRangeException(nameof(temperature));

        var previousGasId = -1;
        double totalMoles = 0d;
        foreach (var (gasId, moles) in gases)
        {
            if (gasId < 0)
                throw new ArgumentOutOfRangeException(nameof(gases), "Gas IDs must be nonnegative.");
            if (gasId <= previousGasId)
                throw new ArgumentException("Gas IDs must be unique and ordered.", nameof(gases));
            if (!float.IsFinite(moles) || moles <= 0f)
                throw new ArgumentOutOfRangeException(nameof(gases), "Gas amounts must be positive and finite.");
            previousGasId = gasId;
            totalMoles += moles;
        }
        if (!double.IsFinite(totalMoles) || totalMoles > float.MaxValue)
            throw new InvalidOperationException("The mixture's total moles exceed the supported range.");

        lock (_stateGate)
        {
            var chunk = GetMixtureChunk(position, generation, localVoxelIndex);
            int roomId = chunk.VoxelRoomMap[localVoxelIndex];
            if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
                throw new InvalidOperationException("Solid and void voxels cannot contain a gas mixture.");

            double totalHeatCapacity = 0d;
            foreach (var (gasId, moles) in gases)
                totalHeatCapacity += (double)moles * GetMolarHeatCapacityAtConstantVolume(gasId);
            if (!double.IsFinite(totalHeatCapacity) || totalHeatCapacity > float.MaxValue)
                throw new InvalidOperationException("The mixture's total heat capacity exceeds the supported range.");

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