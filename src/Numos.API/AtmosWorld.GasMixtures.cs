using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;

namespace Numos.API;

public sealed partial class AtmosWorld
{
    /// <summary>
    ///     Serializes every <see cref="IGasMixture" /> operation against restore, disposal, and other mixture
    ///     operations.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every path takes these gates in one order, and it can skip any of them:
    ///     </para>
    ///     <list type="number">
    ///         <item><see cref="Gate" />.</item>
    ///         <item>This gate.</item>
    ///         <item>The <see cref="AtmosSimulation.MixtureGate" /> of each simulation, in ascending ID order.</item>
    ///         <item>The kernel state gate of each simulation, in the same order.</item>
    ///         <item>The pool state gate.</item>
    ///         <item>The recording gate.</item>
    ///     </list>
    ///     <para>
    ///         A mixture operation never takes <see cref="Gate" />. Restore, replay, world disposal, and
    ///         <see cref="GasMixture.Dispose" /> take it and then this gate, so no mixture operation interleaves with
    ///         them. Releasing a mixture destroys its link sets, which only <see cref="Gate" /> guards.
    ///     </para>
    ///     <para>
    ///         A configuration change holds this gate while it applies and records the new configuration. A mixture
    ///         operation then sees one configuration from start to end.
    ///     </para>
    ///     <para>
    ///         A world tick takes every gate from <see cref="Gate" /> to the pool state gate before the solvers run. A
    ///         solver callback runs on the tick thread, so its mixture calls re-enter them. A worker thread that a
    ///         callback starts must not call <see cref="IGasMixture" /> members, because the tick thread holds the gates
    ///         that the call needs.
    ///     </para>
    ///     <para>
    ///         A simulation's lifecycle members take <see cref="Gate" /> before their own gate, and never call into
    ///         the world while they hold only their own gate.
    ///     </para>
    /// </remarks>
    private readonly object _mixtureGate = new();

    private AtmosConfigSnapshot MixtureConfig => Volatile.Read(ref _config);

