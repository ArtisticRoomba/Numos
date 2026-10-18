using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Numos.CoreSim.Replay;

namespace Numos.CoreSim.Solvers;

internal abstract class SolverArrayStorage
{
    internal abstract Type ElementType { get; }
    internal abstract int Length { get; }
    internal abstract long PayloadBytes { get; }
    internal abstract bool CaptureForRollback { get; }

    /// <summary>
    ///     Gets the descriptor that owns this storage. Only storage rebuilt from a snapshot that carries no descriptor
    ///     can be null, and restore binds it by key before the chunk sees it.
    /// </summary>
    internal ChunkFieldDescriptor? Field { get; init; }

    internal abstract SolverArrayStorage Clone();

    /// <summary>
    ///     Returns storage sharing these values but owned by <paramref name="field" />.
    /// </summary>
    internal abstract SolverArrayStorage WithField(ChunkFieldDescriptor field);
    internal abstract void AppendHash(ref AtmosStateHasher hash);
}

internal sealed class SolverArrayStorage<T>(T[] values, bool captureForRollback) : SolverArrayStorage
{
    internal T[] Values { get; } = values;
    internal override Type ElementType => typeof(T);
    internal override int Length => Values.Length;
    internal override long PayloadBytes => (long)Values.Length * Unsafe.SizeOf<T>();
    internal override bool CaptureForRollback => captureForRollback;

    internal override SolverArrayStorage Clone()
    {
        // Captured elements contain no references, so an array clone detaches their entire state.
        return new SolverArrayStorage<T>((T[])Values.Clone(), CaptureForRollback) { Field = Field };
    }

    internal override SolverArrayStorage WithField(ChunkFieldDescriptor field)
    {
        return new SolverArrayStorage<T>(Values, CaptureForRollback) { Field = field };
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

internal sealed class SolverArrayKeyComparer : IEqualityComparer<object>
{
    internal readonly static SolverArrayKeyComparer Instance = new();

    public new bool Equals(object? x, object? y)
    {
        return x is string left && y is string right
            ? StringComparer.Ordinal.Equals(left, right)
            : ReferenceEquals(x, y);
    }

    public int GetHashCode(object obj)
    {
        return obj is string name ? StringComparer.Ordinal.GetHashCode(name) : RuntimeHelpers.GetHashCode(obj);
    }
}