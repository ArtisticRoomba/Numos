using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Numos.CoreSim.Replay;

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

    internal abstract void AppendHash(ref AtmosStateHasher hash);

    /// <summary>
    ///     Resets one element to default. Indices past the end of a fixed-length array are ignored.
    /// </summary>
    internal abstract void ClearElement(int index);

    /// <summary>
    ///     Resets every element to default.
    /// </summary>
    internal abstract void ClearAll();

    /// <summary>
    ///     Writes the values as one little-endian byte run, without a length prefix.
    /// </summary>
    /// <remarks>Only valid for portable element types, where reversing each element's bytes converts its endianness.</remarks>
    internal abstract void Write(BinaryWriter writer);
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

    internal override void Write(BinaryWriter writer)
    {
        Span<byte> bytes = AsBytes(Values);
        if (BitConverter.IsLittleEndian)
        {
            writer.Write(bytes);
            return;
        }

        byte[] swapped = bytes.ToArray();
        AtmosReplayCodec.ReverseElementBytes(swapped, Unsafe.SizeOf<T>());
        writer.Write(swapped);
    }

    internal override void AppendHash(ref AtmosStateHasher hash)
    {
        hash.Add(typeof(T).FullName);
        hash.Add(Unsafe.SizeOf<T>());
        hash.Add(Length);
        foreach (ref var value in Values.AsSpan())
        {
            // Registration rejects managed references before storage can enter a checkpoint.
            // Include the exact representation, including NaN payloads and custom value-type layout.
            ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref value), Unsafe.SizeOf<T>());
            foreach (byte part in bytes)
                hash.AddByte(part);
        }
    }
}