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
    private GasMixtureState _state;

    internal GasMixture(AtmosSimulation owner, GasMixtureState state)
    {
        Owner = owner;
        _state = state;
    }

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
    public float GetMoles(int gasId) => Owner.GetMixtureMoles(this, gasId);

    /// <inheritdoc />
    public void SetMoles(int gasId, float moles) => Owner.SetMixtureMoles(this, gasId, moles);

    /// <inheritdoc />
    public void AdjustMoles(int gasId, float deltaMoles) =>
        Owner.AdjustMixtureMoles(this, gasId, deltaMoles);

    /// <inheritdoc />
    public void AddGas(int gasId, float moles, float temperature) =>
        Owner.AddGasToMixture(this, gasId, moles, temperature);

    /// <inheritdoc />
    public void Clear() => Owner.ClearMixture(this);

    /// <inheritdoc />
    public GasMixture Remove(float moles) => Owner.RemoveFromMixture(this, moles);

    /// <inheritdoc />
    public GasMixture RemoveRatio(float ratio) => Owner.RemoveRatioFromMixture(this, ratio);

    /// <inheritdoc />
    public GasMixture RemoveVolume(float volume) => Owner.RemoveVolumeFromMixture(this, volume);

    /// <inheritdoc />
    public float TransferTo(IGasMixture destination, float moles) =>
        Owner.TransferMixture(this, destination, moles);

    /// <inheritdoc />
    public float TransferRatioTo(IGasMixture destination, float ratio) =>
        Owner.TransferMixtureRatio(this, destination, ratio);

    /// <inheritdoc />
    public GasMixture Clone() => Owner.CloneMixture(this);

    /// <inheritdoc />
    public GasMixtureSnapshot GetSnapshot() => Owner.GetMixtureSnapshot(this);

    internal GasMixtureState CaptureState() => _state.Clone();

    internal GasMixtureState State => _state;

    internal void ApplyState(GasMixtureState state)
    {
        _state = state.Clone();
    }
}