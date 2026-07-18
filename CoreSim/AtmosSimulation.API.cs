using JetBrains.Annotations;

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
}