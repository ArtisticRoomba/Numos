using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Describes one kind of per-chunk solver storage and the slot every chunk keeps it in.
/// </summary>
/// <remarks>
///     <para>
///         Descriptors are process-wide and never removed, and each one owns its <see cref="Key" />.
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

    private protected ChunkFieldDescriptor(
        string key, int slot, bool captureForRollback, int? fixedLength, bool clearOnSleep, bool clearOnVacuum)
    {
        Key = key;
        Slot = slot;
        CaptureForRollback = captureForRollback;
        FixedLength = fixedLength;
        ClearOnSleep = clearOnSleep;
        ClearOnVacuum = clearOnVacuum;
    }

    /// <summary>
    ///     Gets whether a rollback field of this element type can be written to a portable replay file.
    /// </summary>
    /// <remarks>
    ///     Only primitives and enums over them qualify: their bytes have no padding, and swapping each element's bytes
    ///     converts them to little-endian. Structs are left out, since their layout and padding aren't pinned.
    /// </remarks>
    internal bool IsPortable => IsPortableElementType(ElementType);

    /// <summary>
    ///     Gets the ordinal key.
    /// </summary>
    internal string Key { get; }

    /// <summary>
    ///     Gets the index of this field's storage in each chunk.
    /// </summary>
    internal int Slot { get; }

    internal abstract Type ElementType { get; }

    internal abstract int ElementSize { get; }

    internal bool CaptureForRollback { get; }

    /// <summary>
    ///     Gets the array length, or null for one element per voxel.
    /// </summary>
    internal int? FixedLength { get; }

    /// <summary>
    ///     Gets whether <see cref="AtmosChunk.Sleep" /> resets this field's storage to default.
    /// </summary>
    internal bool ClearOnSleep { get; }

    /// <summary>
    ///     Gets whether clearing a voxel or chunk to vacuum resets this field's storage to default.
    /// </summary>
    internal bool ClearOnVacuum { get; }

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

    /// <summary>
    ///     Reads <paramref name="length" /> little-endian elements written by <see cref="SolverArrayStorage.WriteValues{TSink}" />.
    /// </summary>
    /// <remarks>Only valid for portable element types.</remarks>
    /// <exception cref="InvalidDataException">The data holds fewer elements.</exception>
    internal abstract SolverArrayStorage Read(ref StateReader reader, int length);

    internal int GetLength(int voxelCount)
    {
        return FixedLength ?? voxelCount;
    }

    internal string Describe()
    {
        return Describe(ElementType, CaptureForRollback, FixedLength, ClearOnSleep, ClearOnVacuum);
    }

    internal static bool IsPortableElementType(Type type)
    {
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);

        return type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) || type == typeof(Half) || type == typeof(float) ||
               type == typeof(double);
    }

    internal static string Describe(Type elementType, bool captureForRollback, int? fixedLength, bool clearOnSleep, bool clearOnVacuum)
    {
        string length = fixedLength is { } count ? $"{count} elements" : "one element per voxel";
        string policy = captureForRollback ? "captured for rollback" : "transient";
        string sleep = clearOnSleep ? "cleared on sleep" : "kept on sleep";
        string vacuum = clearOnVacuum ? "cleared on vacuum" : "kept on vacuum";
        return $"{elementType.FullName} ({length}, {policy}, {sleep}, {vacuum})";
    }
}

/// <inheritdoc />
internal sealed class ChunkFieldDescriptor<T> : ChunkFieldDescriptor
{
    internal ChunkFieldDescriptor(
        string key, int slot, bool captureForRollback, int? fixedLength, bool clearOnSleep, bool clearOnVacuum)
        : base(key, slot, captureForRollback, fixedLength, clearOnSleep, clearOnVacuum)
    {
    }

    internal override Type ElementType => typeof(T);

    internal SolverArrayStorage<T> Allocate(int voxelCount)
    {
        return new SolverArrayStorage<T>(this, new T[GetLength(voxelCount)]);
    }

    internal override SolverArrayStorage Read(ref StateReader reader, int length)
    {
        // Checked before allocating, so a corrupt length can't ask for more than the data holds.
        if ((long)length * Unsafe.SizeOf<T>() > reader.Remaining)
            throw new InvalidDataException($"Chunk field '{Key}' is truncated.");

        var values = new T[length];
        reader.ReadElements(SolverArrayStorage<T>.AsBytes(values), Unsafe.SizeOf<T>());
        return new SolverArrayStorage<T>(this, values);
    }

    internal override int ElementSize => Unsafe.SizeOf<T>();

    internal bool Matches(bool captureForRollback, int? fixedLength, bool clearOnSleep, bool clearOnVacuum)
    {
        return CaptureForRollback == captureForRollback &&
               FixedLength == fixedLength &&
               ClearOnSleep == clearOnSleep &&
               ClearOnVacuum == clearOnVacuum;
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
    /// <param name="clearOnSleep">Whether the array is reset to default when a chunk sleeps.</param>
    /// <param name="clearOnVacuum">Whether the array is reset to default when a voxel becomes a vacuum.</param>
    /// <returns>The descriptor shared by every registration of the same definition.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="key" /> is blank, or a captured <typeparamref name="T" /> contains managed references.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="InvalidOperationException">The key is registered with a different definition.</exception>
    internal static ChunkFieldDescriptor<T> Register<T>(
        string key, bool captureForRollback = false, int? length = null, bool clearOnSleep = false, bool clearOnVacuum = false)
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
                if (existing is ChunkFieldDescriptor<T> typed && typed.Matches(captureForRollback, length, clearOnSleep, clearOnVacuum))
                    return typed;

                throw new InvalidOperationException(
                    $"Chunk field '{key}' is already registered as {existing.Describe()} and cannot be registered " +
                    $"again as {ChunkFieldDescriptor.Describe(typeof(T), captureForRollback, length, clearOnSleep, clearOnVacuum)}.");
            }

            int slot = _slotCount;
            Volatile.Write(ref _slotCount, slot + 1);
            var descriptor = new ChunkFieldDescriptor<T>(key, slot, captureForRollback, length, clearOnSleep, clearOnVacuum);
            ByKey.Add(key, descriptor);
            return descriptor;
        }
    }

    /// <summary>
    ///     Finds the descriptor registered under <paramref name="key" />, if any.
    /// </summary>
    internal static bool TryGet(string key, [NotNullWhen(true)] out ChunkFieldDescriptor? descriptor)
    {
        lock (Gate)
            return ByKey.TryGetValue(key, out descriptor);
    }
}