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
    ///     The setter stores the raw value. Pressure and energy calculations use the owner's configured fallback
    ///     when the stored value is non-finite or nonpositive.
    /// </remarks>
    float Temperature { get; set; }

    /// <summary>The ideal-gas pressure, in pascals (Pa).</summary>
    float Pressure { get; }

    /// <summary>The total amount of gas, in moles (mol).</summary>
    float TotalMoles { get; }

    /// <summary>The number of gas IDs with a positive amount.</summary>
    int ActiveGasCount { get; }

    /// <summary>
    ///     Gets the amount of a registered gas, returning zero when absent.
    /// </summary>
    /// <param name="gasName">The exact, case-sensitive registered gas name.</param>
    /// <returns>The amount in moles.</returns>
    /// <exception cref="KeyNotFoundException">The gas name is not registered.</exception>
    /// <exception cref="ArgumentNullException">The gas name is null.</exception>
    /// <exception cref="ArgumentException">The gas name is empty.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    float GetMoles(string gasName);

    /// <summary>
    ///     Sets a registered gas amount without changing temperature.
    /// </summary>
    /// <param name="gasName">The exact, case-sensitive registered gas name.</param>
    /// <param name="moles">The nonnegative, finite amount to store, in moles.</param>
    /// <exception cref="KeyNotFoundException">The gas name is not registered.</exception>
    /// <exception cref="ArgumentNullException">The gas name is null.</exception>
    /// <exception cref="ArgumentException">The gas name is empty.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative or non-finite.</exception>
    void SetMoles(string gasName, float moles);

    /// <summary>
    ///     Adjusts a registered gas amount without changing temperature.
    /// </summary>
    /// <param name="gasName">The exact, case-sensitive registered gas name.</param>
    /// <param name="deltaMoles">The finite adjustment in moles; the result is clamped to zero.</param>
    /// <exception cref="KeyNotFoundException">The gas name is not registered.</exception>
    /// <exception cref="ArgumentNullException">The gas name is null.</exception>
    /// <exception cref="ArgumentException">The gas name is empty.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The adjustment is non-finite.</exception>
    void AdjustMoles(string gasName, float deltaMoles);

    /// <summary>
    ///     Adds a registered gas and mixes its sensible internal energy.
    /// </summary>
    /// <param name="gasName">The exact, case-sensitive registered gas name.</param>
    /// <param name="moles">The positive, finite amount to add, in moles.</param>
    /// <param name="temperature">The nonnegative, finite incoming temperature, in kelvins.</param>
    /// <exception cref="KeyNotFoundException">The gas name is not registered.</exception>
    /// <exception cref="ArgumentNullException">The gas name is null.</exception>
    /// <exception cref="ArgumentException">The gas name is empty.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An amount or temperature is invalid.</exception>
    /// <example>
    ///     After registering Oxygen, call <c>mixture.AddGas("Oxygen", 2f, 293.15f)</c> to add two moles.
    /// </example>
    void AddGas(string gasName, float moles, float temperature);

    /// <summary>Gets one gas amount, returning zero when the gas is absent.</summary>
    float GetMoles(int gasId);

    /// <summary>
    ///     Sets one registered gas amount without changing the stored temperature.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The gas ID is not registered or the amount is invalid.</exception>
    void SetMoles(int gasId, float moles);

    /// <summary>
    ///     Adjusts one registered gas amount, clamping the result to zero, without changing the stored temperature.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The gas ID is not registered or the adjustment is invalid.</exception>
    void AdjustMoles(int gasId, float deltaMoles);

    /// <summary>
    ///     Adds a registered gas and mixes its sensible internal energy into this mixture.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The gas ID is not registered, or the amount or temperature is invalid.</exception>
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
        GasMixtureGas[] gases = Gases ?? [];
        for (int index = 0; index < gases.Length; index++)
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