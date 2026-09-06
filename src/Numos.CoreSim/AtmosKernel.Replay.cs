using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    private readonly Int3 _dimensions;
    private bool _isApplyingOperation;
    private bool _isReplaying;
    private AtmosStateHash? _stoppedRecordingHash;

    private bool ShouldRecord => _isRecording && !_isTickExecuting && !_isApplyingOperation;

    internal AtmosTimelinePosition TimelinePosition
    {
        get
        {
            lock (_stateGate)
            {
                return new AtmosTimelinePosition(checked((ulong)TickCount), _lastOperationSequence);
            }
        }
    }

    internal bool IsReplaying
    {
        get
        {
            lock (_stateGate)
            {
                return _isReplaying;
            }
        }
    }

    private void RecordOperation(AtmosOperation operation)
    {
        _lastOperationSequence = checked(_lastOperationSequence + 1);
        _recordedOperations.Add(new AtmosRecordedOperation(TimelinePosition, operation));
    }

    private void RecordVoxelMixture(AtmosChunk chunk, ushort index)
    {
        if (ShouldRecord)
            RecordOperation(new SetVoxelMixtureOperation(chunk, index));
    }

    private void EnsureCanChangeSolverDefinition()
    {
        if (_isRecording || _isReplaying)
        {
            throw new InvalidOperationException(
                "Solver registration, removal and reset are unavailable during recording or replay. Register compatible named solvers before starting the session.");
        }
    }

    internal AtmosSimulationCheckpoint CaptureCheckpoint()
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("capture a checkpoint during a tick");
            return new AtmosSimulationCheckpoint(
                _dimensions,
                TimelinePosition,
                _accumulator,
                _config,
                _solverPipeline.GetSteps().Select(static step =>
                    new AtmosSolverCheckpoint(step.Name, step.Kind == SolverStepKind.Custom, step.Enabled)).ToArray(),
                OrderedChunks().Select(static chunk => new AtmosChunkCheckpoint(chunk)).ToArray());
        }
    }

    internal AtmosStateHash ComputeStateHash()
    {
        lock (_stateGate)
        {
            return AtmosStateHasher.Hash(CaptureCheckpoint());
        }
    }

    internal void ResumeRecording()
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("resume recording");
            if (_isRecording || !_hasRecording || _stoppedRecordingHash != ComputeStateHash())
                throw new InvalidOperationException("Recording can only resume at the unchanged, stopped recording head.");

            _isRecording = true;
        }
    }

    internal void ResumeRecordingFromCurrentPosition()
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("resume recording from the current position");
            if (_isRecording ||
                !_hasRecording ||
                TimelinePosition.Tick < _recordingStart.Tick ||
                TimelinePosition.Tick > _recordingHead.Tick ||
                _lastOperationSequence < _recordingStart.OperationSequence ||
                _lastOperationSequence > _recordingHead.OperationSequence)
                throw new InvalidOperationException("Recording can only branch from a position in its stopped history.");

            _recordedOperations.RemoveAll(operation => operation.Sequence > _lastOperationSequence);
            _recordingHead = TimelinePosition;
            _stoppedRecordingHash = null;
            _isRecording = true;
        }
    }

    internal void RestoreCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("restore a checkpoint during a tick");
            if (_isRecording)
                throw new InvalidOperationException("Stop recording before restoring a checkpoint.");

            ValidateCheckpoint(checkpoint);
            InstallCheckpoint(checkpoint);
        }
    }

    private void ValidateCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.FormatVersion is < 1 or > AtmosSimulationCheckpoint.CurrentFormatVersion ||
            checkpoint.CompatibilityVersion != AtmosSimulationCheckpoint.CurrentCompatibilityVersion ||
            checkpoint.Dimensions != _dimensions ||
            checkpoint.Position.Tick > int.MaxValue)
        {
            throw new ArgumentException(
                "The checkpoint format or dimensions are incompatible with this simulation.",
                nameof(checkpoint));
        }

        SolverStepInfo[] steps = _solverPipeline.GetSteps();
        if (steps.Length != checkpoint.Solvers.Count ||
            steps.Where((step, index) =>
                step.Name != checkpoint.Solvers[index].Name ||
                step.Kind == SolverStepKind.Custom != checkpoint.Solvers[index].IsCustom).Any())
        {
            throw new ArgumentException(
                "The checkpoint requires the same solver names, kinds and order. Custom names identify host-supplied compatible implementations.",
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
            foreach (var chunk in checkpoint.Chunks)
                replacement[chunk.Position] = chunk.Materialize();
        }
        catch
        {
            foreach (var chunk in replacement.Values)
                chunk.Release();

            throw;
        }

        ConcurrentDictionary<Int3, AtmosChunk> previous = _chunkMap;
        _chunkMap = replacement;
        _config = checkpoint.Config;
        _tickConfig.Capture(_config);
        _tickConfig.ClearGasSolverData();
        TickCount = checked((int)checkpoint.Position.Tick);
        _lastOperationSequence = checkpoint.Position.OperationSequence;
        _accumulator = checkpoint.ElapsedAccumulator;
        foreach (var step in checkpoint.Solvers)
            _solverPipeline.SetEnabled(step.Name, step.Enabled);

        _defaultSolvers.ClearTransientState();
        _chunkCollectionRevision++;
        LastBoundaryTicks = 0;
        foreach (var chunk in previous.Values)
            chunk.Release();
    }

    internal AtmosReplayResult ReplayTo(
        AtmosSimulationCheckpoint checkpoint, IReadOnlyList<AtmosRecordedOperation> operations,
        AtmosTimelinePosition target)
    {
        ArgumentNullException.ThrowIfNull(operations);
        // Detach the host's batch before validation and application.
        AtmosRecordedOperation[] history = operations.ToArray();
        lock (_stateGate)
        {
            ThrowIfTickExecuting("replay during a tick");
            if (_isRecording || _isReplaying)
                throw new InvalidOperationException("Stop recording before replaying; recursive replay is unavailable.");

            ValidateCheckpoint(checkpoint);
            ValidateHistory(checkpoint.Position, history, target);
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
                        Tick();

                    ApplyOperation(operation.Operation);
                    _lastOperationSequence = operation.Sequence;
                }

                while ((ulong)TickCount < target.Tick)
                    Tick();

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

    private static void ValidateHistory(
        AtmosTimelinePosition start, AtmosRecordedOperation[] history, AtmosTimelinePosition target)
    {
        if (target.Tick < start.Tick || target.Tick > int.MaxValue || target.OperationSequence < start.OperationSequence)
            throw new ArgumentOutOfRangeException(nameof(target), "The target must follow the checkpoint.");

        ulong sequence = start.OperationSequence;
        ulong tick = start.Tick;
        AtmosRecordedOperation? previous = null;
        foreach (var operation in history)
        {
            if (operation == null ||
                operation.Operation == null ||
                previous != null && (operation.Sequence <= previous.Sequence || operation.AfterTick < previous.AfterTick))
            {
                throw new ArgumentException(
                    "Operations must be non-null and ordered by strictly increasing sequence and nondecreasing tick.",
                    nameof(history));
            }

            previous = operation;
            if (operation.Sequence <= start.OperationSequence) continue;

            if (operation.Sequence > target.OperationSequence)
            {
                if (operation.AfterTick < target.Tick)
                    throw new ArgumentException("The target omits an operation preceding its tick.", nameof(target));

                continue;
            }

            if (operation.Sequence != checked(sequence + 1) || operation.AfterTick < tick || operation.AfterTick > target.Tick)
            {
                throw new ArgumentException(
                    "The history has a sequence gap or an operation outside the replay interval.",
                    nameof(history));
            }

            sequence = operation.Sequence;
            tick = operation.AfterTick;
        }

        if (sequence != target.OperationSequence)
            throw new ArgumentException("The history does not contain the target operation sequence.", nameof(history));
    }

    private void ApplyOperation(AtmosOperation operation)
    {
        _isApplyingOperation = true;
        try
        {
            switch (operation)
            {
                case SetAtmosConfigOperation op:
                    SetAtmosConfig(op.Config);
                    break;
                case CreateChunkOperation op:
                    CreateAndRegisterChunk(op.Position, _dimensions.X, _dimensions.Y, _dimensions.Z, op.MaxActiveRooms);
                    break;
                case RemoveChunkOperation op:
                    UnregisterChunk(op.Position);
                    break;
                case SetChunkClassificationOperation op:
                    SetChunkClassification(op.Position, op.Classification);
                    break;
                case SetChunkBoundaryClassificationOperation op:
                    SetChunkBoundaryClassification(op.Position, op.Classification);
                    break;
                case SetVoxelClassificationOperation op:
                    SetVoxelClassification(op.Position, op.LocalVoxelIndex, op.Classification);
                    break;
                case SetVoxelTemperatureOperation op:
                    SetVoxelTemperature(op.Position, op.LocalVoxelIndex, op.Temperature);
                    break;
                case AddGasToVoxelOperation op:
                    AddGasToVoxel(op.Position, op.LocalVoxelIndex, op.GasId, op.Moles, op.Temperature);
                    break;
                case WakeRoomOperation op:
                    WakeRoom(op.Position, op.RoomId);
                    break;
                case SleepChunkOperation op:
                    SleepChunk(op.Position);
                    break;
                case SetSolverEnabledOperation op:
                    if (!SetSolverEnabled(op.Name, op.Enabled))
                        throw new ArgumentException($"Unknown solver '{op.Name}'.");

                    break;
                case SetVoxelMixtureOperation op:
                    var chunk = GetChunk(op.Position);
                    ValidateVoxelIndex(chunk, op.LocalVoxelIndex);
                    chunk.WakeRoom(GetGasRoomId(chunk, op.LocalVoxelIndex));
                    foreach (var gas in op.Gases)
                        chunk.ActiveGases[chunk.GetOrCreateGasChannel(gas.GasId)].Moles[op.LocalVoxelIndex] = gas.Moles;

                    chunk.Temperature[op.LocalVoxelIndex] = op.Temperature;
                    chunk.TotalPressure[op.LocalVoxelIndex] = op.Pressure;
                    chunk.TotalHeatCapacity[op.LocalVoxelIndex] = op.HeatCapacity;
                    chunk.MarkChanged();
                    break;
                case SetElapsedAccumulatorOperation op:
                    _accumulator = op.Seconds;
                    break;
                default: throw new ArgumentException($"Unsupported replay operation code {operation.Code}.", nameof(operation));
            }
        }
        finally
        {
            _isApplyingOperation = false;
        }
    }

    private AtmosChunk[] OrderedChunks()
    {
        return _chunkMap.Values
            .OrderBy(static chunk => chunk.GridPosition.X)
            .ThenBy(static chunk => chunk.GridPosition.Y)
            .ThenBy(static chunk => chunk.GridPosition.Z).ToArray();
    }
}