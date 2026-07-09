using System;
using System.Buffers;

namespace Opal.Prototypes.AtmosAndThermalGeneric;

/// <summary>
/// Represents a single gas type within a chunk using a Structure of Arrays (SoA) layout.
/// </summary>
public struct GasChannel
{
    public int GasId;
    public float[] Moles;
    public bool IsInitialized => Moles != null;
    
    public void Initialize(int gasId, int voxelCount)
    {
        GasId = gasId;
        Moles = ArrayPool<float>.Shared.Rent(voxelCount);
        Array.Clear(Moles, 0, voxelCount);
    }

    public void Release()
    {
        if (IsInitialized)
        {
            ArrayPool<float>.Shared.Return(Moles);
            Moles = null;
        }
    }
}
