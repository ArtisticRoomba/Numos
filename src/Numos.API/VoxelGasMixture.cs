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

    /// <inheritdoc />
    public AtmosSimulation Owner { get; }

    /// <inheritdoc />
    public float Volume => Owner.GetMixtureVolume(this);

    /// <inheritdoc />
    public float Temperature
    {
        get => Owner.GetMixtureTemperature(this);
        set => Owner.SetMixtureTemperature(this, value);
    }

    /// <inheritdoc />
    public float Pressure => Owner.GetMixturePressure(this);

    /// <inheritdoc />
    public float TotalMoles => Owner.GetMixtureTotalMoles(this);

    /// <inheritdoc />
    public int ActiveGasCount => Owner.GetMixtureActiveGasCount(this);

    /// <inheritdoc />
    public float GetMoles(int gasId)
    {
        return Owner.GetMixtureMoles(this, gasId);
    }

    /// <inheritdoc />
    public void SetMoles(int gasId, float moles)
    {
        Owner.SetMixtureMoles(this, gasId, moles);
    }

    /// <inheritdoc />
    public void AdjustMoles(int gasId, float deltaMoles)
    {
        Owner.AdjustMixtureMoles(this, gasId, deltaMoles);
    }

    /// <inheritdoc />
    public void AddGas(int gasId, float moles, float temperature)
    {
        Owner.AddGasToMixture(this, gasId, moles, temperature);
    }

    /// <inheritdoc />
    public float GetMoles(string gasName)
    {
        return Owner.GetMixtureMoles(this, gasName);
    }

    /// <inheritdoc />
    public void SetMoles(string gasName, float moles)
    {
        Owner.SetMixtureMoles(this, gasName, moles);
    }

    /// <inheritdoc />
    public void AdjustMoles(string gasName, float deltaMoles)
    {
        Owner.AdjustMixtureMoles(this, gasName, deltaMoles);
    }

    /// <inheritdoc />
    public void AddGas(string gasName, float moles, float temperature)
    {
        Owner.AddGasToMixture(this, gasName, moles, temperature);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Owner.ClearMixture(this);
    }

    /// <inheritdoc />
    public GasMixture Remove(float moles)
    {
        return Owner.RemoveFromMixture(this, moles);
    }

    /// <inheritdoc />
    public GasMixture RemoveRatio(float ratio)
    {
        return Owner.RemoveRatioFromMixture(this, ratio);
    }

    /// <inheritdoc />
    public GasMixture RemoveVolume(float volume)
    {
        return Owner.RemoveVolumeFromMixture(this, volume);
    }

    /// <inheritdoc />
    public float TransferTo(IGasMixture destination, float moles)
    {
        return Owner.TransferMixture(this, destination, moles);
    }

    /// <inheritdoc />
    public float TransferRatioTo(IGasMixture destination, float ratio)
    {
        return Owner.TransferMixtureRatio(this, destination, ratio);
    }

    /// <inheritdoc />
    public GasMixture Clone()
    {
        return Owner.CloneMixture(this);
    }

    /// <inheritdoc />
    public GasMixtureSnapshot GetSnapshot()
    {
        return Owner.GetMixtureSnapshot(this);
    }
}