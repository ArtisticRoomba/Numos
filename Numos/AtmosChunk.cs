namespace Numos;

/// <summary>
/// Represents the active voxel grid data for a chunk, completely decoupled from any game engine.
/// </summary>
public class AtmosChunk
{
    public Int3 GridPosition;
    public bool IsAwake; 
    public int[] VoxelRoomMap; 
    
    public const int RoomUnassigned = 0;
    public const int RoomVoid = -1;
    public const int RoomSolid = -2;

    public ushort[] ActiveAirIndices; 
    public int ActiveAirCount;
    public int ActiveRoomId;

    public float[] TotalPressure; 
    public float[] Temperature;

    public GasChannel[] ActiveGases;
    public int ActiveGasCount;

    public int SleepTimer;

    public int Width;
    public int Height;
    public int Depth;
    public int VoxelCount;

    public AtmosChunk(int width = 16, int height = 16, int depth = 16)
    {
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = width * height * depth;
        EnsureInitialized();
    }

    public void EnsureInitialized()
    {
        if (VoxelRoomMap == null || VoxelRoomMap.Length != VoxelCount) VoxelRoomMap = new int[VoxelCount];
        if (ActiveAirIndices == null || ActiveAirIndices.Length != VoxelCount) ActiveAirIndices = new ushort[VoxelCount];
        if (TotalPressure == null || TotalPressure.Length != VoxelCount) TotalPressure = new float[VoxelCount];
        if (Temperature == null || Temperature.Length != VoxelCount) Temperature = new float[VoxelCount];
        if (ActiveGases == null) ActiveGases = new GasChannel[16];
    }

    public void Initialize(Int3 position, int width = 16, int height = 16, int depth = 16)
    {
        GridPosition = position;
        IsAwake = false;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = width * height * depth;
        
        EnsureInitialized();
        
        ActiveAirCount = 0;
        ActiveRoomId = RoomUnassigned;
        ActiveGasCount = 0;
        SleepTimer = 0;
        
        Array.Clear(VoxelRoomMap, 0, VoxelRoomMap.Length);
        Array.Clear(ActiveAirIndices, 0, ActiveAirIndices.Length);
        Array.Clear(TotalPressure, 0, TotalPressure.Length);
        Array.Clear(Temperature, 0, Temperature.Length);
        Array.Clear(ActiveGases, 0, ActiveGases.Length);
    }
    
    public void Release()
    {
        if (ActiveGases != null)
        {
            for (int i = 0; i < ActiveGasCount; i++)
            {
                ActiveGases[i].Release();
            }
        }
    }

    public void WakeRoom(int targetRoomId)
    {
        if (targetRoomId == RoomSolid || targetRoomId == RoomVoid) return;
        
        if (IsAwake && ActiveRoomId == targetRoomId)
        {
            SleepTimer = 0;
            return;
        }

        ActiveAirCount = 0;
        IsAwake = true;
        SleepTimer = 0;
        ActiveRoomId = targetRoomId;

        for (ushort i = 0; i < VoxelCount; i++)
        {
            if (VoxelRoomMap[i] == targetRoomId)
            {
                ActiveAirIndices[ActiveAirCount] = i;
                ActiveAirCount++;
            }
        }
    }

    public void InjectGasToVoxel(ushort localVoxelIndex, int gasId, float molesToAdd, float temperature)
    {
        if (!IsAwake) return;
        
        int room = VoxelRoomMap[localVoxelIndex];
        if (room == RoomSolid) return;
        if (room == RoomVoid) return;
        
        SleepTimer = 0;

        int targetChannelIndex = -1;
        for (int i = 0; i < ActiveGasCount; i++)
        {
            if (ActiveGases[i].GasId == gasId)
            {
                targetChannelIndex = i;
                break;
            }
        }
        
        if (targetChannelIndex == -1)
        {
            if (ActiveGasCount >= ActiveGases.Length)
            {
                throw new Exception("Maximum unique gas channels reached for this chunk!");
            }
            
            ActiveGases[ActiveGasCount] = new GasChannel();
            ActiveGases[ActiveGasCount].Initialize(gasId, VoxelCount);

            targetChannelIndex = ActiveGasCount;
            ActiveGasCount++;
        }
        
        ActiveGases[targetChannelIndex].Moles[localVoxelIndex] += molesToAdd;
        
        float currentTotalMoles = 0f;
        for (int g = 0; g < ActiveGasCount; g++)
        {
            currentTotalMoles += ActiveGases[g].Moles[localVoxelIndex];
        }
        
        float currentTemp = Temperature[localVoxelIndex];
        float newTemp = ((currentTotalMoles - molesToAdd) * currentTemp + molesToAdd * temperature) / currentTotalMoles;
        Temperature[localVoxelIndex] = newTemp;

        TotalPressure[localVoxelIndex] = currentTotalMoles * newTemp;
    }
    
    public AtmosChunkSnapshot GetNetworkSnapshot()
    {
        var snapshot = new AtmosChunkSnapshot
        {
            GridPosition = GridPosition,
            TotalPressure = new float[VoxelCount],
            Temperature = new float[VoxelCount],
            Gases = new GasSnapshot[ActiveGasCount],
            VoxelRoomMap = new int[VoxelCount]
        };

        Array.Copy(TotalPressure, snapshot.TotalPressure, VoxelCount);
        Array.Copy(Temperature, snapshot.Temperature, VoxelCount);
        Array.Copy(VoxelRoomMap, snapshot.VoxelRoomMap, VoxelCount);

        for (int g = 0; g < ActiveGasCount; g++)
        {
            snapshot.Gases[g] = new GasSnapshot
            {
                GasId = ActiveGases[g].GasId,
                Moles = new float[VoxelCount]
            };
            Array.Copy(ActiveGases[g].Moles, snapshot.Gases[g].Moles, VoxelCount);
        }

        return snapshot;
    }

    public ushort GetIndex(int x, int y, int z) => (ushort)(x + (y * Width) + (z * Width * Height));
    
    public (int x, int y, int z) GetXYZ(ushort index) => (index % Width, (index / Width) % Height, index / (Width * Height));
}
