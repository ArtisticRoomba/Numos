using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Describes one kind of per-chunk solver storage and the slot every chunk keeps it in.
/// </summary>
/// <remarks>
///     <para>
///         Descriptors are process-wide and never removed. A registered descriptor owns its <see cref="Key" />; a private
///         descriptor is only reachable through the instance that created it.
///     </para>
///     <para>
///         <see cref="Slot" /> is a process-local index assigned in registration order, which depends on static
///         initialization order. It must never reach checkpoints, hashes, snapshots, or any ordering that does. Those all
///         use <see cref="Key" />.
///     </para>
/// </remarks>
internal abstract class ChunkFieldDescriptor
{
    private object? _publicHandle;

    private protected ChunkFieldDescriptor(string key, int slot, bool captureForRollback, int? fixedLength, bool isPrivate)
    {
        Key = key;
        Slot = slot;
        CaptureForRollback = captureForRollback;
        FixedLength = fixedLength;
        IsPrivate = isPrivate;
    }

    /// <summary>
    ///     Gets the ordinal key for registered fields, or the debug name for private fields.
    /// </summary>
    internal string Key { get; }

    /// <summary>
    ///     Gets the index of this field's storage in each chunk.
    /// </summary>
    internal int Slot { get; }

    internal abstract Type ElementType { get; }

    internal bool CaptureForRollback { get; }

    /// <summary>
    ///     Gets the array length, or null for one element per voxel.
    /// </summary>
    internal int? FixedLength { get; }

    internal bool IsPrivate { get; }

    /// <summary>
    ///     Gets the public wrapper published for this descriptor, if any.
    /// </summary>
    internal object? PublicHandle => Volatile.Read(ref _publicHandle);

    /// <summary>
    ///     Publishes a public wrapper unless another thread published one first.
    /// </summary>
    /// <returns>The wrapper every caller should use, which may not be <paramref name="handle" />.</returns>
    internal object GetOrSetPublicHandle(object handle)
    {
        return Interlocked.CompareExchange(ref _publicHandle, handle, null) ?? handle;
    }

    internal int GetLength(int voxelCount)
    {
        return FixedLength ?? voxelCount;
    }

    internal abstract SolverArrayStorage Allocate(int voxelCount);

    internal string Describe()
    {
        return Describe(ElementType, CaptureForRollback, FixedLength);
    }

    internal static string Describe(Type elementType, bool captureForRollback, int? fixedLength)
    {
        string length = fixedLength is { } count ? $"{count} elements" : "one element per voxel";
        string policy = captureForRollback ? "captured for rollback" : "transient";
        return $"{elementType.FullName} ({length}, {policy})";
    }
}

/// <inheritdoc />
internal sealed class ChunkFieldDescriptor<T> : ChunkFieldDescriptor
{
    internal ChunkFieldDescriptor(string key, int slot, bool captureForRollback, int? fixedLength, bool isPrivate)
        : base(key, slot, captureForRollback, fixedLength, isPrivate)
    {
    }

    internal override Type ElementType => typeof(T);

    internal override SolverArrayStorage Allocate(int voxelCount)
    {
        return new SolverArrayStorage<T>(new T[GetLength(voxelCount)], CaptureForRollback) { Field = this };
    }

    internal bool Matches(bool captureForRollback, int? fixedLength)
    {
        return CaptureForRollback == captureForRollback && FixedLength == fixedLength;
    }
}

/// <summary>
///     Assigns chunk storage slots to field descriptors.
/// </summary>
/// <remarks>
///     The registry is process-wide, so keys are shared by every simulation and every assembly loaded into the process.
///     Namespace them. Slots are never reclaimed.
/// </remarks>
internal static class ChunkFieldRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, ChunkFieldDescriptor> ByKey = new(StringComparer.Ordinal);
    private static ChunkFieldDescriptor[] _bySlot = new ChunkFieldDescriptor[16];
    private static int _slotCount;

    /// <summary>
    ///     Gets the number of slots assigned so far.
    /// </summary>
    internal static int SlotCount => Volatile.Read(ref _slotCount);

    /// <summary>
    ///     Gets the descriptor for a key, creating it on first registration.
    /// </summary>
    /// <param name="key">The ordinal key. Captured fields persist it in checkpoints, so it must stay stable.</param>
    /// <param name="captureForRollback">Whether chunks capture and restore the field.</param>
    /// <param name="length">The array length, or null for one element per voxel.</param>
    /// <returns>The descriptor shared by every registration of the same definition.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="key" /> is blank, or a captured <typeparamref name="T" /> contains managed references.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="InvalidOperationException">The key is registered with a different definition.</exception>
    internal static ChunkFieldDescriptor<T> Register<T>(string key, bool captureForRollback, int? length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (length is { } count)
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(length));

        if (captureForRollback && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new ArgumentException(
                $"Chunk field '{key}' is captured for rollback, so {typeof(T).FullName} cannot contain managed references.",
                nameof(captureForRollback));
        }

        lock (Gate)
        {
            if (ByKey.TryGetValue(key, out var existing))
            {
                if (existing is ChunkFieldDescriptor<T> typed && typed.Matches(captureForRollback, length))
                    return typed;

                throw new InvalidOperationException(
                    $"Chunk field '{key}' is already registered as {existing.Describe()} and cannot be registered " +
                    $"again as {ChunkFieldDescriptor.Describe(typeof(T), captureForRollback, length)}.");
            }

            var descriptor = new ChunkFieldDescriptor<T>(key, _slotCount, captureForRollback, length, false);
            AddSlot(descriptor);
            ByKey.Add(key, descriptor);
            return descriptor;
        }
    }

    /// <summary>
    ///     Creates a transient field that is only reachable through the returned descriptor.
    /// </summary>
    /// <param name="debugName">A name for diagnostics. It is not a key and may repeat.</param>
    /// <param name="length">The array length, or null for one element per voxel.</param>
    /// <returns>A new descriptor with its own slot.</returns>
    /// <remarks>
    ///     Every call consumes a slot for the life of the process, so create private fields once rather than per tick or
    ///     per simulation.
    /// </remarks>
    internal static ChunkFieldDescriptor<T> CreatePrivate<T>(string debugName, int? length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debugName);
        if (length is { } count)
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(length));

        lock (Gate)
        {
            var descriptor = new ChunkFieldDescriptor<T>(debugName, _slotCount, false, length, true);
            AddSlot(descriptor);
            return descriptor;
        }
    }

    internal static bool TryGetByKey(string key, [NotNullWhen(true)] out ChunkFieldDescriptor? descriptor)
    {
        lock (Gate)
        {
            return ByKey.TryGetValue(key, out descriptor);
        }
    }

    internal static ChunkFieldDescriptor GetBySlot(int slot)
    {
        // Read the count first: it is published after the array that holds it.
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);
        return Volatile.Read(ref _bySlot)[slot];
    }

    private static void AddSlot(ChunkFieldDescriptor descriptor)
    {
        if (_slotCount == _bySlot.Length)
        {
            var grown = new ChunkFieldDescriptor[_bySlot.Length * 2];
            Array.Copy(_bySlot, grown, _slotCount);
            Volatile.Write(ref _bySlot, grown);
        }

        _bySlot[_slotCount] = descriptor;
        Volatile.Write(ref _slotCount, _slotCount + 1);
    }
}