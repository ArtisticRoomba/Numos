using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     A standalone gas mixture that lives in its world's mixture pool, suitable for canisters and other portable or
///     arbitrary-volume containers.
/// </summary>
/// <remarks>
///     <para>
///         Instances are created through <see cref="AtmosWorld.CreateGasMixture" /> or by the <c>Remove</c> and
///         <c>Clone</c> members of <see cref="IGasMixture" />. A <see cref="GasMixture" /> is a handle to a pool slot.
///         The world ticks the slot with the grid (phase change and reactions), records every mutation, and includes
///         the slot in checkpoints and state hashes.
///     </para>
///     <para>
///         <see cref="Dispose" /> frees the slot. Numos has no finalizer, because finalizer timing is not
///         deterministic. A mixture that is never disposed stays allocated until the world is disposed or a restore
///         drops it.
///     </para>
///     <para>
///         Any member except <see cref="Dispose" /> and <see cref="Id" /> throws
///         <see cref="ObjectDisposedException" /> after the handle is disposed. A handle goes stale when its slot is
///         freed or reused, for example when a restore drops the mixture. A stale handle throws
///         <see cref="InvalidOperationException" />. It works again when a later restore or replay makes the same
///         slot and generation live.
///     </para>
///     <para>
///         A disposed handle stays disposed, also when a restore brings its mixture back. A handle also cannot reach
///         a mixture in another world, such as a world that loads a saved replay. Save <see cref="Id" /> instead, and
///         call <see cref="AtmosWorld.TryGetGasMixture" /> to get a new handle.
///         <see cref="AtmosWorld.GetGasMixtureIds" /> lists every live mixture. Several handles can name one mixture.
///         Disposing any of them frees the slot, and the others then go stale.
///     </para>
/// </remarks>
[PublicAPI]
public sealed class GasMixture : IInternalGasMixture, IDisposable
{
    internal GasMixture(AtmosWorld world, int slot, uint generation)
    {
        World = world;
        Slot = slot;
        Generation = generation;
    }

    /// <summary>
    ///     Gets the pool slot index.
    /// </summary>
    internal int Slot { get; }

    /// <summary>
    ///     Gets the generation that identifies this allocation of the slot.
    /// </summary>
    internal uint Generation { get; }

    /// <summary>
    ///     Gets whether <see cref="Dispose" /> ran. The world reads and writes it while holding its mixture gate.
    /// </summary>
    internal bool IsDisposed { get; set; }

    /// <inheritdoc />
    public AtmosWorld World { get; }

    /// <summary>
    ///     Gets the value-only identifier of the allocation that this handle names.
    /// </summary>
    /// <remarks>
    ///     The identifier never changes and never throws, also after <see cref="Dispose" />. It then names nothing.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     GasMixtureId saved = canister.Id;
    ///     // Later, possibly in a world that loaded a replay of this one:
    ///     if (world.TryGetGasMixture(saved, out GasMixture? restored))
    ///         restored.AddGas("Oxygen", 1f, 293.15f);
    ///     </code>
    /// </example>
    public GasMixtureId Id => new(Slot, Generation);

    /// <summary>
    ///     The address that explicit links use to connect this mixture to a voxel or to another mixture.
    /// </summary>
    /// <remarks>
    ///     Pass it to <see cref="AtmosWorld.CreatePortal" /> or <see cref="AtmosWorld.CreateLinks" />. Disposing the
    ///     mixture destroys every link set that uses it.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     using GasMixture canister = world.CreateGasMixture(0.1f);
    ///     AtmosPortalHandle valve = world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0));
    ///     </code>
    /// </example>
    /// <exception cref="ObjectDisposedException">The mixture or its world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The handle is stale.</exception>
    public AtmosCellRef CellRef => World.GetMixtureCellRef(this);

    /// <summary>
    ///     The container volume, in cubic metres (m³).
    /// </summary>
    /// <remarks>
    ///     Changing the volume keeps the stored gas and temperature, so pressure scales inversely with it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The assigned volume is not positive and finite.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The resulting pressure is not finite, or the handle is stale. The previous volume is kept.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The mixture or its world has been disposed.</exception>
    public float Volume
    {
        get => World.GetMixtureVolume(this);
        set => World.SetMixtureVolume(this, value);
    }

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

    /// <summary>
    ///     Frees the pool slot, records the release, and disposes the handle.
    /// </summary>
    /// <remarks>
    ///     A second call does nothing. Disposing after the world is disposed does nothing. Disposing a stale handle
    ///     marks it disposed and leaves the pool alone.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Called from a solver callback while a tick is executing.</exception>
    public void Dispose()
    {
        World.ReleaseMixture(this);
    }
}
