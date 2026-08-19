using JetBrains.Annotations;
using Numos.CoreSim;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    private readonly object _mixtureGate = new();

    /// <summary>Creates an empty, independently stored gas mixture owned by this simulation.</summary>
    /// <param name="volume">Container volume in cubic metres (m³).</param>
    /// <param name="temperature">Initial temperature in kelvins (K).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The volume is not positive and finite, or the temperature is negative or non-finite.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public GasMixture CreateGasMixture(
        float volume,
        float temperature = AtmosPhysicalConstants.RoomTemperature)
    {
        ValidateVolume(volume);
        ValidateTemperature(temperature);
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return new GasMixture(this, new GasMixtureState(volume, temperature));
        }
    }

    /// <summary>Creates sandboxed live access to one voxel's gas mixture.</summary>
    /// <remarks>
    ///     The returned capability never exposes solver arrays. It is bound to the current chunk generation and
    ///     becomes stale if that chunk is removed or replaced at the same grid position.
    /// </remarks>
    [PublicAPI]
    public IGasMixture GetVoxelGasMixture(AtmosChunkHandle chunk, ushort localVoxelIndex)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            var identity = _kernel.GetVoxelMixtureIdentity(chunk.Position, localVoxelIndex);
            return new VoxelGasMixture(this, chunk.Position, identity.Generation, identity.LocalVoxelIndex);
        }
    }

    /// <summary>Creates sandboxed live access to one voxel addressed by local coordinates.</summary>
    [PublicAPI]
    public IGasMixture GetVoxelGasMixture(AtmosChunkHandle chunk, int x, int y, int z)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            var identity = _kernel.GetVoxelMixtureIdentity(chunk.Position, x, y, z);
            return new VoxelGasMixture(this, chunk.Position, identity.Generation, identity.LocalVoxelIndex);
        }
    }

    internal float GetMixtureVolume(IInternalGasMixture mixture)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return mixture switch
            {
                GasMixture owned => owned.State.Volume,
                VoxelGasMixture voxel => _kernel.GetVoxelMixtureVolume(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex),
                _ => throw CreateUnsupportedMixtureException(nameof(mixture))
            };
        }
    }

    internal float GetMixtureTemperature(IInternalGasMixture mixture)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return mixture switch
            {
                GasMixture owned => owned.State.Temperature,
                VoxelGasMixture voxel => _kernel.GetVoxelMixtureTemperature(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex),
                _ => throw CreateUnsupportedMixtureException(nameof(mixture))
            };
        }
    }

    internal float GetMixturePressure(IInternalGasMixture mixture)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return mixture switch
            {
                GasMixture owned => CalculateMixturePressure(owned.State),
                VoxelGasMixture voxel => _kernel.GetVoxelMixturePressure(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex),
                _ => throw CreateUnsupportedMixtureException(nameof(mixture))
            };
        }
    }

    internal float GetMixtureTotalMoles(IInternalGasMixture mixture)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return mixture switch
            {
                GasMixture owned => owned.State.TotalMoles,
                VoxelGasMixture voxel => _kernel.GetVoxelMixtureTotalMoles(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex),
                _ => throw CreateUnsupportedMixtureException(nameof(mixture))
            };
        }
    }

    internal int GetMixtureActiveGasCount(IInternalGasMixture mixture)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return mixture switch
            {
                GasMixture owned => owned.State.ActiveGasCount,
                VoxelGasMixture voxel => _kernel.GetVoxelMixtureActiveGasCount(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex),
                _ => throw CreateUnsupportedMixtureException(nameof(mixture))
            };
        }
    }

    internal float GetMixtureMoles(IInternalGasMixture mixture, int gasId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gasId);
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return mixture switch
            {
                GasMixture owned => owned.State.Moles.GetValueOrDefault(gasId),
                VoxelGasMixture voxel => _kernel.GetVoxelMixtureMoles(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex,
                    gasId),
                _ => throw CreateUnsupportedMixtureException(nameof(mixture))
            };
        }
    }

    internal void SetMixtureVolume(GasMixture mixture, float volume)
    {
        ValidateVolume(volume);
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            GasMixtureState state = mixture.State;
            float previousVolume = state.Volume;
            state.Volume = volume;
            try
            {
                ValidateState(state);
            }
            catch
            {
                state.Volume = previousVolume;
                throw;
            }
        }
    }

    internal void SetMixtureTemperature(IInternalGasMixture mixture, float temperature)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            if (mixture is GasMixture owned)
            {
                owned.State.Temperature = temperature;
                return;
            }

            if (mixture is VoxelGasMixture voxel)
            {
                _kernel.SetVoxelMixtureTemperature(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex,
                    temperature);
                return;
            }

            throw CreateUnsupportedMixtureException(nameof(mixture));
        }
    }

    internal void SetMixtureMoles(IInternalGasMixture mixture, int gasId, float moles)
    {
        ValidateGasId(gasId);
        ValidateNonnegativeFinite(moles, nameof(moles));
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            if (mixture is GasMixture owned)
            {
                SetOwnedMixtureMoles(owned.State, gasId, moles);
                return;
            }

            if (mixture is VoxelGasMixture voxel)
            {
                _kernel.SetVoxelMixtureMoles(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex,
                    gasId,
                    moles);
                return;
            }

            throw CreateUnsupportedMixtureException(nameof(mixture));
        }
    }

    internal void AdjustMixtureMoles(IInternalGasMixture mixture, int gasId, float deltaMoles)
    {
        ValidateGasId(gasId);
        if (!float.IsFinite(deltaMoles))
            throw new ArgumentOutOfRangeException(nameof(deltaMoles), deltaMoles, "Mole adjustment must be finite.");

        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            if (mixture is GasMixture owned)
            {
                float adjusted = owned.State.Moles.GetValueOrDefault(gasId) + deltaMoles;
                if (!float.IsFinite(adjusted))
                    throw new InvalidOperationException("The adjusted gas amount exceeds the supported range.");
                SetOwnedMixtureMoles(owned.State, gasId, MathF.Max(0f, adjusted));
                return;
            }

            if (mixture is VoxelGasMixture voxel)
            {
                _kernel.AdjustVoxelMixtureMoles(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex,
                    gasId,
                    deltaMoles);
                return;
            }

            throw CreateUnsupportedMixtureException(nameof(mixture));
        }
    }

    internal void AddGasToMixture(IInternalGasMixture mixture, int gasId, float moles, float temperature)
    {
        ValidateGasId(gasId);
        ValidatePositiveFinite(moles, nameof(moles));
        ValidateTemperature(temperature);

        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            if (mixture is GasMixture owned)
            {
                AddGasToOwnedMixture(owned.State, gasId, moles, temperature);
                return;
            }

            if (mixture is VoxelGasMixture voxel)
            {
                _kernel.AddVoxelMixtureGas(
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
    }

    internal void ClearMixture(IInternalGasMixture mixture)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            if (mixture is GasMixture owned)
            {
                owned.State.Moles.Clear();
                return;
            }

            if (mixture is VoxelGasMixture voxel)
            {
                _kernel.ClearVoxelMixture(
                    voxel.ChunkPosition,
                    voxel.ChunkGeneration,
                    voxel.LocalVoxelIndex);
                return;
            }

            throw CreateUnsupportedMixtureException(nameof(mixture));
        }
    }

    internal GasMixture RemoveFromMixture(IInternalGasMixture mixture, float moles)
    {
        ValidateNonnegativeFinite(moles, nameof(moles));
        return ExecuteMixtureTransaction(mixture is VoxelGasMixture, () =>
        {
            var source = CaptureMixtureState(mixture);
            float totalMoles = source.TotalMoles;
            float ratio = totalMoles > 0f ? MathF.Min(1f, moles / totalMoles) : 0f;
            return RemoveRatioCore(mixture, source, ratio);
        });
    }

    internal GasMixture RemoveRatioFromMixture(IInternalGasMixture mixture, float ratio)
    {
        ValidateFinite(ratio, nameof(ratio));
        return ExecuteMixtureTransaction(mixture is VoxelGasMixture, () =>
        {
            return RemoveRatioCore(mixture, CaptureMixtureState(mixture), Math.Clamp(ratio, 0f, 1f));
        });
    }

    internal GasMixture RemoveVolumeFromMixture(IInternalGasMixture mixture, float volume)
    {
        ValidateNonnegativeFinite(volume, nameof(volume));
        return ExecuteMixtureTransaction(mixture is VoxelGasMixture, () =>
        {
            var source = CaptureMixtureState(mixture);
            float ratio = Math.Clamp(volume / source.Volume, 0f, 1f);
            return RemoveRatioCore(mixture, source, ratio);
        });
    }

    internal float TransferMixture(IInternalGasMixture source, IGasMixture destination, float moles)
    {
        ValidateNonnegativeFinite(moles, nameof(moles));
        var internalDestination = GetOwnedMixture(destination, nameof(destination));
        return ExecuteTransfer(source, internalDestination, state =>
        {
            float totalMoles = state.TotalMoles;
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
        return ExecuteMixtureTransaction(mixture is VoxelGasMixture, () =>
        {
            return new GasMixture(this, CaptureMixtureState(mixture));
        });
    }

    internal GasMixtureSnapshot GetMixtureSnapshot(IInternalGasMixture mixture)
    {
        return ExecuteMixtureTransaction(mixture is VoxelGasMixture, () =>
        {
            var state = CaptureMixtureState(mixture);
            var gases = new GasMixtureGas[state.ActiveGasCount];
            var index = 0;
            foreach (var (gasId, moles) in state.Moles)
                gases[index++] = new GasMixtureGas(gasId, moles);

            return new GasMixtureSnapshot(
                state.Volume,
                state.Temperature,
                CalculateMixturePressure(state),
                state.TotalMoles,
                gases);
        });
    }

    private GasMixture RemoveRatioCore(IInternalGasMixture mixture, GasMixtureState source, float ratio)
    {
        var removed = new GasMixtureState(source.Volume, source.Temperature);
        if (ratio <= 0f || source.ActiveGasCount == 0)
            return new GasMixture(this, removed);

        ValidateMixtureCanMutate(mixture);
        foreach (var (gasId, sourceMoles) in source.Moles.ToArray())
        {
            float removedMoles = sourceMoles * ratio;
            float remainingMoles = sourceMoles - removedMoles;
            if (removedMoles > 0f)
                removed.Moles.Add(gasId, removedMoles);
            SetStateMoles(source, gasId, remainingMoles);
        }

        ValidateState(source);
        ValidateState(removed);
        ApplyMixtureState(mixture, source);
        return new GasMixture(this, removed);
    }

    private float ExecuteTransfer(
        IInternalGasMixture source,
        IInternalGasMixture destination,
        Func<GasMixtureState, float> getRatio)
    {
        if (IsSameMixture(source, destination))
            return 0f;

        bool usesVoxel = source is VoxelGasMixture || destination is VoxelGasMixture;
        return ExecuteMixtureTransaction(usesVoxel, () =>
        {
            var sourceState = CaptureMixtureState(source);
            float ratio = getRatio(sourceState);
            if (ratio <= 0f || sourceState.ActiveGasCount == 0)
                return 0f;

            ValidateMixturesCanMutate(source, destination);
            var destinationState = CaptureMixtureState(destination);
            var removed = RemoveRatioFromState(sourceState, ratio);
            MergeStates(destinationState, removed);
            ValidateState(sourceState);
            ValidateState(destinationState);

            ApplyMixtureState(source, sourceState);
            ApplyMixtureState(destination, destinationState);
            return removed.TotalMoles;
        });
    }

    private TResult ExecuteMixtureTransaction<TResult>(bool usesVoxel, Func<TResult> transaction)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            if (!usesVoxel)
                return transaction();

            TResult result = default!;
            _kernel.ExecuteMixtureTransaction(() => result = transaction());
            return result;
        }
    }

    private GasMixtureState CaptureMixtureState(IInternalGasMixture mixture)
    {
        if (mixture is GasMixture owned)
            return owned.CaptureState();

        if (mixture is VoxelGasMixture voxel)
        {
            var voxelState = _kernel.CaptureVoxelMixture(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex);
            var state = new GasMixtureState(voxelState.Volume, voxelState.Temperature);
            foreach (var (gasId, moles) in voxelState.Gases)
                state.Moles.Add(gasId, moles);
            return state;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    private void ApplyMixtureState(IInternalGasMixture mixture, GasMixtureState state)
    {
        if (mixture is GasMixture owned)
        {
            owned.ApplyState(state);
            return;
        }

        if (mixture is VoxelGasMixture voxel)
        {
            _kernel.ReplaceVoxelMixture(
                voxel.ChunkPosition,
                voxel.ChunkGeneration,
                voxel.LocalVoxelIndex,
                state.Temperature,
                state.ToGasArray());
            return;
        }

        throw CreateUnsupportedMixtureException(nameof(mixture));
    }

    private void ValidateMixtureCanMutate(IInternalGasMixture mixture)
    {
        if (mixture is not VoxelGasMixture voxel)
            return;
        _kernel.ValidateVoxelMixtureMutations([GetVoxelAddress(voxel)]);
    }

    private void ValidateMixturesCanMutate(
        IInternalGasMixture first,
        IInternalGasMixture second)
    {
        if (first is VoxelGasMixture firstVoxel && second is VoxelGasMixture secondVoxel)
        {
            _kernel.ValidateVoxelMixtureMutations(
                [GetVoxelAddress(firstVoxel), GetVoxelAddress(secondVoxel)]);
            return;
        }

        ValidateMixtureCanMutate(first);
        ValidateMixtureCanMutate(second);
    }

    private static VoxelGasMixtureAddress GetVoxelAddress(VoxelGasMixture mixture)
    {
        return new VoxelGasMixtureAddress(
            mixture.ChunkPosition,
            mixture.ChunkGeneration,
            mixture.LocalVoxelIndex);
    }

    private void SetOwnedMixtureMoles(GasMixtureState state, int gasId, float moles)
    {
        bool hadGas = state.Moles.TryGetValue(gasId, out float previousMoles);
        SetStateMoles(state, gasId, moles);
        try
        {
            ValidateState(state);
        }
        catch
        {
            if (hadGas)
                state.Moles[gasId] = previousMoles;
            else
                state.Moles.Remove(gasId);
            throw;
        }
    }

    private void AddGasToOwnedMixture(GasMixtureState state, int gasId, float moles, float temperature)
    {
        bool hadGas = state.Moles.TryGetValue(gasId, out float previousMoles);
        float previousTemperature = state.Temperature;
        float combinedMoles = previousMoles + moles;
        if (!float.IsFinite(combinedMoles))
            throw new InvalidOperationException("A merged gas amount exceeds the supported range.");

        float currentHeatCapacity = CalculateMixtureHeatCapacity(state);
        float incomingHeatCapacity = moles * GetMolarHeatCapacityAtConstantVolume(gasId);
        float combinedHeatCapacity = currentHeatCapacity + incomingHeatCapacity;
        if (!float.IsFinite(combinedHeatCapacity))
            throw new InvalidOperationException("The mixture's heat capacity exceeds the supported range.");

        float currentTemperature = GetEffectiveMixtureTemperature(state.Temperature);
        float incomingTemperature = GetEffectiveMixtureTemperature(temperature);
        float mixedTemperature = combinedHeatCapacity > 0f
            ? currentTemperature +
              (incomingTemperature - currentTemperature) * incomingHeatCapacity / combinedHeatCapacity
            : temperature;

        state.Moles[gasId] = combinedMoles;
        state.Temperature = mixedTemperature;
        try
        {
            ValidateState(state);
        }
        catch
        {
            if (hadGas)
                state.Moles[gasId] = previousMoles;
            else
                state.Moles.Remove(gasId);
            state.Temperature = previousTemperature;
            throw;
        }
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

        float destinationTemperature = GetEffectiveMixtureTemperature(destination.Temperature);
        float incomingTemperature = GetEffectiveMixtureTemperature(incoming.Temperature);
        float mixedTemperature = combinedHeatCapacity > 0f
            ? destinationTemperature +
              (incomingTemperature - destinationTemperature) * incomingHeatCapacity / combinedHeatCapacity
            : incoming.Temperature;

        foreach (var (gasId, incomingMoles) in incoming.Moles)
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
        var total = 0f;
        foreach (var (gasId, moles) in state.Moles)
            total += moles * GetMolarHeatCapacityAtConstantVolume(gasId);
        if (!float.IsFinite(total))
            throw new InvalidOperationException("The mixture's heat capacity exceeds the supported range.");
        return total;
    }

    private float GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        float fallback = Config.DefaultMolarHeatCapacityAtConstantVolume;
        if (!float.IsFinite(fallback) || fallback <= 0f)
            fallback = AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

        var registry = Config.GasRegistry;
        if ((uint)gasId < (uint)registry.Count)
        {
            float configured = registry[gasId].MolarHeatCapacityAtConstantVolume;
            if (float.IsFinite(configured) && configured > 0f)
                return configured;
        }

        return fallback;
    }

    private float GetEffectiveMixtureTemperature(float temperature)
    {
        if (float.IsFinite(temperature) && temperature > 0f)
            return temperature;
        float fallback = Config.DefaultTemperatureFallback;
        return float.IsFinite(fallback) && fallback > 0f
            ? fallback
            : AtmosConfigDefaults.DefaultTemperatureFallback;
    }

    private float CalculateMixturePressure(GasMixtureState state)
    {
        float totalMoles = state.TotalMoles;
        if (totalMoles <= 0f)
            return 0f;
        return totalMoles / state.Volume * AtmosPhysicalConstants.MolarGasConstant *
               GetEffectiveMixtureTemperature(state.Temperature);
    }

    private static GasMixtureState RemoveRatioFromState(GasMixtureState source, float ratio)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        var removed = new GasMixtureState(source.Volume, source.Temperature);
        foreach (var (gasId, sourceMoles) in source.Moles.ToArray())
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
        var total = 0f;
        foreach (var (gasId, moles) in state.Moles)
        {
            ValidateGasId(gasId);
            ValidatePositiveFinite(moles, nameof(state));
            total += moles;
        }

        if (!float.IsFinite(total))
            throw new InvalidOperationException("The mixture's total moles exceed the supported range.");

        CalculateMixtureHeatCapacity(state);

        float pressure = total / state.Volume * AtmosPhysicalConstants.MolarGasConstant *
                         GetEffectiveMixtureTemperature(state.Temperature);
        if (!float.IsFinite(pressure))
            throw new InvalidOperationException("The mixture's pressure exceeds the supported range.");
    }

    private IInternalGasMixture GetOwnedMixture(IGasMixture mixture, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(mixture, parameterName);
        if (mixture is not IInternalGasMixture internalMixture)
            throw CreateUnsupportedMixtureException(parameterName);
        ValidateOwnedMixture(internalMixture, parameterName);
        return internalMixture;
    }

    private void ValidateOwnedMixture(IInternalGasMixture mixture, string parameterName)
    {
        if (!ReferenceEquals(mixture.Owner, this))
        {
            throw new ArgumentException(
                "Gas mixtures must be owned by the same AtmosSimulation.",
                parameterName);
        }
    }

    private static bool IsSameMixture(IInternalGasMixture left, IInternalGasMixture right)
    {
        if (ReferenceEquals(left, right))
            return true;
        return left is VoxelGasMixture leftVoxel && right is VoxelGasMixture rightVoxel &&
               leftVoxel.ChunkPosition == rightVoxel.ChunkPosition &&
               leftVoxel.ChunkGeneration == rightVoxel.ChunkGeneration &&
               leftVoxel.LocalVoxelIndex == rightVoxel.LocalVoxelIndex;
    }

    private static ArgumentException CreateUnsupportedMixtureException(string parameterName)
    {
        return new ArgumentException(
            "Only GasMixture instances and voxel mixtures created by AtmosSimulation are supported.",
            parameterName);
    }

    private static void ValidateGasId(int gasId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gasId);
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
            throw new ArgumentOutOfRangeException(nameof(temperature), temperature,
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

}
