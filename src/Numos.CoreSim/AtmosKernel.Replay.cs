using System.Collections.Concurrent;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    private readonly Int3 _dimensions;
    private Action<AtmosOperation>? _externalOperationSink;
    private bool _isApplyingOperation;

    private bool ShouldRecord =>
        _externalOperationSink != null &&
        !_isTickExecuting &&
        !_isApplyingOperation;

    private void RecordOperation(AtmosOperation operation)
    {
        _externalOperationSink!(operation);
    }

    /// <summary>
    ///     Routes external semantic mutations to the owning world's recorder without coupling the kernel to API
    ///     identities. A <see langword="null" /> sink stops recording.
    /// </summary>
    internal void SetExternalOperationSink(Action<AtmosOperation>? sink)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("change the external operation recorder");
            _externalOperationSink = sink;
        }
    }

    private void RecordVoxelMixture(AtmosChunk chunk, ushort index)
    {
        if (ShouldRecord)
            RecordOperation(new SetVoxelMixtureOperation(chunk, index));
    }

    internal AtmosSimulationCheckpoint CaptureCheckpoint()
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("capture a checkpoint during a tick");
            AtmosChunk[] chunks = OrderedChunks();
            var chunkCheckpoints = new AtmosChunkCheckpoint[chunks.Length];
            for (int index = 0; index < chunks.Length; index++)
                chunkCheckpoints[index] = new AtmosChunkCheckpoint(chunks[index]);

            return new AtmosSimulationCheckpoint(_dimensions, chunkCheckpoints);
        }
    }

    /// <summary>
    ///     Replaces the grid with a checkpoint's chunks and moves the kernel to the owning world's tick.
    /// </summary>
    /// <param name="checkpoint">Grid state to install.</param>
    /// <param name="tickCount">The world's completed-tick count once the restore finishes.</param>
    /// <remarks>
    ///     Configuration and solver enablement belong to the world, which applies its own before or after this call.
    ///     Transient solver data is discarded either way.
    /// </remarks>
    internal void RestoreCheckpoint(AtmosSimulationCheckpoint checkpoint, int tickCount)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("restore a checkpoint during a tick");
            ValidateCheckpoint(checkpoint, _dimensions);
            InstallCheckpoint(checkpoint, tickCount);
        }
    }

    /// <summary>
    ///     Checks that a checkpoint can be installed in a simulation whose chunks have <paramref name="dimensions" />,
    ///     without needing a live kernel.
    /// </summary>
    internal static void ValidateCheckpoint(AtmosSimulationCheckpoint checkpoint, Int3 dimensions)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.FormatVersion != AtmosSimulationCheckpoint.CurrentFormatVersion ||
            checkpoint.CompatibilityVersion != AtmosSimulationCheckpoint.CurrentCompatibilityVersion ||
            checkpoint.Dimensions != dimensions ||
            dimensions.X <= 0 ||
            dimensions.Y <= 0 ||
            dimensions.Z <= 0 ||
            (long)dimensions.X * dimensions.Y * dimensions.Z > AtmosChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentException(
                "The checkpoint format or dimensions are incompatible with this simulation.",
                nameof(checkpoint));
        }

        var positions = new HashSet<Int3>();
        foreach (var chunk in checkpoint.Chunks)
        {
            if (chunk.Dimensions != dimensions || !positions.Add(chunk.Position))
                throw new ArgumentException("The checkpoint contains incompatible or duplicate chunks.", nameof(checkpoint));
        }
    }

    private void InstallCheckpoint(AtmosSimulationCheckpoint checkpoint, int tickCount)
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
        CurrentTickConfig.Capture(_config);
        ClearSolverData();
        TickCount = tickCount;
        _defaultSolvers.ClearTransientState();
        _chunkCollectionRevision++;
        LastBoundaryTicks = 0;
        LastFlowDiagnostics = default;
        foreach (var chunk in previous.Values)
            chunk.Release();
    }

    internal void ApplyRecordedOperation(AtmosOperation operation)
    {
        _isApplyingOperation = true;
        try
        {
            operation.Apply(this);
        }
        finally
        {
            _isApplyingOperation = false;
        }
    }

    internal void Apply(CreateChunkOperation op)
    {
        CreateAndRegisterChunk(op.Position, _dimensions.X, _dimensions.Y, _dimensions.Z);
    }

    internal void Apply(RemoveChunkOperation op)
    {
        UnregisterChunk(op.Position);
    }

    internal void Apply(SetChunkClassificationOperation op)
    {
        SetChunkClassification(op.Position, op.Classification);
    }

    internal void Apply(SetChunkBoundaryClassificationOperation op)
    {
        SetChunkBoundaryClassification(op.Position, op.Classification);
    }

    internal void Apply(SetVoxelClassificationOperation op)
    {
        SetVoxelClassification(op.Position, op.LocalVoxelIndex, op.Classification);
    }

    internal void Apply(SetVoxelTemperatureOperation op)
    {
        SetVoxelTemperature(op.Position, op.LocalVoxelIndex, op.Temperature);
    }

    internal void Apply(AddGasToVoxelOperation op)
    {
        AddGasToVoxel(op.Position, op.LocalVoxelIndex, op.GasId, op.Moles, op.Temperature);
    }

    internal void Apply(WakeChunkOperation op)
    {
        WakeChunk(op.Position);
    }

    internal void Apply(SleepChunkOperation op)
    {
        SleepChunk(op.Position);
    }

    internal void Apply(SetVoxelMomentumOperation op)
    {
        SetVoxelMomentum(op.Position, op.LocalVoxelIndex, op.MomentumX, op.MomentumY, op.MomentumZ);
    }

    internal void Apply(EditVoxelBoxOperation op)
    {
        EditVoxelBox(op);
    }

    internal void Apply(SetVoxelMixtureOperation op)
    {
        var chunk = GetChunk(op.Position);
        ValidateVoxelIndex(chunk, op.LocalVoxelIndex);
        ValidateMomentum(op.MomentumX, op.MomentumY, op.MomentumZ);
        chunk.Wake();
        foreach (var gas in op.Gases)
            chunk.ActiveGases[chunk.GetOrCreateGasChannel(gas.GasId)].Moles[op.LocalVoxelIndex] = gas.Moles;

        chunk.Temperature[op.LocalVoxelIndex] = op.Temperature;
        chunk.TotalPressure[op.LocalVoxelIndex] = op.Pressure;
        chunk.TotalHeatCapacity[op.LocalVoxelIndex] = op.HeatCapacity;
        chunk.MomentumX[op.LocalVoxelIndex] = op.MomentumX;
        chunk.MomentumY[op.LocalVoxelIndex] = op.MomentumY;
        chunk.MomentumZ[op.LocalVoxelIndex] = op.MomentumZ;
        chunk.IsVacuum[op.LocalVoxelIndex] = op.Pressure <= 0f;
        chunk.MarkChanged();
    }

    private AtmosChunk[] OrderedChunks()
    {
        // Positions are the map's keys, so this order is total and doesn't depend on sort stability.
        AtmosChunk[] chunks = _chunkMap.Values.ToArray();
        Array.Sort(
            chunks,
            static (first, second) =>
            {
                int order = first.GridPosition.X.CompareTo(second.GridPosition.X);
                if (order == 0)
                    order = first.GridPosition.Y.CompareTo(second.GridPosition.Y);

                return order != 0 ? order : first.GridPosition.Z.CompareTo(second.GridPosition.Z);
            });

        return chunks;
    }
}