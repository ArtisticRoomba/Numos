using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     A simulation-owned gas mixture with independent sparse backing storage, suitable for canisters and other
///     portable or arbitrary-volume containers.
/// </summary>
/// <remarks>
///     Instances are created through <see cref="AtmosSimulation.CreateGasMixture" />. They retain their owning
///     simulation so energy calculations use the same gas registry and configuration as voxel mixtures.
/// </remarks>
[PublicAPI]
public sealed class GasMixture : IInternalGasMixture
{
    internal GasMixture(AtmosSimulation owner, GasMixtureState state)
    {
        Owner = owner;
        State = state;
    }

    internal GasMixtureState State { get; private set; }

    /// <inheritdoc />
    public AtmosSimulation Owner { get; }

    /// <inheritdoc />
    public float Volume
    {
        get => Owner.GetMixtureVolume(this);
        set => Owner.SetMixtureVolume(this, value);
    }

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

    internal GasMixtureState CaptureState()
    {
        return State.Clone();
    }

    internal void ApplyState(GasMixtureState state)
    {
        State = state;
    }
}