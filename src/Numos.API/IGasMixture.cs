using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     A mutable, simulation-owned gas volume.
/// </summary>
/// <remarks>
///     <para>
///         Implementations either own detached container storage (<see cref="GasMixture" />) or address a live voxel
///         (<see cref="AtmosSimulation.GetVoxelGasMixture(AtmosChunkHandle, ushort)" />). Members never expose the
///         solver's backing arrays or spans. Every live-voxel operation takes the simulation's state lock, so it
///         waits for an in-progress tick instead of racing it. This is a common capability surface, not an
///         extension point: transfer endpoints must be instances created by <see cref="AtmosSimulation" />.
///     </para>
///     <para>
///         Mutating a live voxel is recorded as an external operation and survives replay. A standalone
///         <see cref="GasMixture" /> is not simulation state: it is never recorded or checkpointed, so a transfer
///         between a voxel and a container replays only the voxel's side.
///     </para>
///     <para>
///         A voxel mixture is bound to the chunk generation it was created from. Once that chunk is unregistered,
///         members throw <see cref="KeyNotFoundException" />; once a new chunk is registered at the same position,
///         they throw <see cref="InvalidOperationException" />. Mutations of solid or void voxels also throw
///         <see cref="InvalidOperationException" />.
///     </para>
/// </remarks>
[PublicAPI]
public interface IGasMixture
{
    /// <summary>
    ///     The simulation that owns this mixture and resolves its gas properties.
    /// </summary>
    AtmosSimulation Owner { get; }

    /// <summary>
    ///     The represented volume, in cubic metres (m³).
    /// </summary>
    /// <remarks>
    ///     For a voxel this is the owner's configured voxel volume.
    /// </remarks>
    float Volume { get; }

    /// <summary>
    ///     The stored temperature, in kelvins (K).
    /// </summary>
    /// <remarks>
    ///     The setter stores the raw value. Pressure and energy calculations use the owner's configured fallback
    ///     when the stored value is non-finite or nonpositive.
    /// </remarks>
    float Temperature { get; set; }

    /// <summary>
    ///     The ideal-gas pressure, in pascals (Pa), computed from <see cref="TotalMoles" />, <see cref="Volume" />,
    ///     and the effective temperature. Zero when the mixture is empty.
    /// </summary>
    float Pressure { get; }

    /// <summary>
    ///     The total amount of gas, in moles (mol).
    /// </summary>
    float TotalMoles { get; }

    /// <summary>
    ///     The number of gas IDs with a positive amount.
    /// </summary>
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

    /// <summary>
    ///     Gets one gas amount, returning zero when the gas is absent.
    /// </summary>
    /// <param name="gasId">The simulation gas ID. An unregistered nonnegative ID reads as zero.</param>
    /// <returns>The amount in moles.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gasId" /> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    float GetMoles(int gasId);

    /// <summary>
    ///     Sets one registered gas amount without changing the stored temperature.
    /// </summary>
    /// <param name="gasId">The registered simulation gas ID.</param>
    /// <param name="moles">The nonnegative, finite amount to store, in moles. Zero removes the gas.</param>
    /// <exception cref="ArgumentOutOfRangeException">The gas ID is not registered or the amount is invalid.</exception>
    /// <exception cref="InvalidOperationException">The resulting pressure or heat capacity is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    void SetMoles(int gasId, float moles);

    /// <summary>
    ///     Adjusts one registered gas amount, clamping the result to zero, without changing the stored temperature.
    /// </summary>
    /// <param name="gasId">The registered simulation gas ID.</param>
    /// <param name="deltaMoles">The finite signed adjustment, in moles.</param>
    /// <exception cref="ArgumentOutOfRangeException">The gas ID is not registered or the adjustment is invalid.</exception>
    /// <exception cref="InvalidOperationException">The adjusted amount, pressure, or heat capacity is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    void AdjustMoles(int gasId, float deltaMoles);

    /// <summary>
    ///     Adds a registered gas and mixes its sensible internal energy into this mixture.
    /// </summary>
    /// <param name="gasId">The registered simulation gas ID.</param>
    /// <param name="moles">The positive, finite amount to add, in moles.</param>
    /// <param name="temperature">The nonnegative, finite incoming temperature, in kelvins.</param>
    /// <remarks>
    ///     The new temperature is the heat-capacity-weighted average of the current and incoming temperatures, using
    ///     each gas's registered molar heat capacity at constant volume.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The gas ID is not registered, or the amount or temperature is invalid.</exception>
    /// <exception cref="InvalidOperationException">The merged amount, heat capacity, or pressure is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    void AddGas(int gasId, float moles, float temperature);

    /// <summary>
    ///     Removes every gas while retaining this mixture's volume and temperature.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    void Clear();

