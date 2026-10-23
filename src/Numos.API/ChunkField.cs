using JetBrains.Annotations;
using Numos.Collections;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Names one kind of custom per-chunk storage that solvers attach to every chunk.
/// </summary>
/// <typeparam name="T">
///     The element type. Rollback fields cannot use a type that is or contains a managed reference.
/// </typeparam>
/// <remarks>
///     <para>
///         A field is a typed key, not storage. Each chunk allocates its own array the first time the field is read
///         through <see cref="AtmosSolverChunk.Get{T}" /> or <see cref="AtmosSimulation.GetChunkField{T}" />. The
///         array starts filled with default values. Allocation is lazy, so a field nobody touches adds nothing to
///         checkpoints or state hashes.
///     </para>
///     <para>
///         Fields are process-wide. Every simulation, world, and assembly in the process shares one registry, so a
///         <see cref="Key" /> registered by one mod is the same field everywhere. Namespace keys (for example
///         <c>my-mod/fire/exposure</c>). Store fields in <see langword="static readonly" /> members.
///         Each new key takes a slot that Numos never reclaims, so do not register fields per tick or per simulation.
///     </para>
///     <para>
///         Checkpoints and state hashes capture rollback fields by <see cref="Key" />. The key, element type,
///         length, and the element type's memory layout are therefore a compatibility contract with saved
///         checkpoints. Restore discards transient fields, and they start empty again.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     static class FireData
///     {
///         public static readonly ChunkField&lt;float&gt; Exposure =
///             ChunkField&lt;float&gt;.Register("my-mod/fire/exposure", rollback: true);
///     }
///
///     world.Solvers.Register("my-mod/fire", context =>
///     {
///         foreach (var simulation in context.Simulations)
///         {
///             foreach (var chunk in context.Chunks(simulation))
///             {
///                 Span&lt;float&gt; exposure = chunk.Get(FireData.Exposure).AsSpan();
///                 for (int voxel = 0; voxel &lt; exposure.Length; voxel++)
///                     exposure[voxel] += 1f;
///             }
///         }
///     });
///     </code>
/// </example>
[PublicAPI]
public sealed class ChunkField<T>
{
    private ChunkField(ChunkFieldDescriptor<T> descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>
    ///     Gets the registered key.
    /// </summary>
    public string Key => Descriptor.Key;

    /// <summary>
    ///     Gets whether chunks capture this field in checkpoints and state hashes and restore it on rollback.
    /// </summary>
    public bool Rollback => Descriptor.CaptureForRollback;

    /// <summary>
    ///     Gets the fixed array length, or <see langword="null" /> when the field holds one element per voxel.
    /// </summary>
    public int? Length => Descriptor.FixedLength;

    internal ChunkFieldDescriptor<T> Descriptor { get; }

    /// <summary>
    ///     Registers a keyed field, or returns the existing one when the same definition is already registered.
    /// </summary>
    /// <param name="key">
    ///     An ordinal key shared by every caller that means the same field. Rollback fields persist it in checkpoints,
    ///     so it must stay stable across versions.
    /// </param>
    /// <param name="rollback">
    ///     <see langword="true" /> to capture the field in checkpoints and state hashes and restore it on rollback;
    ///     <see langword="false" /> for scratch storage that starts empty after a restore.
    /// </param>
    /// <param name="length">
    ///     The array length for a fixed-size field, or <see langword="null" /> for one element per voxel. Zero is
    ///     allowed.
    /// </param>
    /// <returns>
    ///     The field for <paramref name="key" />. Repeated registrations of the same definition return the same
    ///     instance.
    /// </returns>
    /// <remarks>
    ///     Registration is thread-safe. Registering a key that already exists with a different element type, length,
    ///     or rollback policy throws instead of silently sharing incompatible storage.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="key" /> is null, empty, or whitespace, or <paramref name="rollback" /> is set and
    ///     <typeparamref name="T" /> contains managed references.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="key" /> is already registered with a different definition.
    /// </exception>
    /// <example>
    ///     <code>
    ///     public static readonly ChunkField&lt;int&gt; BurnCount =
    ///         ChunkField&lt;int&gt;.Register("my-mod/fire/burn-count", rollback: true, length: 1);
    ///     </code>
    /// </example>
    public static ChunkField<T> Register(string key, bool rollback = false, int? length = null)
    {
        return Wrap(ChunkFieldRegistry.Register<T>(key, rollback, length));
    }

    /// <summary>
    ///     Returns the key and definition of this field.
    /// </summary>
    public override string ToString()
    {
        return $"{Key}: {Descriptor.Describe()}";
    }

    /// <summary>
    ///     Wraps a chunk's array for this field: chunk-shaped when per-voxel, one-dimensional otherwise.
    /// </summary>
    internal FlatArray<T> CreateView(T[] values, Int3 chunkDimensions)
    {
        return Length is null ? new FlatArray<T>(values, chunkDimensions) : new FlatArray<T>(values);
    }

    /// <summary>
    ///     Gets a view of this field's array in <paramref name="chunk" />, creating the array on first use.
    /// </summary>
    /// <remarks>Takes no lock. The caller must ensure the chunk is safe to touch.</remarks>
    internal FlatArray<T> View(AtmosChunk chunk)
    {
        return CreateView(chunk.GetOrCreateField(Descriptor), chunk.Dimensions);
    }

    private static ChunkField<T> Wrap(ChunkFieldDescriptor<T> descriptor)
    {
        if (descriptor.PublicHandle is ChunkField<T> existing)
            return existing;

        return (ChunkField<T>)descriptor.GetOrSetPublicHandle(new ChunkField<T>(descriptor));
    }
}