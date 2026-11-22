using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.State.Advanced;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Gets host solver data for a slot, creating it on first use.
    /// </summary>
    /// <param name="descriptor">The slot's descriptor.</param>
    /// <param name="state">Passed to <paramref name="factory" />.</param>
    /// <param name="factory">Builds the value on a miss.</param>
    /// <returns>The cached value.</returns>
    /// <remarks>
    ///     Outside a tick, this first catches the tick configuration up with the applied one. It drops the data if the
    ///     configuration changed, so the value matches what the next tick sees. During a tick the applied
    ///     configuration cannot change, because the world defers changes to the next tick boundary.
    /// </remarks>
    internal T GetSolverData<T, TState>(SolverDataDescriptor descriptor, TState state, Func<TState, T> factory)
        where T : class
    {
        lock (StateGate)
        {
            if (!_isTickExecuting)
                CurrentTickConfig.Capture(_config);

            return CurrentTickConfig.SolverData.GetOrCreate(descriptor, state, factory);
        }
    }

    /// <summary>
    ///     Drops all solver data without disposing it.
    /// </summary>
    private void ClearSolverData()
    {
        CurrentTickConfig.SolverData.Clear();
    }

    internal T[] GetOrCreateChunkField<T>(Int3 position, ChunkFieldDescriptor<T> field)
    {
        lock (StateGate)
        {
            return GetChunk(position).GetOrCreateField(field);
        }
    }

    /// <summary>
    ///     Gets a chunk's storage for a field if the chunk has already allocated it.
    /// </summary>
    /// <param name="position">The chunk's position.</param>
    /// <param name="field">The field descriptor.</param>
    /// <param name="array">The live array, or <see langword="null" /> when the chunk has no storage for the field.</param>
    /// <returns><see langword="true" /> when the chunk has storage for <paramref name="field" />.</returns>
    /// <remarks>
    ///     This never allocates, so it leaves checkpoints and state hashes unchanged.
    /// </remarks>
    internal bool TryGetChunkField<T>(Int3 position, ChunkFieldDescriptor<T> field, [NotNullWhen(true)] out T[]? array)
    {
        lock (StateGate)
        {
            return GetChunk(position).TryGetField(field, out array);
        }
    }

    /// <summary>
    ///     Writes a run of elements into one chunk's field storage and records the operation.
    /// </summary>
    /// <param name="op">The write. The kernel validates all of it before it changes any state.</param>
    /// <remarks>
    ///     The write creates the storage when the chunk has none, which advances the chunk's revision once. The write
    ///     itself neither wakes the chunk nor advances its revision. An empty run changes nothing and records nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     No rollback field with the operation's element size is registered under its key.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the operation's position.</exception>
    /// <exception cref="ArgumentException">The values hold a partial element.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The run does not fit in the field's array.</exception>
    internal void WriteChunkField(WriteChunkFieldOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        lock (StateGate)
        {
            var field = ResolveRecordedChunkField(op.Key, op.ElementSize);
            var chunk = GetChunk(op.Position);
            if (op.Values.Length % op.ElementSize != 0)
                throw new ArgumentException($"The write to chunk field '{op.Key}' holds a partial element.", nameof(op));

            int count = op.Values.Length / op.ElementSize;
            int length = field.GetLength(chunk.VoxelCount);
            if (op.Start < 0 || op.Start > length - count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(op),
                    $"Chunk field '{op.Key}' has {length} elements, so {count} elements cannot start at index {op.Start}.");
            }

            if (count == 0)
                return;

            Span<byte> target = field.GetOrCreateBytes(chunk).Slice(op.Start * op.ElementSize, op.Values.Length);
            op.Values.Span.CopyTo(target);
            StateBinarySink.ReverseElements(target, op.ElementSize);
            if (ShouldRecord) RecordOperation(op);
        }
    }

    /// <summary>
    ///     Stores one element in every voxel of a global box for a per-voxel field and records the operation.
    /// </summary>
    /// <param name="op">The fill. The kernel validates all of it before it changes any state.</param>
    /// <remarks>
    ///     Chunks that do not exist are skipped. Every chunk the box touches gets the field's storage. Apart from
    ///     creating that storage, the fill neither wakes a chunk nor advances its revision.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     No per-voxel rollback field with the operation's element size is registered under its key.
    /// </exception>
    /// <exception cref="ArgumentException">The value is not one element, or the box minimum exceeds its maximum.</exception>
    internal void FillChunkFieldBox(FillChunkFieldBoxOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        lock (StateGate)
        {
            var field = ResolveRecordedChunkField(op.Key, op.ElementSize);
            if (field.FixedLength != null)
            {
                throw new InvalidOperationException(
                    $"The replay fills a box of chunk field '{op.Key}', but the field registered under that key has a " +
                    "fixed length. Only per-voxel fields can be filled by box.");
            }

            if (op.Value.Length != op.ElementSize)
                throw new ArgumentException($"The fill of chunk field '{op.Key}' does not hold exactly one element.", nameof(op));

            if (op.Min.X > op.Max.X || op.Min.Y > op.Max.Y || op.Min.Z > op.Max.Z)
                throw new ArgumentException("The box minimum must not exceed its maximum.", nameof(op));

            byte[] element = op.Value.ToArray();
            StateBinarySink.ReverseElements(element, op.ElementSize);
            foreach (var chunk in CollectBoxChunks(op.Min, op.Max))
                FillChunkFieldInChunk(chunk, field, op.Min, op.Max, element);

            if (ShouldRecord) RecordOperation(op);
        }
    }

    private static void FillChunkFieldInChunk(AtmosChunk chunk, ChunkFieldDescriptor field, Int3 min, Int3 max, byte[] element)
    {
        var dimensions = chunk.Dimensions;
        long originX = (long)chunk.GridPosition.X * dimensions.X;
        long originY = (long)chunk.GridPosition.Y * dimensions.Y;
        long originZ = (long)chunk.GridPosition.Z * dimensions.Z;
        int x0 = (int)(Math.Max(min.X, originX) - originX);
        int x1 = (int)(Math.Min(max.X, originX + dimensions.X - 1) - originX);
        int y0 = (int)(Math.Max(min.Y, originY) - originY);
        int y1 = (int)(Math.Min(max.Y, originY + dimensions.Y - 1) - originY);
        int z0 = (int)(Math.Max(min.Z, originZ) - originZ);
        int z1 = (int)(Math.Min(max.Z, originZ + dimensions.Z - 1) - originZ);

        Span<byte> bytes = field.GetOrCreateBytes(chunk);
        int size = element.Length;
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        {
            int row = dimensions.X * (y + dimensions.Y * z);
            for (int x = x0; x <= x1; x++)
                element.CopyTo(bytes.Slice((row + x) * size, size));
        }
    }

    /// <summary>
    ///     Finds the rollback field a recorded write names.
    /// </summary>
    /// <remarks>
    ///     The live API checks its field first, so these failures mean a replay was recorded against a different
    ///     registration.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The key is not registered, names a transient field, or names a field with another element size.
    /// </exception>
    private static ChunkFieldDescriptor ResolveRecordedChunkField(string key, int elementSize)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!ChunkFieldRegistry.TryGet(key, out var field))
        {
            throw new InvalidOperationException(
                $"The replay writes chunk field '{key}', but no field is registered under that key. Register the field " +
                "before replaying, or call the generated <assembly name>.NumosModule.EnsureRegistered() of the " +
                "assembly that declares it.");
        }

        if (!field.CaptureForRollback)
        {
            throw new InvalidOperationException(
                $"The replay writes chunk field '{key}', but the field registered under that key is transient.");
        }

        if (field.ElementSize != elementSize)
        {
            throw new InvalidOperationException(
                $"The replay writes {elementSize}-byte elements to chunk field '{key}', but the field registered " +
                $"under that key holds {field.ElementType.FullName} ({field.ElementSize} bytes).");
        }

        return field;
    }
}