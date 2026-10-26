using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.Solvers;

internal abstract class SolverArrayStorage(ChunkFieldDescriptor field)
{
    /// <summary>
    ///     Gets the descriptor that owns this storage.
    /// </summary>
    internal ChunkFieldDescriptor Field { get; } = field;

    internal Type ElementType => Field.ElementType;
    internal bool CaptureForRollback => Field.CaptureForRollback;
    internal abstract int Length { get; }
    internal abstract long PayloadBytes { get; }

    internal abstract SolverArrayStorage Clone();

    /// <summary>
    ///     Gets the values' bytes in native layout, including any struct padding.
    /// </summary>
    internal abstract ReadOnlySpan<byte> Bytes { get; }

    /// <summary>
    ///     Resets one element to default. Indices past the end of a fixed-length array are ignored.
    /// </summary>
    internal abstract void ClearElement(int index);

    /// <summary>
    ///     Resets every element to default.
    /// </summary>
    internal abstract void ClearAll();

    /// <summary>
    ///     Writes the values as one block through <see cref="IStateSink.WriteElements" />, without a count.
    /// </summary>
    /// <remarks>
    ///     A sink swaps each element on a big-endian host, which is only meaningful for portable element types. Struct
    ///     elements still hash this way, in their native layout, but can't be saved.
    /// </remarks>
    internal void WriteValues<TSink>(ref TSink sink) where TSink : struct, IStateSink
    {
        sink.WriteElements(Bytes, Field.ElementSize);
    }
}

internal sealed class SolverArrayStorage<T>(ChunkFieldDescriptor<T> field, T[] values) : SolverArrayStorage(field)
{
    internal T[] Values { get; } = values;
    internal override int Length => Values.Length;
    internal override long PayloadBytes => (long)Values.Length * Unsafe.SizeOf<T>();

    internal override SolverArrayStorage Clone()
    {
        // Captured elements contain no references, so an array clone detaches their entire state.
        return new SolverArrayStorage<T>((ChunkFieldDescriptor<T>)Field, (T[])Values.Clone());
    }

    internal override void ClearElement(int index)
    {
        if ((uint)index < (uint)Values.Length)
            Values[index] = default!;
    }

    internal override void ClearAll()
    {
        Array.Clear(Values);
    }

    internal static Span<byte> AsBytes(T[] values)
    {
        // Registration rejects managed references for captured fields, so the elements are plain bytes.
        return MemoryMarshal.CreateSpan(
            ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(values)),
            checked(values.Length * Unsafe.SizeOf<T>()));
    }

    internal override ReadOnlySpan<byte> Bytes => AsBytes(Values);
}