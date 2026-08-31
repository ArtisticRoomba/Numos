using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Generation-bound, sandboxed access to one voxel's structure-of-arrays gas state.
/// </summary>
internal sealed class VoxelGasMixture : IInternalGasMixture
{
    internal VoxelGasMixture(
        AtmosSimulation owner,
        Int3 chunkPosition,
        long chunkGeneration,
        ushort localVoxelIndex)
    {
        Owner = owner;
        ChunkPosition = chunkPosition;
        ChunkGeneration = chunkGeneration;
        LocalVoxelIndex = localVoxelIndex;
    }

    internal Int3 ChunkPosition { get; }
    internal long ChunkGeneration { get; }
    internal ushort LocalVoxelIndex { get; }

    public AtmosSimulation Owner { get; }
    public float Volume => Owner.GetMixtureVolume(this);

    public float Temperature
    {
        get => Owner.GetMixtureTemperature(this);
        set => Owner.SetMixtureTemperature(this, value);
    }

    public float Pressure => Owner.GetMixturePressure(this);
    public float TotalMoles => Owner.GetMixtureTotalMoles(this);
    public int ActiveGasCount => Owner.GetMixtureActiveGasCount(this);

    public float GetMoles(int gasId)
    {
        return Owner.GetMixtureMoles(this, gasId);
    }

    public void SetMoles(int gasId, float moles)
    {
        Owner.SetMixtureMoles(this, gasId, moles);
    }

    public void AdjustMoles(int gasId, float deltaMoles)
    {
        Owner.AdjustMixtureMoles(this, gasId, deltaMoles);
    }

    public void AddGas(int gasId, float moles, float temperature)
    {
        Owner.AddGasToMixture(this, gasId, moles, temperature);
    }

    public void Clear()
    {
        Owner.ClearMixture(this);
    }

    public GasMixture Remove(float moles)
    {
        return Owner.RemoveFromMixture(this, moles);
    }

    public GasMixture RemoveRatio(float ratio)
    {
        return Owner.RemoveRatioFromMixture(this, ratio);
    }

    public GasMixture RemoveVolume(float volume)
    {
        return Owner.RemoveVolumeFromMixture(this, volume);
    }

    public float TransferTo(IGasMixture destination, float moles)
    {
        return Owner.TransferMixture(this, destination, moles);
    }

    public float TransferRatioTo(IGasMixture destination, float ratio)
    {
        return Owner.TransferMixtureRatio(this, destination, ratio);
    }

    public GasMixture Clone()
    {
        return Owner.CloneMixture(this);
    }

    public GasMixtureSnapshot GetSnapshot()
    {
        return Owner.GetMixtureSnapshot(this);
    }
}