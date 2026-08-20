using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     A mutable, simulation-owned gas volume.
/// </summary>
/// <remarks>
///     Implementations may own detached container storage or represent a live voxel. Members never expose the
///     solver's backing arrays or spans. Every live-voxel operation is serialized with simulation ticks. This is a
///     common capability surface, not an extension point: transfer endpoints must be instances created by
///     <see cref="AtmosSimulation" />.
/// </remarks>
[PublicAPI]
public interface IGasMixture
{
    /// <summary>The simulation that owns this mixture and resolves its gas properties.</summary>
    AtmosSimulation Owner { get; }

    /// <summary>The represented volume, in cubic metres (m³).</summary>
    float Volume { get; }

    /// <summary>The stored temperature, in kelvins (K).</summary>
    /// <remarks>
    ///     Subject to a representable derived pressure, the setter stores the raw value. Pressure and energy
    ///     calculations use the owner's configured fallback when the stored value is non-finite or nonpositive.
    /// </remarks>
    float Temperature { get; set; }

    /// <summary>The ideal-gas pressure, in pascals (Pa).</summary>
    /// <exception cref="InvalidOperationException">
    ///     The mixture's pressure is not representable under the owner's current live configuration.
    /// </exception>
    float Pressure { get; }

    /// <summary>The total amount of gas, in moles (mol).</summary>
    float TotalMoles { get; }

    /// <summary>The number of gas IDs with a positive amount.</summary>
    int ActiveGasCount { get; }

    /// <summary>Gets one gas amount, returning zero when the gas is absent.</summary>
    float GetMoles(int gasId);

    /// <summary>Sets one gas amount without changing the stored temperature.</summary>
    void SetMoles(int gasId, float moles);

    /// <summary>Adjusts one gas amount, clamping the result to zero, without changing the stored temperature.</summary>
    void AdjustMoles(int gasId, float deltaMoles);

    /// <summary>Adds gas and mixes its sensible internal energy into this mixture.</summary>
    void AddGas(int gasId, float moles, float temperature);

    /// <summary>Removes every gas while retaining this mixture's volume and temperature.</summary>
    void Clear();

    /// <summary>Removes up to the requested total amount in the mixture's current proportions.</summary>
    GasMixture Remove(float moles);

    /// <summary>Removes a fraction of every gas in the mixture.</summary>
    GasMixture RemoveRatio(float ratio);

    /// <summary>Removes the fraction of gas corresponding to a fraction of this mixture's volume.</summary>
    GasMixture RemoveVolume(float volume);

    /// <summary>Transfers up to the requested total amount to another mixture owned by the same simulation.</summary>
    /// <returns>The amount actually transferred, in moles (mol).</returns>
    float TransferTo(IGasMixture destination, float moles);

    /// <summary>Transfers a fraction of every gas to another mixture owned by the same simulation.</summary>
    /// <returns>The amount actually transferred, in moles (mol).</returns>
    float TransferRatioTo(IGasMixture destination, float ratio);

    /// <summary>Creates an owned, detached copy that remains associated with the same simulation.</summary>
    GasMixture Clone();

    /// <summary>Captures a detached, deterministic snapshot of this mixture.</summary>
    GasMixtureSnapshot GetSnapshot();
}

/// <summary>A gas ID and its positive amount in a mixture snapshot.</summary>
/// <param name="GasId">Simulation gas ID.</param>
/// <param name="Moles">Amount in moles (mol).</param>
public readonly record struct GasMixtureGas(int GasId, float Moles);

/// <summary>Detached scalar and composition values captured from an <see cref="IGasMixture" />.</summary>
/// <param name="Volume">Volume in cubic metres (m³).</param>
/// <param name="Temperature">Stored temperature in kelvins (K).</param>
/// <param name="Pressure">Ideal-gas pressure in pascals (Pa).</param>
/// <param name="TotalMoles">Total amount in moles (mol).</param>
/// <param name="Gases">Positive gas amounts ordered by gas ID.</param>
public readonly record struct GasMixtureSnapshot(
    float Volume,
    float Temperature,
    float Pressure,
    float TotalMoles,
    GasMixtureGas[] Gases)
{
    /// <summary>Gets one captured gas amount, returning zero when the gas is absent.</summary>
    public float GetMoles(int gasId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gasId);
        var gases = Gases ?? [];
        for (var index = 0; index < gases.Length; index++)
        {
            if (gases[index].GasId == gasId)
                return gases[index].Moles;
            if (gases[index].GasId > gasId)
                break;
        }

        return 0f;
    }
}

internal interface IInternalGasMixture : IGasMixture
{
}
