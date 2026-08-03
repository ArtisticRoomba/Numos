using Maths;
using Numos.Datatypes.Primitives;
using Numos.Datatypes.Snapshots;

namespace Numos;

/// <summary>
///     Public-facing safe API for Numos,
///     an engine-agnostic, pseudo-realistic, voxel-based atmospherics simulation.
/// </summary>
public sealed class AtmosSimulation : IDisposable
{
    /// <summary>
    ///     The fixed simulation rate in ticks per second.
    /// </summary>
    public const float SimulationRate = AtmosKernel.SimulationRate;

    private readonly int _chunkDepth;
    private readonly int _chunkHeight;
    private readonly int _chunkWidth;
    private readonly AtmosKernel _kernel;
    private bool _disposed;

    /// <summary>
    /// Kernel accessor for the dangerous API.
    /// </summary>
    internal AtmosKernel Kernel
    {
        get
        {
            ThrowIfDisposed();
            return _kernel;
        }
    }

    public AtmosSimulation(int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
        : this(new AtmosConfig(), chunkWidth, chunkHeight, chunkDepth)
    {
    }

    public AtmosSimulation(AtmosConfig config, int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkDepth);

        Config = config;
        _chunkWidth = chunkWidth;
        _chunkHeight = chunkHeight;
        _chunkDepth = chunkDepth;
        _kernel = new AtmosKernel(chunkWidth, chunkHeight, chunkDepth);
        _kernel.SetAtmosConfig(config);
    }

    /// <summary>
    ///     The live configuration used by the simulation.
    /// </summary>
    public AtmosConfig Config { get; private set; }

    /// <summary>
    ///     Number of chunks currently owned by the simulation.
    /// </summary>
    public int ChunkCount
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.ChunkCount;
        }
    }

    /// <summary>
    ///     Number of fixed simulation ticks processed since construction.
    /// </summary>
    public int TickCount
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.TickCount;
        }
    }

    /// <summary>
    ///     Timestamp ticks spent processing boundary flow during the most recent update.
    /// </summary>
    public long LastBoundaryTicks
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.LastBoundaryTicks;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _kernel.Dispose();
        _disposed = true;
    }

    /// <summary>
    ///     Advances the fixed-step simulation by an elapsed real-time duration.
    /// </summary>
    public void Update(float elapsedSeconds)
    {
        ThrowIfDisposed();
        _kernel.Update(elapsedSeconds);
    }

    /// <summary>
    ///     Sets the live configuration and advances the simulation.
    /// </summary>
    public void Update(float elapsedSeconds, AtmosConfig config)
    {
        SetAtmosConfig(config);
        Update(elapsedSeconds);
    }

    /// <summary>
    ///     Changes the live configuration used by subsequent updates and ticks.
    /// </summary>
    public void SetAtmosConfig(AtmosConfig config)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        _kernel.SetAtmosConfig(config);
    }

    /// <summary>
    ///     Creates a chunk using this simulation's configured chunk dimensions.
    /// </summary>
    public AtmosChunkHandle CreateAndRegisterChunk(Int3 position, int maxActiveRooms = 64)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActiveRooms);
        _kernel.CreateAndRegisterChunk(position, _chunkWidth, _chunkHeight, _chunkDepth, maxActiveRooms);
        return new AtmosChunkHandle(position);
    }

    /// <summary>
    ///     Removes a chunk and releases the kernel resources it owns.
    /// </summary>
    public bool UnregisterChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        return _kernel.UnregisterChunk(chunk.Position);
    }

    /// <summary>
    ///     Returns a detached copy of the current chunk state.
    /// </summary>
    public AtmosChunkSnapshot GetChunkSnapshot(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        return _kernel.GetChunkSnapshot(chunk.Position);
    }

    public void SetChunkClassification(AtmosChunkHandle chunk, VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetChunkClassification(chunk.Position, classification);
    }

    public void SetVoxelClassification(AtmosChunkHandle chunk, ushort localVoxelIndex,
        VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelClassification(chunk.Position, localVoxelIndex, classification);
    }

    public void SetVoxelClassification(AtmosChunkHandle chunk, int x, int y, int z,
        VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelClassification(chunk.Position, x, y, z, classification);
    }

    public void SetVoxelTemperature(AtmosChunkHandle chunk, ushort localVoxelIndex, float temperature)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelTemperature(chunk.Position, localVoxelIndex, temperature);
    }

    public void SetVoxelTemperature(AtmosChunkHandle chunk, int x, int y, int z, float temperature)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelTemperature(chunk.Position, x, y, z, temperature);
    }

    public void AddGasToVoxel(AtmosChunkHandle chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        ThrowIfDisposed();
        _kernel.AddGasToVoxel(chunk.Position, localVoxelIndex, gasId, moles, temperature);
    }

    public void AddGasToVoxel(AtmosChunkHandle chunk, int x, int y, int z, int gasId, float moles,
        float temperature)
    {
        ThrowIfDisposed();
        _kernel.AddGasToVoxel(chunk.Position, x, y, z, gasId, moles, temperature);
    }

    public void WakeRoom(AtmosChunkHandle chunk, int roomId)
    {
        ThrowIfDisposed();
        _kernel.WakeRoom(chunk.Position, roomId);
    }

    public void SleepChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        _kernel.SleepChunk(chunk.Position);
    }

    /// <summary>
    ///     Runs exactly one fixed simulation tick.
    /// </summary>
    public void Tick()
    {
        ThrowIfDisposed();
        _kernel.Tick();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
