using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Generation-bound, sandboxed access to one voxel's structure-of-arrays gas state.
/// </summary>
internal sealed class VoxelGasMixture : IInternalGasMixture
{
    internal VoxelGasMixture(
        AtmosSimulation simulation,
        Int3 chunkPosition,
        long chunkGeneration,
        ushort localVoxelIndex)
    {
        Simulation = simulation;
        ChunkPosition = chunkPosition;
        ChunkGeneration = chunkGeneration;
        LocalVoxelIndex = localVoxelIndex;
    }

    internal AtmosSimulation Simulation { get; }
    internal Int3 ChunkPosition { get; }
    internal long ChunkGeneration { get; }
    internal ushort LocalVoxelIndex { get; }

    /// <inheritdoc />
    public AtmosWorld World => Simulation.World;

    /// <inheritdoc />
    public float Volume => World.GetMixtureVolume(this);

    /// <inheritdoc />
    public float Temperature
    {
        get => World.GetMixtureTemperature(this);
        set => World.SetMixtureTemperature(this, value);
    }

    /// <inheritdoc />
    public float Pressure => World.GetMixturePressure(this);

    /// <inheritdoc />
    public float TotalMoles => World.GetMixtureTotalMoles(this);

    /// <inheritdoc />
    public int ActiveGasCount => World.GetMixtureActiveGasCount(this);

    /// <inheritdoc />
    public float GetMoles(int gasId)
    {
        return World.GetMixtureMoles(this, gasId);
    }

    /// <inheritdoc />
    public void SetMoles(int gasId, float moles)
    {
        World.SetMixtureMoles(this, gasId, moles);
    }

    /// <inheritdoc />
    public void AdjustMoles(int gasId, float deltaMoles)
    {
        World.AdjustMixtureMoles(this, gasId, deltaMoles);
    }

    /// <inheritdoc />
    public void AddGas(int gasId, float moles, float temperature)
    {
        World.AddGasToMixture(this, gasId, moles, temperature);
    }

    /// <inheritdoc />
    public float GetMoles(string gasName)
    {
        return World.GetMixtureMoles(this, gasName);
    }

    /// <inheritdoc />
    public void SetMoles(string gasName, float moles)
    {
        World.SetMixtureMoles(this, gasName, moles);
    }

    /// <inheritdoc />
    public void AdjustMoles(string gasName, float deltaMoles)
    {
        World.AdjustMixtureMoles(this, gasName, deltaMoles);
    }

    /// <inheritdoc />
    public void AddGas(string gasName, float moles, float temperature)
    {
        World.AddGasToMixture(this, gasName, moles, temperature);
    }

    /// <inheritdoc />
    public void Clear()
    {
        World.ClearMixture(this);
    }

    /// <inheritdoc />
    public GasMixture Remove(float moles)
    {
        return World.RemoveFromMixture(this, moles);
    }

    /// <inheritdoc />
    public GasMixture RemoveRatio(float ratio)
    {
        return World.RemoveRatioFromMixture(this, ratio);
    }

    /// <inheritdoc />
    public GasMixture RemoveVolume(float volume)
    {
        return World.RemoveVolumeFromMixture(this, volume);
    }

    /// <inheritdoc />
    public float TransferTo(IGasMixture destination, float moles)
    {
        return World.TransferMixture(this, destination, moles);
    }

    /// <inheritdoc />
    public float TransferRatioTo(IGasMixture destination, float ratio)
    {
        return World.TransferMixtureRatio(this, destination, ratio);
    }

    /// <inheritdoc />
    public GasMixture Clone()
    {
        return World.CloneMixture(this);
    }

    /// <inheritdoc />
    public GasMixtureSnapshot GetSnapshot()
    {
        return World.GetMixtureSnapshot(this);
    }
}
