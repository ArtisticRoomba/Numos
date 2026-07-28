using JetBrains.Annotations;
using Maths;

namespace Numos;

public partial class AtmosSimulation
{
    /// <summary>
    /// Updates the simulation.
    /// </summary>
    /// <param name="elapsedSeconds">Number of seconds since <see cref="Update"/> was called.</param>
    /// <param name="config">The <see cref="AtmosConfig"/> the sim should use when updating.</param>
    [PublicAPI]
    public void Update(float elapsedSeconds, AtmosConfig config)
    {
        _accumulator += elapsedSeconds;

        if (_accumulator > FixedDt * MaxStepsPerFrame)
        {
            _accumulator = FixedDt * MaxStepsPerFrame;
        }

        LastBoundaryTicks = 0;

        // Snapshot chunks
        var chunks = _chunkMap.Values.ToArray();

        var steps = 0;
        while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
        {
            _accumulator -= FixedDt;
            steps++;
            TickSimulation(chunks, config);
        }
    }

    /// <summary>
    ///     Sets the configuration for the simulation.
    /// </summary>
    /// <param name="config">The configuration to use.</param>
    [PublicAPI]
    public void SetAtmosConfig(AtmosConfig config)
    {
        _config = config;
    }

    [PublicAPI]
    public void RegisterChunk(AtmosChunk chunk)
    {
        _chunkMap[chunk.GridPosition] = chunk;
    }

    [PublicAPI]
    public void UnregisterChunk(AtmosChunk chunk)
    {
        _chunkMap.TryRemove(chunk.GridPosition, out _);
        chunk.Release();
    }

    /// <summary>
    ///     Runs a single simulation tick with the given configuration.
    ///     Useful for testing deterministic behavior.
    /// </summary>
    /// <param name="config">The configuration to use.</param>
    [PublicAPI]
    public void Tick(AtmosConfig config)
    {
        var chunks = _chunkMap.Values.ToArray();
        TickSimulation(chunks, config);
    }

    /// <summary>
    ///     Runs a single simulation tick using the default configuration.
    ///     Useful for testing deterministic behavior.
    /// </summary>
    [PublicAPI]
    public void Tick()
    {
        // Create a minimal config for testing
        var config = new AtmosConfig();
        var chunks = _chunkMap.Values.ToArray();
        TickSimulation(chunks, config);
    }

    /// <summary>
    ///     Gets the number of registered chunks.
    /// </summary>
    [PublicAPI]
    public int ChunkCount => _chunkMap.Count;
}