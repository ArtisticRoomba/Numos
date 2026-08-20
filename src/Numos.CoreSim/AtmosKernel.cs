using System.Collections.Concurrent;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Owns simulation state, serialization, and the configured solver pipeline.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable, IAtmosSolverWorld
{
    private readonly ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();
    private readonly HashSet<ulong> _processedAggregateRootPairs = [];
    private readonly object _stateGate = new();
    private readonly AtmosSolverConfigSnapshot _tickConfig = new();
    private readonly AtmosSolverPipeline _solverPipeline;

    private float _accumulator;
    private long _chunkCollectionRevision;
    private AtmosConfig _config = new();
    private bool _hasConfigurationFingerprint;
    private bool _isTickExecuting;
    private ulong _lastConfigurationFingerprint;
    private bool _solverPipelineInvalidationPending;

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>Number of fixed simulation ticks processed since construction.</summary>
    internal int TickCount;

    internal AtmosKernel(
        int chunkWidth = AtmosChunkConstants.DefaultWidth,
        int chunkHeight = AtmosChunkConstants.DefaultHeight,
        int chunkDepth = AtmosChunkConstants.DefaultDepth)
    {
        var defaultSolvers = new DefaultAtmosSolvers(chunkWidth, chunkHeight, chunkDepth);
        _solverPipeline = new AtmosSolverPipeline(defaultSolvers.CreateSteps, defaultSolvers);
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("dispose the simulation");
            foreach (var chunk in _chunkMap.Values)
                chunk.Release();

            _chunkMap.Clear();
            _solverPipeline.Dispose();
        }
    }

    private void TickSimulation(AtmosChunk[] chunks)
    {
        ThrowIfTickExecuting("run a recursive simulation tick");
        _isTickExecuting = true;
        try
        {
            _tickConfig.Capture(_config);
            bool configurationChanged = _hasConfigurationFingerprint &&
                                        _lastConfigurationFingerprint != _tickConfig.ConfigurationFingerprint;
            if (!_hasConfigurationFingerprint || configurationChanged)
            {
                ValidateAndRefreshConfiguredState(chunks);
                _lastConfigurationFingerprint = _tickConfig.ConfigurationFingerprint;
                _hasConfigurationFingerprint = true;
            }

            if (configurationChanged || _solverPipelineInvalidationPending)
            {
                foreach (var chunk in chunks)
                    chunk.InvalidateSolverDerivedState();

                _solverPipelineInvalidationPending = false;
            }

            TickCount++;

            foreach (var chunk in chunks)
            {
                if (chunk.IsAwake)
                    chunk.MarkChanged();
            }

            var context = new AtmosSolverExecutionContext(this, chunks, _tickConfig, _config, TickCount);
            _solverPipeline.Execute(context);

            // This coordinator deliberately runs after the complete configured pipeline, including custom
            // stages registered after the built-ins. No earlier stage can therefore mutate a chunk after it
            // has been projected and committed to automatic sleep for this tick.
            foreach (var chunk in chunks)
            {
                if (_tickConfig.VoxelSnappingEnabled)
                {
                    if (chunk.IsAwake)
                        chunk.VoxelAggregates.FinalizeTick(
                            chunk, _tickConfig, _processedAggregateRootPairs);
                }
                else
                {
                    chunk.VoxelAggregates.Reset();
                }
            }
        }
        finally
        {
            _isTickExecuting = false;
        }
    }

    private void ThrowIfTickExecuting(string operation)
    {
        if (_isTickExecuting)
            throw new InvalidOperationException($"A solver callback cannot {operation}.");
    }

    private void ValidateAndRefreshConfiguredState(AtmosChunk[] chunks)
    {
        // Validate the complete world first so a rejected live configuration cannot partially refresh caches.
        foreach (var chunk in chunks)
        {
            for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
                _ = CalculateConfiguredVoxelState(chunk, (ushort)voxelIndex);
        }

        foreach (var chunk in chunks)
        {
            bool changed = false;
            for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
            {
                (float heatCapacity, float pressure) =
                    CalculateConfiguredVoxelState(chunk, (ushort)voxelIndex);
                changed |= chunk.TotalHeatCapacity[voxelIndex] != heatCapacity ||
                           chunk.TotalPressure[voxelIndex] != pressure;
                chunk.TotalHeatCapacity[voxelIndex] = heatCapacity;
                chunk.TotalPressure[voxelIndex] = pressure;
            }

            if (changed)
                chunk.MarkChanged();
        }
    }

    private (float HeatCapacity, float Pressure) CalculateConfiguredVoxelState(
        AtmosChunk chunk,
        ushort voxelIndex)
    {
        var totalMoles = 0f;
        var heatCapacity = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            GasChannel channel = chunk.ActiveGases[gas];
            float moles = channel.Moles[voxelIndex];
            if (!float.IsFinite(moles) || moles < 0f)
                throw new InvalidOperationException("The current gas state is outside the supported range.");

            totalMoles += moles;
            heatCapacity += moles * _tickConfig.GetMolarHeatCapacityAtConstantVolume(channel.GasId);
        }

        if (!float.IsFinite(totalMoles) || !float.IsFinite(heatCapacity))
            throw new InvalidOperationException("The current configuration makes a voxel total unrepresentable.");

        float pressure = AtmosSolverMath.CalculatePressure(
            _tickConfig,
            totalMoles,
            chunk.Temperature[voxelIndex]);
        if (!float.IsFinite(pressure))
            throw new InvalidOperationException("The current configuration makes a voxel pressure unrepresentable.");

        return (heatCapacity, pressure);
    }

    bool IAtmosSolverWorld.TryGetChunk(Int3 position, out AtmosChunk chunk)
    {
        return _chunkMap.TryGetValue(position, out chunk!);
    }

    void IAtmosSolverWorld.AddBoundaryProcessingTicks(long elapsedTicks)
    {
        LastBoundaryTicks += elapsedTicks;
    }
}
