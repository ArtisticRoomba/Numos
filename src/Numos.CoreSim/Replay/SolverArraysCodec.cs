using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.Replay;

/// <summary>
///     The <see cref="NumosSerializeAttribute.Codec" /> for <see cref="AtmosChunkCheckpoint.SolverArrays" />, the chunk's
///     rollback <c>ChunkField&lt;T&gt;</c> arrays.
/// </summary>
/// <remarks>
///     <para>
///         A count, then for each array in ordinal key order: its key, the element type's full name, the element size
///         and count as <c>int32</c>, and the elements as one little-endian block. The state hash sees the same calls,
///         so a struct-typed array hashes its native bytes even though it can't be saved;
///         <see cref="AtmosReplayCodec" /> rejects those before writing.
///     </para>
///     <para>
///         Reading maps each key back to its registered field. The length is only checked against the data here; the
///         chunk's dimensions aren't known yet, so <see cref="AtmosChunkCheckpoint.OnDecoded" /> checks it against
///         the registration afterwards.
///     </para>
/// </remarks>
internal readonly struct SolverArraysCodec : IStateCodec<IReadOnlyList<AtmosSolverArraySnapshot>>
{
    public static void Write<TSink>(ref TSink sink, IReadOnlyList<AtmosSolverArraySnapshot> value)
        where TSink : struct, IStateSink
    {
        sink.WriteCount(value.Count);
        foreach (var array in value)
        {
            sink.WriteString(array.Key);
            sink.WriteString(array.ElementType.FullName!);
            sink.WriteInt32(array.ElementSize);
            sink.WriteInt32(array.Length);
            array.WriteValues(ref sink);
        }
    }

    public static bool StateEquals(IReadOnlyList<AtmosSolverArraySnapshot> left, IReadOnlyList<AtmosSolverArraySnapshot> right) =>
        Compare(left, right) == 0;

    public static int Compare(IReadOnlyList<AtmosSolverArraySnapshot> left, IReadOnlyList<AtmosSolverArraySnapshot> right)
    {
        int result = left.Count.CompareTo(right.Count);
        for (int i = 0; result == 0 && i < left.Count; i++)
        {
            result = string.CompareOrdinal(left[i].Key, right[i].Key);
            if (result == 0)
                result = string.CompareOrdinal(left[i].ElementType.FullName, right[i].ElementType.FullName);

            if (result == 0)
                result = left[i].CompareValues(right[i]);
        }

        return result;
    }

    /// <exception cref="InvalidDataException">An array doesn't match its registered field, or the data is malformed.</exception>
    /// <exception cref="NotSupportedException">A key has no registered field.</exception>
    public static IReadOnlyList<AtmosSolverArraySnapshot> Read(ref StateReader reader, IGasRegistry? gases)
    {
        int count = reader.ReadCount();
        if (count == 0)
            return [];

        var arrays = new List<AtmosSolverArraySnapshot>(Math.Min(count, 64));
        string? previousKey = null;
        for (int index = 0; index < count; index++)
        {
            string key = reader.ReadString();
            if (previousKey != null && string.CompareOrdinal(previousKey, key) >= 0)
                throw new InvalidDataException("Chunk field arrays are not in ascending ordinal key order.");

            previousKey = key;
            string typeName = reader.ReadString();
            int size = reader.ReadInt32();
            int length = reader.ReadInt32();
            if (!ChunkFieldRegistry.TryGet(key, out var field))
            {
                throw new NotSupportedException(
                    $"The file contains chunk field '{key}', but no field is registered under that key. [ChunkData] " +
                    "fields register once their assembly has run; call ChunkField<T>.Register for other fields before loading.");
            }

            if (!field.CaptureForRollback)
                throw new InvalidDataException($"Chunk field '{key}' is saved in the file but registered without rollback.");

            if (field.ElementType.FullName != typeName || field.ElementSize != size)
            {
                throw new InvalidDataException(
                    $"Chunk field '{key}' was saved as {typeName} ({size} bytes) but is registered as " +
                    $"{field.ElementType.FullName} ({field.ElementSize} bytes).");
            }

            if (!field.IsPortable)
                throw new InvalidDataException($"Chunk field '{key}' is registered as {typeName}, which isn't a portable element type.");

            if (length < 0)
                throw new InvalidDataException($"Chunk field '{key}' has a negative length.");

            arrays.Add(AtmosSolverArraySnapshot.Adopt(field.Read(ref reader, length)));
        }

        return arrays.AsReadOnly();
    }

    public static IReadOnlyList<AtmosSolverArraySnapshot> Default => [];
}
