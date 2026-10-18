using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    private readonly Int3 _dimensions;
    private Action<AtmosOperation>? _externalOperationSink;
    private bool _isApplyingOperation;
    private bool _isReplaying;
    private Action _replayTick = null!;
    private Func<string, bool, bool> _setWorldSolverEnabled = null!;
    private Func<AtmosSolverCheckpoint[]> _solverCheckpointProvider = static () => [];
    private Action _solversRestored = static () => { };
    private AtmosStateHash? _stoppedRecordingHash;

    private bool ShouldRecord =>
        (_isRecording || _externalOperationSink != null) &&
        !_isTickExecuting &&
        !_isApplyingOperation;

    internal AtmosTimelinePosition TimelinePosition
    {
        get
        {
            lock (StateGate)
            {
                return new AtmosTimelinePosition(checked((ulong)TickCount), _lastOperationSequence);
            }
        }
    }

    internal bool IsReplaying
    {
        get
        {
            lock (StateGate)
            {
                return _isReplaying;
            }
        }
    }

    private void RecordOperation(AtmosOperation operation)
    {
        if (_externalOperationSink != null)
        {
            _externalOperationSink(operation);
            return;
        }

        _lastOperationSequence = checked(_lastOperationSequence + 1);
        _recordedOperations.Add(new AtmosRecordedOperation(TimelinePosition, operation));
    }

    /// <summary>
    ///     Routes external semantic mutations to a world-owned recorder without coupling the kernel to API identities.
    /// </summary>
    internal void SetExternalOperationSink(Action<AtmosOperation>? sink)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("change the external operation recorder");
            if (_isRecording)
            {
                throw new InvalidOperationException("Stop component recording before attaching a world operation recorder.");
            }

            _externalOperationSink = sink;
        }
    }

    private void RecordVoxelMixture(AtmosChunk chunk, ushort index)
    {
        if (ShouldRecord)
            RecordOperation(new SetVoxelMixtureOperation(chunk, index));
    }

    /// <summary>
    ///     Connects the kernel to the world that owns its solver pipeline and tick loop.
    /// </summary>
    /// <param name="solverCheckpointProvider">Supplies the pipeline metadata stored in and validated against checkpoints.</param>
    /// <param name="setWorldSolverEnabled">Applies checkpointed and recorded solver enablement.</param>
    /// <param name="replayTick">Advances the owning world by one tick during replay.</param>
    /// <param name="solversRestored">
    ///     Runs at the end of every checkpoint install, including the one inside <see cref="ReplayTo" /> before any
    ///     replayed tick and the one that puts the previous state back after a failed replay.
    /// </param>
    internal void ConfigureWorldCallbacks(
        Func<AtmosSolverCheckpoint[]> solverCheckpointProvider,
        Func<string, bool, bool> setWorldSolverEnabled,
        Action replayTick,
        Action solversRestored)
    {
        _solverCheckpointProvider = solverCheckpointProvider;
        _setWorldSolverEnabled = setWorldSolverEnabled;
        _replayTick = replayTick;
        _solversRestored = solversRestored;
    }

    internal AtmosSimulationCheckpoint CaptureCheckpoint()
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("capture a checkpoint during a tick");
            return new AtmosSimulationCheckpoint(
                _dimensions,
                TimelinePosition,
                _config,
                _solverCheckpointProvider(),
                OrderedChunks().Select(static chunk => new AtmosChunkCheckpoint(chunk)).ToArray());
        }
    }

    internal AtmosStateHash ComputeStateHash()
    {
        lock (StateGate)
        {
            return CaptureCheckpoint().ComputeStateHash();
        }
    }

    /// <summary>
    ///     Resumes the stopped recording if the live state still matches its head.
    /// </summary>
    /// <param name="current">
    ///     The live state's hash when the caller already knows it, such as right after restoring a checkpoint whose
    ///     digest is cached; otherwise the state is captured and hashed here.
    /// </param>
    internal void ResumeRecording(AtmosStateHash? current = null)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("resume recording");
            if (_isRecording || !_hasRecording || _stoppedRecordingHash != (current ?? ComputeStateHash()))
                throw new InvalidOperationException("Recording can only resume at the unchanged, stopped recording head.");

            _isRecording = true;
        }
    }

    internal void RestoreCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("restore a checkpoint during a tick");
            if (_isRecording)
                throw new InvalidOperationException("Stop recording before restoring a checkpoint.");

            ValidateCheckpoint(checkpoint);
            InstallCheckpoint(checkpoint);
        }
    }

    internal void ValidateCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.FormatVersion != AtmosSimulationCheckpoint.CurrentFormatVersion ||
            checkpoint.CompatibilityVersion != AtmosSimulationCheckpoint.CurrentCompatibilityVersion ||
            checkpoint.Dimensions != _dimensions ||
            checkpoint.Position.Tick > int.MaxValue)
        {
            throw new ArgumentException(
                "The checkpoint format or dimensions are incompatible with this simulation.",
                nameof(checkpoint));
        }

        AtmosSolverCheckpoint[] steps = _solverCheckpointProvider();
        if (steps.Length != 0 &&
            (steps.Length != checkpoint.Solvers.Count ||
             steps.Where((step, index) =>
                 step.Name != checkpoint.Solvers[index].Name ||
                 step.IsCustom != checkpoint.Solvers[index].IsCustom ||
                 step.NeighborSelectionKey != checkpoint.Solvers[index].NeighborSelectionKey).Any()))
        {
            throw new ArgumentException(
                "The checkpoint requires the same solver names, kinds, order, and neighbor selections.",
                nameof(checkpoint));
        }

        checkpoint.Config.ValidateGasRegistry();
        var positions = new HashSet<Int3>();
        foreach (var chunk in checkpoint.Chunks)
        {
            if (chunk.Dimensions != _dimensions || !positions.Add(chunk.Position))
                throw new ArgumentException("The checkpoint contains incompatible or duplicate chunks.", nameof(checkpoint));
        }
    }

    private void InstallCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        // Materialize all pooled storage before replacing anything observable.
        var replacement = new ConcurrentDictionary<Int3, AtmosChunk>();
        try
        {
            int denseId = 0;
            foreach (var chunk in checkpoint.Chunks)
            {
                var materialized = chunk.Materialize();
                // This bypasses RegisterChunk, which normally assigns DenseId, so it must be assigned here too.
                materialized.DenseId = denseId++;
                replacement[chunk.Position] = materialized;
            }
        }
        catch
        {
            foreach (var chunk in replacement.Values)
                chunk.Release();

            throw;
        }

        ConcurrentDictionary<Int3, AtmosChunk> previous = _chunkMap;
        _chunkMap = replacement;
        // Rebase on the restored chunk count instead of continuing to grow across repeated restores
        // (e.g. replay scrubbing), which would otherwise leave DenseId-indexed lookup tables oversized.
        // Ids are reassigned densely as 0..replacement.Count above, so none are free.
        _nextChunkDenseId = replacement.Count;
        _freeChunkDenseIds.Clear();
        _config = checkpoint.Config;
        CurrentTickConfig.Capture(_config);
        ClearSolverData();
        TickCount = checked((int)checkpoint.Position.Tick);
        _lastOperationSequence = checkpoint.Position.OperationSequence;
        _accumulator = 0f;
        foreach (var step in checkpoint.Solvers)
            _setWorldSolverEnabled(step.Name, step.Enabled);

        _defaultSolvers.ClearTransientState();
        _chunkCollectionRevision++;
        LastBoundaryTicks = 0;
        foreach (var chunk in previous.Values)
            chunk.Release();

        // Host solvers reset their own transient state last, so a throw from one leaves a fully installed
        // checkpoint behind rather than a half-swapped kernel.
        _solversRestored();
    }

    internal AtmosReplayResult ReplayTo(
        AtmosSimulationCheckpoint checkpoint, IReadOnlyList<AtmosRecordedOperation> operations,
        AtmosTimelinePosition target)
    {
        ArgumentNullException.ThrowIfNull(operations);
        // Detach the host's batch before validation and application.
        AtmosRecordedOperation[] history = operations.ToArray();
        lock (StateGate)
        {
            ThrowIfTickExecuting("replay during a tick");
            if (_isRecording || _isReplaying)
                throw new InvalidOperationException("Stop recording before replaying; recursive replay is unavailable.");

            ValidateCheckpoint(checkpoint);
            ReplayHistoryValidator.ThrowIfInvalidReplay(checkpoint.Position, history, target);
            var previous = CaptureCheckpoint();
            long started = Stopwatch.GetTimestamp();
            _isReplaying = true;
            try
            {
                InstallCheckpoint(checkpoint);
                foreach (var operation in history)
                {
                    if (operation.Sequence <= checkpoint.Position.OperationSequence || operation.Sequence > target.OperationSequence)
                        continue;

                    while ((ulong)TickCount < operation.AfterTick)
                        _replayTick();

                    ApplyRecordedOperation(operation.Operation);
                    _lastOperationSequence = operation.Sequence;
                }

                while ((ulong)TickCount < target.Tick)
                    _replayTick();

                return new AtmosReplayResult(
                    checkpoint.Position,
                    TimelinePosition,
                    target.Tick - checkpoint.Position.Tick,
                    Stopwatch.GetElapsedTime(started));
            }
            catch
            {
                InstallCheckpoint(previous);
                throw;
            }
            finally
            {
                _isReplaying = false;
            }
        }
    }

    internal void ApplyRecordedOperation(AtmosOperation operation)
    {
        _isApplyingOperation = true;
        try
        {
            ApplyRecordedOperationGenerated(operation);
        }
        finally
        {
            _isApplyingOperation = false;
        }
    }

    private void Apply(SetAtmosConfigOperation op)
    {
        SetAtmosConfig(op.Config);
    }

    private void Apply(CreateChunkOperation op)
    {
        CreateAndRegisterChunk(op.Position, _dimensions.X, _dimensions.Y, _dimensions.Z);
    }

    private void Apply(RemoveChunkOperation op)
    {
        UnregisterChunk(op.Position);
    }

    private void Apply(SetChunkClassificationOperation op)
    {
        SetChunkClassification(op.Position, op.Classification);
    }

    private void Apply(SetChunkBoundaryClassificationOperation op)
    {
        SetChunkBoundaryClassification(op.Position, op.Classification);
    }

    private void Apply(SetVoxelClassificationOperation op)
    {
        SetVoxelClassification(op.Position, op.LocalVoxelIndex, op.Classification);
    }

    private void Apply(SetVoxelTemperatureOperation op)
    {
        SetVoxelTemperature(op.Position, op.LocalVoxelIndex, op.Temperature);
    }

    private void Apply(AddGasToVoxelOperation op)
    {
        AddGasToVoxel(op.Position, op.LocalVoxelIndex, op.GasId, op.Moles, op.Temperature);
    }

    private void Apply(WakeChunkOperation op)
    {
        WakeChunk(op.Position);
    }

    private void Apply(SleepChunkOperation op)
    {
        SleepChunk(op.Position);
    }

    private void Apply(SetSolverEnabledOperation op)
    {
        if (!_setWorldSolverEnabled(op.Name, op.Enabled))
            throw new ArgumentException($"Unknown solver '{op.Name}'.");
    }

    private void Apply(SetVoxelMixtureOperation op)
    {
        var chunk = GetChunk(op.Position);
        ValidateVoxelIndex(chunk, op.LocalVoxelIndex);
        chunk.Wake();
        foreach (var gas in op.Gases)
            chunk.ActiveGases[chunk.GetOrCreateGasChannel(gas.GasId)].Moles[op.LocalVoxelIndex] = gas.Moles;

        chunk.Temperature[op.LocalVoxelIndex] = op.Temperature;
        chunk.TotalPressure[op.LocalVoxelIndex] = op.Pressure;
        chunk.TotalHeatCapacity[op.LocalVoxelIndex] = op.HeatCapacity;
        chunk.IsVacuum[op.LocalVoxelIndex] = op.Pressure <= 0f;
        chunk.MarkChanged();
    }

    private AtmosChunk[] OrderedChunks()
    {
        return _chunkMap.Values
            .OrderBy(static chunk => chunk.GridPosition.X)
            .ThenBy(static chunk => chunk.GridPosition.Y)
            .ThenBy(static chunk => chunk.GridPosition.Z).ToArray();
    }
}