    /// <summary>
    ///     Removes up to the requested total amount in the mixture's current proportions.
    /// </summary>
    /// <param name="moles">The nonnegative, finite amount to remove, in moles. Amounts above
    /// <see cref="TotalMoles" /> remove everything.</param>
    /// <returns>
    ///     A new detached <see cref="GasMixture" /> holding the removed gas, with this mixture's volume and
    ///     temperature. It is empty when nothing was removed.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="moles" /> is negative or non-finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    GasMixture Remove(float moles);

    /// <summary>
    ///     Removes a fraction of every gas in the mixture.
    /// </summary>
    /// <param name="ratio">The finite fraction to remove, clamped to [0, 1].</param>
    /// <returns>
    ///     A new detached <see cref="GasMixture" /> holding the removed gas, with this mixture's volume and
    ///     temperature.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratio" /> is non-finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    GasMixture RemoveRatio(float ratio);

    /// <summary>
    ///     Removes the gas occupying <paramref name="volume" /> cubic metres of this mixture, that is, the fraction
    ///     <c>volume / Volume</c> of every gas.
    /// </summary>
    /// <param name="volume">The nonnegative, finite volume to remove, in cubic metres. Values at or above
    /// <see cref="Volume" /> remove everything.</param>
    /// <returns>
    ///     A new detached <see cref="GasMixture" /> holding the removed gas, with this mixture's volume and
    ///     temperature.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="volume" /> is negative or non-finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    GasMixture RemoveVolume(float volume);

    /// <summary>
    ///     Transfers up to the requested total amount to another mixture owned by the same simulation.
    /// </summary>
    /// <param name="destination">A <see cref="GasMixture" /> or voxel mixture owned by <see cref="Owner" />.</param>
    /// <param name="moles">The nonnegative, finite amount to move, in moles. Amounts above
    /// <see cref="TotalMoles" /> move everything.</param>
    /// <returns>The amount actually transferred, in moles (mol). Zero when the destination is this mixture.</returns>
    /// <remarks>
    ///     Gas leaves in this mixture's proportions and the destination temperature becomes the heat-capacity-weighted
    ///     average of both sides. Both sides are validated before either is written, so a failed transfer leaves
    ///     both unchanged.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="destination" /> was not created by <see cref="AtmosSimulation" /> or belongs to a different
    ///     simulation.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="moles" /> is negative or non-finite.</exception>
    /// <exception cref="InvalidOperationException">The merged amount, heat capacity, temperature, or pressure is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    float TransferTo(IGasMixture destination, float moles);

    /// <summary>
    ///     Transfers a fraction of every gas to another mixture owned by the same simulation.
    /// </summary>
    /// <param name="destination">A <see cref="GasMixture" /> or voxel mixture owned by <see cref="Owner" />.</param>
    /// <param name="ratio">The finite fraction to move, clamped to [0, 1].</param>
    /// <returns>The amount actually transferred, in moles (mol). Zero when the destination is this mixture.</returns>
    /// <remarks>
    ///     Mixing and validation behave as in <see cref="TransferTo" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="destination" /> was not created by <see cref="AtmosSimulation" /> or belongs to a different
    ///     simulation.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratio" /> is non-finite.</exception>
    /// <exception cref="InvalidOperationException">The merged amount, heat capacity, temperature, or pressure is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    float TransferRatioTo(IGasMixture destination, float ratio);

    /// <summary>
    ///     Creates an owned, detached copy that remains associated with the same simulation.
    /// </summary>
    /// <returns>
    ///     A new <see cref="GasMixture" /> with this mixture's volume, stored temperature, and gas amounts. Cloning a
    ///     voxel gives a container sized to the voxel volume.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    GasMixture Clone();

    /// <summary>
    ///     Captures a detached, deterministic snapshot of this mixture.
    /// </summary>
    /// <returns>The current values, with gases ordered by ID and only positive amounts included.</returns>
    /// <exception cref="ObjectDisposedException">The owning simulation has been disposed.</exception>
    GasMixtureSnapshot GetSnapshot();
}

/// <summary>
///     A gas ID and its positive amount in a mixture snapshot.
/// </summary>
/// <param name="GasId">Simulation gas ID.</param>
/// <param name="Moles">Amount in moles (mol).</param>
public readonly record struct GasMixtureGas(int GasId, float Moles);

/// <summary>
///     Detached scalar and composition values captured from an <see cref="IGasMixture" />.
/// </summary>
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
    /// <summary>
    ///     Gets one captured gas amount, returning zero when the gas is absent.
    /// </summary>
    /// <param name="gasId">The simulation gas ID.</param>
    /// <returns>The captured amount in moles.</returns>
    /// <remarks>
    ///     The lookup relies on <see cref="Gases" /> being sorted by ID, which snapshots from
    ///     <see cref="IGasMixture.GetSnapshot" /> always are. A null <see cref="Gases" /> array reads as empty.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gasId" /> is negative.</exception>
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