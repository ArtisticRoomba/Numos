using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Declares a <see cref="ChunkField{T}" /> as a static partial property and has the <c>Numos.API</c> source
///     generator register it and add a typed accessor for it to <see cref="AtmosSolverChunk" />.
/// </summary>
/// <remarks>
///     <para>
///         Put the attribute on a <see langword="static" /> <see langword="partial" /> property declaration of type
///         <see cref="ChunkField{T}" /> with only a <see langword="get" /> accessor. The containing type and every
///         type it is nested in must be <see langword="partial" /> and non-generic. The property must be
///         <see langword="public" /> or <see langword="internal" />.
///         The generator supplies the property body. The body calls <see cref="ChunkField{T}.Register" /> once from a
///         nested holder class, so the field is ready before any static initializer of the declaring type can
///         observe it.
///     </para>
///     <para>
///         The generator also emits a static class named after the declaring type (<c>FireChunkDataChunkAccessors</c>
///         for <c>FireChunkData</c>, <c>OuterInnerChunkAccessors</c> for a nested <c>Outer.Inner</c>) in the same
///         namespace. The class holds one extension property per field.
///         Per-voxel fields return <see cref="Numos.Collections.FlatArray{T}" />. Fields with a
///         <see cref="Length" /> return <see cref="Span{T}" />.
///         When the project references <c>Numos.API.Dangerous</c>, the generator adds the same accessors to its chunk
///         view. Projects on C# 13 get <c>GetExposure()</c>-style extension methods instead, because extension
///         properties need C# 14.
///     </para>
///     <para>
///         The generated code only calls public API. A field declared this way is interchangeable with one created by
///         calling <see cref="ChunkField{T}.Register" /> directly with the same definition.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     public static partial class FireChunkData
///     {
///         // Survives checkpoints and rollback, so the key is a compatibility contract.
///         [ChunkData("my-mod/fire/exposure", Rollback = true)]
///         public static partial ChunkField&lt;float&gt; Exposure { get; }
///
///         // One counter per chunk rather than one per voxel.
///         [ChunkData("my-mod/fire/burn-count", Rollback = true, Length = 1)]
///         public static partial ChunkField&lt;int&gt; BurnCount { get; }
///
///         // Scratch space; keyed "MyMod.FireChunkData.Scratch" and cleared on restore.
///         [ChunkData]
///         public static partial ChunkField&lt;float&gt; Scratch { get; }
///     }
///
///     world.Solvers.Register("my-mod/fire", context =>
///     {
///         foreach (var simulation in context.Simulations)
///         {
///             foreach (var chunk in context.Chunks(simulation))
///             {
///                 FlatArray&lt;float&gt; exposure = chunk.Exposure;
///                 Span&lt;int&gt; burnCount = chunk.BurnCount;
///                 for (int voxel = 0; voxel &lt; chunk.VoxelCount; voxel++)
///                     exposure[voxel] += 1f;
///                 burnCount[0]++;
///             }
///         }
///     });
///     </code>
/// </example>
[PublicAPI]
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ChunkDataAttribute : Attribute
{
    /// <summary>
    ///     Declares a transient field keyed by the property's full name, <c>{Namespace}.{Type}.{Property}</c>.
    /// </summary>
    /// <remarks>
    ///     The derived key changes whenever the property, its type, or its namespace is renamed. Fields with
    ///     <see cref="Rollback" /> set must therefore use <see cref="ChunkDataAttribute(string)" />.
    /// </remarks>
    public ChunkDataAttribute()
    {
    }

    /// <summary>
    ///     Declares a field with an explicit registry key.
    /// </summary>
    /// <param name="key">
    ///     The key passed to <see cref="ChunkField{T}.Register" />. Namespace it (for example
    ///     <c>my-mod/fire/exposure</c>), since the registry is shared by every assembly in the process. It must not be
    ///     blank.
    /// </param>
    public ChunkDataAttribute(string key)
    {
        Key = key;
    }

    /// <summary>
    ///     Gets the explicit registry key, or <see langword="null" /> when the key is derived from the property name.
    /// </summary>
    public string? Key { get; }

    /// <summary>
    ///     Gets or sets whether the field is captured in checkpoints and state hashes and restored on rollback.
    /// </summary>
    /// <remarks>
    ///     Rollback fields need an explicit <see cref="Key" /> and an unmanaged element type, because the key and the
    ///     element layout are stored in checkpoints.
    /// </remarks>
    public bool Rollback { get; set; }

    /// <summary>
    ///     Gets or sets a fixed array length for the field, in place of one element per voxel.
    /// </summary>
    /// <remarks>
    ///     The generator checks whether the property was assigned, not its value. Leave it out for a per-voxel field.
    ///     Set it (zero is allowed) for a fixed-size one.
    ///     Reading it at run time cannot tell an unset length from an explicit zero. Use
    ///     <see cref="ChunkField{T}.Length" /> on the registered field instead.
    /// </remarks>
    public int Length { get; set; }

    /// <summary>
    ///     Gets or sets the name of the generated chunk accessor, when it should differ from the property name.
    /// </summary>
    /// <remarks>
    ///     Use it to avoid a clash with a member of <see cref="AtmosSolverChunk" /> or with another field's accessor
    ///     in the same namespace. On C# 13 the generated method is named <c>Get{AccessorName}</c>.
    /// </remarks>
    public string? AccessorName { get; set; }
}