    /// <summary>
    ///     Creates an empty gas mixture in the world's mixture pool.
    /// </summary>
    /// <param name="volume">Container volume in cubic metres (m³).</param>
    /// <param name="temperature">Initial temperature in kelvins (K).</param>
    /// <returns>A new empty <see cref="GasMixture" />. The caller disposes it.</returns>
    /// <remarks>
    ///     The mixture lives outside every chunk grid, but the world ticks it with the grid (phase change and
    ///     reactions). Numos records the creation, checkpoints the mixture, and includes it in state hashes. A mixture
    ///     that is never disposed stays allocated until the world is disposed or a restore drops it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The volume is not positive and finite, or the temperature is negative or non-finite.
    /// </exception>
    /// <exception cref="InvalidOperationException">Called from a solver callback while a tick is executing.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public GasMixture CreateGasMixture(
        float volume,
        float temperature = AtmosPhysicalConstants.RoomTemperature)
    {
        ValidateVolume(volume);
        ValidateTemperature(temperature);
        using var locks = EnterMixtureLocks(null, null, true);
        return AllocateMixture(volume, temperature);
    }

    /// <summary>
    ///     Gets a new handle to a live mixture from its identifier.
    /// </summary>
    /// <param name="id">An identifier from <see cref="GasMixture.Id" /> or <see cref="GetGasMixtureIds" />.</param>
    /// <param name="mixture">
    ///     A new handle when the pool holds that allocation, otherwise <see langword="null" />.
    /// </param>
    /// <returns><see langword="true" /> when the slot holds a mixture with the identifier's generation.</returns>
    /// <remarks>
    ///     Use this after a restore, a load, or a replay, when the host's old handle is disposed or belongs to another
    ///     world. The result is a new object. It is independent of any earlier handle, so an earlier disposed handle
    ///     stays disposed. The call records nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool TryGetGasMixture(GasMixtureId id, [NotNullWhen(true)] out GasMixture? mixture)
    {
        // Takes the pool gate like an allocation does, but allocates nothing.
        using var locks = EnterMixtureLocks(null, null, true);
        mixture = GasMixtures.IsLive(id.Slot, id.Generation) ? new GasMixture(this, id.Slot, id.Generation) : null;
        return mixture != null;
    }

    /// <summary>
    ///     Lists the identifiers of every live mixture in slot order.
    /// </summary>
    /// <returns>A new array, empty when no mixture is live.</returns>
    /// <remarks>
    ///     A host that loads a replay or restores into a fresh world can find its mixtures here. Pair the identifiers
    ///     with saved <see cref="GasMixture.Id" /> values, then call <see cref="TryGetGasMixture" />.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public GasMixtureId[] GetGasMixtureIds()
    {
        using var locks = EnterMixtureLocks(null, null, true);
        var slots = GasMixtures.GetAllocatedSlots();
        var ids = new GasMixtureId[slots.Length];
        for (int index = 0; index < slots.Length; index++)
            ids[index] = new GasMixtureId(slots[index].Slot, slots[index].Generation);

        return ids;
    }

    /// <summary>
    ///     Frees a mixture's slot, destroys the link sets that use it, and records the release.
    /// </summary>
    /// <remarks>
    ///     This takes <see cref="Gate" /> first, because link sets belong to the world gate. The order
    ///     <see cref="Gate" />, <see cref="_mixtureGate" />, pool gate is the documented one, and no mixture operation
    ///     calls this method while it holds <see cref="_mixtureGate" />. The destruction is immediate bookkeeping, like
    ///     a chunk removal. The links leave the solver at the next tick boundary.
    /// </remarks>
    internal void ReleaseMixture(GasMixture mixture)
    {
        lock (Gate)
        {
            lock (_mixtureGate)
            {
                if (mixture.IsDisposed)
                    return;

                if (IsDisposed)
                {
                    mixture.IsDisposed = true;
                    return;
                }

                lock (GasMixtures.StateGate)
                {
                    if (GasMixtures.IsLive(mixture.Slot, mixture.Generation))
                    {
                        GasMixtures.Release(mixture.Slot, mixture.Generation);
                        InvalidateLinksForMixture(mixture.Slot, mixture.Generation);
                        if (ShouldRecordMixtureOperation())
                            RecordWorldOperation(new ReleaseGasMixtureOperation(mixture.Slot, mixture.Generation));
                    }

                    mixture.IsDisposed = true;
                }
            }
        }
    }

    internal AtmosCellRef GetMixtureCellRef(GasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        GasMixtures.Resolve(mixture.Slot, mixture.Generation);
        return AtmosCellRef.ForGasMixtureSlot(mixture.Slot, mixture.Generation);
    }

    internal float GetMixtureMoles(IInternalGasMixture mixture, string gasName)
    {
        using var locks = EnterMixtureLocks(mixture);
        return GetMixtureMoles(mixture, ResolveGasName(gasName));
    }

    internal void SetMixtureMoles(IInternalGasMixture mixture, string gasName, float moles)
    {
        using var locks = EnterMixtureLocks(mixture);
        SetMixtureMoles(mixture, ResolveGasName(gasName), moles);
    }

    internal void AdjustMixtureMoles(IInternalGasMixture mixture, string gasName, float deltaMoles)
    {
        using var locks = EnterMixtureLocks(mixture);
        AdjustMixtureMoles(mixture, ResolveGasName(gasName), deltaMoles);
    }

    internal void AddGasToMixture(IInternalGasMixture mixture, string gasName, float moles, float temperature)
    {
        using var locks = EnterMixtureLocks(mixture);
        AddGasToMixture(mixture, ResolveGasName(gasName), moles, temperature);
    }

    internal float GetMixtureVolume(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        return mixture switch
        {
            GasMixture pooled => GasMixtures.GetVolume(pooled.Slot, pooled.Generation),
            VoxelGasMixture voxel => voxel.Simulation.Kernel.GetVoxelMixtureVolume(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex),
            _ => throw CreateUnsupportedMixtureException(nameof(mixture))
        };
    }

    internal float GetMixtureTemperature(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        return mixture switch
        {
            GasMixture pooled => GasMixtures.GetTemperature(pooled.Slot, pooled.Generation),
            VoxelGasMixture voxel => voxel.Simulation.Kernel.GetVoxelMixtureTemperature(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex),
            _ => throw CreateUnsupportedMixtureException(nameof(mixture))
        };
    }

    internal float GetMixturePressure(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        return mixture switch
        {
            GasMixture pooled => GasMixtures.GetPressure(pooled.Slot, pooled.Generation),
            VoxelGasMixture voxel => voxel.Simulation.Kernel.GetVoxelMixturePressure(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex),
            _ => throw CreateUnsupportedMixtureException(nameof(mixture))
        };
    }

    internal float GetMixtureTotalMoles(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        return mixture switch
        {
            GasMixture pooled => GasMixtures.GetTotalMoles(pooled.Slot, pooled.Generation),
            VoxelGasMixture voxel => voxel.Simulation.Kernel.GetVoxelMixtureTotalMoles(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex),
            _ => throw CreateUnsupportedMixtureException(nameof(mixture))
        };
    }

    internal int GetMixtureActiveGasCount(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        return mixture switch
        {
            GasMixture pooled => GasMixtures.GetActiveGasCount(pooled.Slot, pooled.Generation),
            VoxelGasMixture voxel => voxel.Simulation.Kernel.GetVoxelMixtureActiveGasCount(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex),
            _ => throw CreateUnsupportedMixtureException(nameof(mixture))
        };
    }

    internal float GetMixtureMoles(IInternalGasMixture mixture, int gasId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gasId);
        using var locks = EnterMixtureLocks(mixture);
        return mixture switch
        {
            GasMixture pooled => GasMixtures.GetMoles(pooled.Slot, pooled.Generation, gasId),
            VoxelGasMixture voxel => voxel.Simulation.Kernel.GetVoxelMixtureMoles(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                gasId),
            _ => throw CreateUnsupportedMixtureException(nameof(mixture))
        };
    }

    internal void SetMixtureVolume(GasMixture mixture, float volume)
    {
        ValidateVolume(volume);
        using var locks = EnterMixtureLocks(mixture);
        GasMixtures.SetVolume(mixture.Slot, mixture.Generation, volume);
        if (ShouldRecordMixtureOperation())
            RecordWorldOperation(new SetGasMixtureVolumeOperation(mixture.Slot, mixture.Generation, volume));
    }

    internal void SetMixtureTemperature(IInternalGasMixture mixture, float temperature)
    {
        using var locks = EnterMixtureLocks(mixture);
        if (mixture is GasMixture pooled)
        {
            GasMixtures.SetTemperature(pooled.Slot, pooled.Generation, temperature);
            RecordPooledContent(pooled);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            voxel.Simulation.Kernel.SetVoxelMixtureTemperature(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                temperature);

            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    internal void SetMixtureMoles(IInternalGasMixture mixture, int gasId, float moles)
    {
        ValidateNonnegativeFinite(moles, nameof(moles));
        using var locks = EnterMixtureLocks(mixture);
        ValidateGasId(gasId);
        if (mixture is GasMixture pooled)
        {
            GasMixtures.SetMoles(pooled.Slot, pooled.Generation, gasId, moles);
            RecordPooledContent(pooled);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            voxel.Simulation.Kernel.SetVoxelMixtureMoles(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                gasId,
                moles);

            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    internal void AdjustMixtureMoles(IInternalGasMixture mixture, int gasId, float deltaMoles)
    {
        if (!float.IsFinite(deltaMoles))
            throw new ArgumentOutOfRangeException(nameof(deltaMoles), deltaMoles, "Mole adjustment must be finite.");

        using var locks = EnterMixtureLocks(mixture);
        ValidateGasId(gasId);
        if (mixture is GasMixture pooled)
        {
            GasMixtures.AdjustMoles(pooled.Slot, pooled.Generation, gasId, deltaMoles);
            RecordPooledContent(pooled);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            voxel.Simulation.Kernel.AdjustVoxelMixtureMoles(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                gasId,
                deltaMoles);

            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    internal void AddGasToMixture(IInternalGasMixture mixture, int gasId, float moles, float temperature)
    {
        ValidatePositiveFinite(moles, nameof(moles));
        ValidateTemperature(temperature);

        using var locks = EnterMixtureLocks(mixture);
        ValidateGasId(gasId);
        if (mixture is GasMixture pooled)
        {
            GasMixtures.AddGas(pooled.Slot, pooled.Generation, gasId, moles, temperature);
            RecordPooledContent(pooled);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            voxel.Simulation.Kernel.AddVoxelMixtureGas(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                gasId,
                moles,
                temperature);

            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    internal void ClearMixture(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        if (mixture is GasMixture pooled)
        {
            GasMixtures.Clear(pooled.Slot, pooled.Generation);
            RecordPooledContent(pooled);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            voxel.Simulation.Kernel.ClearVoxelMixture(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex);

            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    internal GasMixture RemoveFromMixture(IInternalGasMixture mixture, float moles)
    {
        ValidateNonnegativeFinite(moles, nameof(moles));
        using var locks = EnterMixtureLocks(mixture, null, true);
        var source = CaptureMixtureState(mixture);
        float totalMoles = source.CapturedTotalMoles;
        float ratio = totalMoles > 0f ? MathF.Min(1f, moles / totalMoles) : 0f;
        return RemoveRatioCore(mixture, source, ratio);
    }

    internal GasMixture RemoveRatioFromMixture(IInternalGasMixture mixture, float ratio)
    {
        ValidateFinite(ratio, nameof(ratio));
        using var locks = EnterMixtureLocks(mixture, null, true);
        return RemoveRatioCore(mixture, CaptureMixtureState(mixture), Math.Clamp(ratio, 0f, 1f));
    }

    internal GasMixture RemoveVolumeFromMixture(IInternalGasMixture mixture, float volume)
    {
        ValidateNonnegativeFinite(volume, nameof(volume));
        using var locks = EnterMixtureLocks(mixture, null, true);
        var source = CaptureMixtureState(mixture);
        float ratio = Math.Clamp(volume / source.Volume, 0f, 1f);
        return RemoveRatioCore(mixture, source, ratio);
    }

    internal float TransferMixture(IInternalGasMixture source, IGasMixture destination, float moles)
    {
        ValidateNonnegativeFinite(moles, nameof(moles));
        var internalDestination = GetOwnedMixture(destination, nameof(destination));
        return ExecuteTransfer(
            source,
            internalDestination,
            state =>
            {
                float totalMoles = state.CapturedTotalMoles;
                return totalMoles > 0f ? MathF.Min(1f, moles / totalMoles) : 0f;
            });
    }

    internal float TransferMixtureRatio(IInternalGasMixture source, IGasMixture destination, float ratio)
    {
        ValidateFinite(ratio, nameof(ratio));
        var internalDestination = GetOwnedMixture(destination, nameof(destination));
        return ExecuteTransfer(source, internalDestination, _ => Math.Clamp(ratio, 0f, 1f));
    }

    internal GasMixture CloneMixture(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture, null, true);
        var state = CaptureMixtureState(mixture);
        return CreateMixtureWithContent(state.Volume, state.Temperature, state.ToGasArray());
    }

    internal GasMixtureSnapshot GetMixtureSnapshot(IInternalGasMixture mixture)
    {
        using var locks = EnterMixtureLocks(mixture);
        var state = CaptureMixtureState(mixture);
        var gases = new GasMixtureGas[state.ActiveGasCount];
        int index = 0;
        foreach ((int gasId, float moles) in state.Moles)
            gases[index++] = new GasMixtureGas(gasId, moles);

        // The totals the mixture's own members report, so a snapshot agrees with them.
        return new GasMixtureSnapshot(
            state.Volume,
            state.Temperature,
            state.CapturedPressure,
            state.CapturedTotalMoles,
            gases);
    }

    internal int ResolveGasName(string gasName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(gasName);
        return MixtureConfig.GasRegistry.GasIdToIndex(gasName);
    }

    internal void Apply(CreateGasMixtureOperation create)
    {
        var (slot, generation) = GasMixtures.Allocate(create.Volume, create.Temperature);
        if (slot != create.Slot || generation != create.Generation)
            throw new InvalidOperationException("Gas mixture allocation diverged from recorded history.");
    }

    internal void Apply(ReleaseGasMixtureOperation release)
    {
        GasMixtures.Release(release.Slot, release.Generation);
        InvalidateLinksForMixture(release.Slot, release.Generation);
    }

    internal void Apply(SetGasMixtureVolumeOperation volume)
    {
        GasMixtures.SetVolume(volume.Slot, volume.Generation, volume.Volume);
    }

    internal void Apply(SetGasMixtureOperation content)
    {
        GasMixtures.ApplyContent(
            content.Slot,
            content.Generation,
            content.Temperature,
            content.Pressure,
            content.HeatCapacity,
            content.Gases);
    }

    private GasMixture RemoveRatioCore(IInternalGasMixture mixture, GasMixtureState source, float ratio)
    {
        if (ratio <= 0f || source.ActiveGasCount == 0)
            return CreateMixtureWithContent(source.Volume, source.Temperature, []);

        ValidateMixtureCanMutate(mixture);
        var removed = RemoveRatioFromState(source, ratio);
        ValidateState(source);
        ValidateState(removed);

        // The new mixture comes first, so a recording lists it before the change to the source.
        var result = CreateMixtureWithContent(removed.Volume, removed.Temperature, removed.ToGasArray());
        ApplyMixtureState(mixture, source);
        return result;
    }

    private float ExecuteTransfer(
        IInternalGasMixture source,
        IInternalGasMixture destination,
        Func<GasMixtureState, float> getRatio)
    {
        using var locks = EnterMixtureLocks(source, destination);

        // The capture throws for a stale handle, so a transfer to the same mixture fails like any other call.
        var sourceState = CaptureMixtureState(source);
        if (IsSameMixture(source, destination))
            return 0f;

        float ratio = getRatio(sourceState);
        if (ratio <= 0f || sourceState.ActiveGasCount == 0)
            return 0f;

        ValidateMixtureCanMutate(source);
        ValidateMixtureCanMutate(destination);
        var destinationState = CaptureMixtureState(destination);
        var removed = RemoveRatioFromState(sourceState, ratio);
        MergeStates(destinationState, removed);
        ValidateState(sourceState);
        ValidateState(destinationState);

        ApplyMixtureState(source, sourceState);
        ApplyMixtureState(destination, destinationState);
        return removed.TotalMoles;
    }

    /// <summary>
    ///     Takes every lock a mixture operation needs, in the order that <see cref="_mixtureGate" /> documents.
    /// </summary>
    /// <param name="first">The first mixture the operation touches, or <see langword="null" /> for none.</param>
    /// <param name="second">The second mixture the operation touches, if any.</param>
    /// <param name="allocates">Whether the operation allocates a pooled mixture even if no endpoint is pooled.</param>
    /// <exception cref="ObjectDisposedException">
    ///     The world, a touched simulation, or a touched <see cref="GasMixture" /> handle has been disposed.
    /// </exception>
    private MixtureLocks EnterMixtureLocks(
        IInternalGasMixture? first,
        IInternalGasMixture? second = null,
        bool allocates = false)
    {
        var locks = default(MixtureLocks);
        try
        {
            locks.Enter(_mixtureGate);
            ThrowIfDisposed();
            ThrowIfHandleDisposed(first);
            ThrowIfHandleDisposed(second);

            var firstSimulation = (first as VoxelGasMixture)?.Simulation;
            var secondSimulation = (second as VoxelGasMixture)?.Simulation;
            if (ReferenceEquals(firstSimulation, secondSimulation))
                secondSimulation = null;

            if (firstSimulation == null)
            {
                firstSimulation = secondSimulation;
                secondSimulation = null;
            }
            else if (secondSimulation != null && secondSimulation.Id.CompareTo(firstSimulation.Id) < 0)
            {
                (firstSimulation, secondSimulation) = (secondSimulation, firstSimulation);
            }

            if (firstSimulation != null)
                locks.Enter(firstSimulation.MixtureGate);

            if (secondSimulation != null)
                locks.Enter(secondSimulation.MixtureGate);

            // The Kernel accessor throws for a disposed simulation.
            var firstKernel = firstSimulation?.Kernel;
            var secondKernel = secondSimulation?.Kernel;
            if (firstKernel != null)
                locks.Enter(firstKernel.StateGate);

            if (secondKernel != null)
                locks.Enter(secondKernel.StateGate);

            if (allocates || first is GasMixture || second is GasMixture)
                locks.Enter(GasMixtures.StateGate);

            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    private static void ThrowIfHandleDisposed(IInternalGasMixture? mixture)
    {
        if (mixture is GasMixture { IsDisposed: true })
            throw new ObjectDisposedException(nameof(GasMixture));
    }

    /// <summary>
    ///     Allocates a pooled mixture and records the allocation. The caller holds the pool gate.
    /// </summary>
    private GasMixture AllocateMixture(float volume, float temperature)
    {
        var (slot, generation) = GasMixtures.Allocate(volume, temperature);
        if (ShouldRecordMixtureOperation())
            RecordWorldOperation(new CreateGasMixtureOperation(slot, generation, volume, temperature));

        return new GasMixture(this, slot, generation);
    }

    /// <summary>
    ///     Allocates a pooled mixture and fills it. A stored temperature that allocation rejects, such as NaN, is
    ///     written through the content record instead.
    /// </summary>
    private GasMixture CreateMixtureWithContent(float volume, float temperature, KeyValuePair<int, float>[] gases)
    {
        bool allocatable = float.IsFinite(temperature) && temperature >= 0f;
        var mixture = AllocateMixture(volume, allocatable ? temperature : 0f);
        if (gases.Length > 0 || !allocatable)
        {
            GasMixtures.Replace(mixture.Slot, mixture.Generation, temperature, gases);
            RecordPooledContent(mixture);
        }

        return mixture;
    }

    /// <summary>
    ///     Records the final content of a pooled mixture after a mutation. The caller holds the pool gate.
    /// </summary>
    private void RecordPooledContent(GasMixture mixture)
    {
        if (ShouldRecordMixtureOperation())
        {
            RecordWorldOperation(
                new SetGasMixtureOperation(
                    mixture.Slot,
                    mixture.Generation,
                    GasMixtures.CaptureContent(mixture.Slot, mixture.Generation)));
        }
    }

    /// <summary>
    ///     Tells whether a pool mutation is an external cause. A mutation from a solver is derived work, and replay
    ///     reproduces it with the tick. The caller holds the pool gate, so the pool's own tick flag is exact.
    /// </summary>
    private bool ShouldRecordMixtureOperation()
    {
        if (GasMixtures.IsTickExecuting)
            return false;

        lock (_recordingGate)
        {
            return _isWorldRecording && !_isApplyingWorldOperation;
        }
    }

    private GasMixtureState CaptureMixtureState(IInternalGasMixture mixture)
    {
        VoxelGasMixtureState captured;
        if (mixture is GasMixture pooled)
        {
            captured = GasMixtures.Capture(pooled.Slot, pooled.Generation);
        }
        else if (mixture is VoxelGasMixture voxel)
        {
            captured = voxel.Simulation.Kernel.CaptureVoxelMixture(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex);
        }
        else
        {
            throw CreateUnsupportedMixtureException(nameof(mixture));
        }

        var state = new GasMixtureState(captured.Volume, captured.Temperature)
        {
            CapturedTotalMoles = captured.TotalMoles,
            CapturedPressure = captured.Pressure
        };

        foreach ((int gasId, float moles) in captured.Gases)
            state.Moles.Add(gasId, moles);

        return state;
    }

    private void ApplyMixtureState(IInternalGasMixture mixture, GasMixtureState state)
    {
        if (mixture is GasMixture pooled)
        {
            GasMixtures.Replace(pooled.Slot, pooled.Generation, state.Temperature, state.ToGasArray());
            RecordPooledContent(pooled);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            voxel.Simulation.Kernel.ReplaceVoxelMixture(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                state.Temperature,
                state.ToGasArray());

            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    private static void ValidateMixtureCanMutate(IInternalGasMixture mixture)
    {
        if (mixture is not VoxelGasMixture voxel)
            return;

        voxel.Simulation.Kernel.ValidateVoxelMixtureMutations(
        [
            new VoxelGasMixtureAddress(voxel.ChunkPosition, voxel.ChunkGeneration, voxel.LocalVoxelIndex)
        ]);
    }

    private void MergeStates(GasMixtureState destination, GasMixtureState incoming)
    {
        if (incoming.ActiveGasCount == 0)
            return;

        float destinationHeatCapacity = CalculateMixtureHeatCapacity(destination);
        float incomingHeatCapacity = CalculateMixtureHeatCapacity(incoming);
        float combinedHeatCapacity = destinationHeatCapacity + incomingHeatCapacity;
        if (!float.IsFinite(combinedHeatCapacity))
            throw new InvalidOperationException("The merged mixture heat capacity exceeds the supported range.");

        float destinationTemperature = MixtureConfig.GetValidatedTemp(destination.Temperature);
        float incomingTemperature = MixtureConfig.GetValidatedTemp(incoming.Temperature);
        float mixedTemperature = combinedHeatCapacity > 0f
            ? destinationTemperature +
              (incomingTemperature - destinationTemperature) * incomingHeatCapacity / combinedHeatCapacity
            : incoming.Temperature;

        foreach ((int gasId, float incomingMoles) in incoming.Moles)
        {
            float combinedMoles = destination.Moles.GetValueOrDefault(gasId) + incomingMoles;
            if (!float.IsFinite(combinedMoles))
                throw new InvalidOperationException("A merged gas amount exceeds the supported range.");

            destination.Moles[gasId] = combinedMoles;
        }

        if (!float.IsFinite(mixedTemperature) || mixedTemperature < 0f)
            throw new InvalidOperationException("The merged mixture temperature is outside the supported range.");

        destination.Temperature = mixedTemperature;
    }

    private float CalculateMixtureHeatCapacity(GasMixtureState state)
    {
        float total = 0f;
        foreach ((int gasId, float moles) in state.Moles)
            total += moles * MixtureConfig.GetMolarHeatCapacityAtConstantVolume(gasId);

        if (!float.IsFinite(total))
            throw new InvalidOperationException("The mixture's heat capacity exceeds the supported range.");

        return total;
    }

    // Matches the pool: moles * validated temperature * (R / V).
    private float CalculateMixturePressure(GasMixtureState state)
    {
        float totalMoles = state.TotalMoles;
        if (totalMoles <= 0f)
            return 0f;

        return totalMoles *
               MixtureConfig.GetValidatedTemp(state.Temperature) *
               (AtmosPhysicalConstants.MolarGasConstant / state.Volume);
    }

    private static GasMixtureState RemoveRatioFromState(GasMixtureState source, float ratio)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        var removed = new GasMixtureState(source.Volume, source.Temperature);
        foreach ((int gasId, float sourceMoles) in source.Moles.ToArray())
        {
            float removedMoles = sourceMoles * ratio;
            float remainingMoles = sourceMoles - removedMoles;
            if (removedMoles > 0f)
                removed.Moles.Add(gasId, removedMoles);

            SetStateMoles(source, gasId, remainingMoles);
        }

        return removed;
    }

    private static void SetStateMoles(GasMixtureState state, int gasId, float moles)
    {
        if (moles > 0f)
            state.Moles[gasId] = moles;
        else
            state.Moles.Remove(gasId);
    }

    private void ValidateState(GasMixtureState state)
    {
        ValidateVolume(state.Volume);
        float total = 0f;
        foreach ((int gasId, float moles) in state.Moles)
        {
            ValidateGasId(gasId);
            ValidatePositiveFinite(moles, nameof(state));
            total += moles;
        }

        if (!float.IsFinite(total))
            throw new InvalidOperationException("The mixture's total moles exceed the supported range.");

        CalculateMixtureHeatCapacity(state);
        if (!float.IsFinite(CalculateMixturePressure(state)))
            throw new InvalidOperationException("The mixture's pressure exceeds the supported range.");
    }

    private IInternalGasMixture GetOwnedMixture(IGasMixture mixture, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(mixture, parameterName);
        if (mixture is not IInternalGasMixture internalMixture)
            throw CreateUnsupportedMixtureException(parameterName);

        if (!ReferenceEquals(internalMixture.World, this))
        {
            throw new ArgumentException(
                "Gas mixtures must belong to the same AtmosWorld.",
                parameterName);
        }

        return internalMixture;
    }

    private static bool IsSameMixture(IInternalGasMixture left, IInternalGasMixture right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is GasMixture leftPooled && right is GasMixture rightPooled)
            return leftPooled.Slot == rightPooled.Slot && leftPooled.Generation == rightPooled.Generation;

        return left is VoxelGasMixture leftVoxel &&
               right is VoxelGasMixture rightVoxel &&
               ReferenceEquals(leftVoxel.Simulation, rightVoxel.Simulation) &&
               leftVoxel.ChunkPosition == rightVoxel.ChunkPosition &&
               leftVoxel.ChunkGeneration == rightVoxel.ChunkGeneration &&
               leftVoxel.LocalVoxelIndex == rightVoxel.LocalVoxelIndex;
    }

    private static ArgumentException CreateUnsupportedMixtureException(string parameterName)
    {
        return new ArgumentException(
            "Only GasMixture instances and voxel mixtures created by Numos are supported.",
            parameterName);
    }

    private void ValidateGasId(int gasId)
    {
        if ((uint)gasId >= (uint)MixtureConfig.GasRegistry.Count)
            throw new ArgumentOutOfRangeException(nameof(gasId), gasId, "Gas ID must refer to a registered gas.");
    }

    private static void ValidateVolume(float volume)
    {
        if (!float.IsFinite(volume) || volume <= 0f)
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be positive and finite.");
    }

    private static void ValidateTemperature(float temperature)
    {
        if (!float.IsFinite(temperature) || temperature < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperature),
                temperature,
                "Temperature must be nonnegative and finite.");
        }
    }

    private static void ValidatePositiveFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive and finite.");
    }

    private static void ValidateNonnegativeFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be nonnegative and finite.");
    }

    private static void ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be finite.");
    }

    [InlineArray(6)]
    private struct GateBuffer
    {
        private object? _gate;
    }

    /// <summary>
    ///     Holds the monitors of one mixture operation and exits them in reverse order. It allocates nothing.
    /// </summary>
    private ref struct MixtureLocks
    {
        private GateBuffer _gates;
        private int _count;

        internal void Enter(object gate)
        {
            Monitor.Enter(gate);
            _gates[_count++] = gate;
        }

        public void Dispose()
        {
            while (_count > 0)
            {
                Monitor.Exit(_gates[--_count]!);
                _gates[_count] = null;
            }
        }
    }
}
