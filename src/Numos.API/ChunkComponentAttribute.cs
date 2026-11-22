using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Declares a <see langword="partial" /> struct as a chunk component, a named group of chunk fields that the
///     <c>Numos.API</c> source generator registers and exposes as one view.
/// </summary>
/// <remarks>
///     <para>
///         The struct is a schema. Numos never stores an array of it. Each instance field becomes one
///         <see cref="ChunkField{T}" />, keyed <c>{key}/{wire name}</c>. The wire name is the field name, or the name
///         passed to <c>[NumosSerialize("...")]</c>. Every instance field must carry one of two marks:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>[NumosSerialize]</c> makes a saved field. Checkpoints, state hashes, and <c>.numos</c> files hold
///                 it, so its element type must be a primitive or an enum.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>[NumosScratch]</c> makes a transient field. A restore clears it, and nothing saves it. Its element
///                 type must be unmanaged.
///             </description>
///         </item>
///     </list>
///     <para>
///         A field holds one value per voxel. <see cref="PerChunkAttribute" /> gives it a fixed number of values per
///         chunk instead. An unmarked instance field is a build warning, and the generator leaves it out. A static field,
///         a property, or a field with <c>[NumosIgnore]</c> cannot be a component field.
///     </para>
///     <para>
///         The generator adds two nested types to the struct. <c>Fields</c> holds one <see cref="ChunkField{T}" />
///         property per field, for host code and for <see cref="AtmosSolverChunk.Get{T}" />. <c>View</c> is a
///         <see langword="ref struct" /> with one live <see cref="Span{T}" /> per field. No member of the struct can be
///         named <c>Fields</c> or <c>View</c>.
///     </para>
///     <para>
///         The generator also adds C# 14 extension members, named after the struct or <see cref="AccessorName" />.
///         For a struct named <c>Fire</c> they are:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>chunk.GetFire()</c> returns the view. It creates the storage of every field that the chunk does
///                 not have yet.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>chunk.TryGetFire(out Fire.View view)</c> returns <see langword="false" /> when the chunk has no
///                 storage for the first declared <c>[NumosSerialize]</c> field, or for the first field when none is
///                 saved. It creates nothing in that case. A restore drops scratch storage, so a saved field gives the
///                 same answer before and after one. A component whose fields are all scratch therefore reads as absent
///                 after every restore, until a call such as <c>chunk.GetFire()</c> creates the storage again.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>chunk.HasFire</c> gives the same answer as <c>TryGetFire</c> without the view.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>chunks.WithFire()</c> on <see cref="AtmosSolverChunks" /> enumerates only the chunks that have
///                 the component. Each item has <c>Chunk</c> and <c>Fire</c> and deconstructs into both.
///             </description>
///         </item>
///     </list>
///     <para>
///         <see cref="AtmosSolverGasMixturePage" /> gets the same <c>GetFire</c>, <c>TryGetFire</c>, and
///         <c>HasFire</c>. A module initializer registers the fields when the assembly loads, as for
///         <see cref="ChunkDataAttribute" />. A component field and a <see cref="ChunkDataAttribute" /> property
///         with the same key are the same field.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [ChunkComponent("my-mod/fire", ClearOnVacuum = true)]
///     public partial struct Fire
///     {
///         [NumosSerialize] public float Exposure;          // my-mod/fire/Exposure, per voxel, saved
///         [NumosSerialize, PerChunk] public int BurnCount; // my-mod/fire/BurnCount, one per chunk, saved
///         [NumosScratch] public float Heat;                // my-mod/fire/Heat, per voxel, cleared on restore
///     }
///
///     foreach (var (chunk, fire) in context.Chunks(simulation).WithFire())
///     {
///         for (int voxel = 0; voxel &lt; fire.Exposure.Length; voxel++)
///             fire.Exposure[voxel] += fire.Heat[voxel];
///
///         fire.BurnCount[0]++;
///     }
///
///     simulation.SetChunkData(handle, Fire.Fields.BurnCount, 0, 0);
///     </code>
/// </example>
/// <param name="key">
///     The prefix of every field key. Namespace it (for example <c>my-mod/fire</c>), because the field registry is
///     shared by every assembly in the process. Saved keys are a compatibility contract with checkpoints and files. The
///     key must not be blank.
/// </param>
[PublicAPI]
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ChunkComponentAttribute(string key) : Attribute
{
    /// <summary>
    ///     Gets the prefix of every field key.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    ///     Gets or sets whether every field of the component resets to default values when the chunk goes to sleep.
    /// </summary>
    public bool ClearOnSleep { get; set; }

    /// <summary>
    ///     Gets or sets whether vacuuming resets the values of every field of the component.
    /// </summary>
    /// <remarks>
    ///     Vacuuming one voxel resets only that voxel's element of each per-voxel field. Vacuuming a whole chunk resets
    ///     every field, including a <see cref="PerChunkAttribute" /> one.
    /// </remarks>
    public bool ClearOnVacuum { get; set; }

    /// <summary>
    ///     Gets or sets the name in the generated extension members, when it should differ from the struct name.
    /// </summary>
    /// <remarks>
    ///     <c>AccessorName = "Burn"</c> gives <c>GetBurn()</c>, <c>TryGetBurn</c>, <c>HasBurn</c>, and
    ///     <c>WithBurn()</c>. Use it when the default names clash with a member of the chunk view or with another
    ///     accessor in the same namespace.
    /// </remarks>
    public string? AccessorName { get; set; }
}

/// <summary>
///     Gives a field of a <see cref="ChunkComponentAttribute" /> struct a fixed number of values per chunk, in place of
///     one value per voxel.
/// </summary>
/// <remarks>
///     The field becomes a fixed-length <see cref="ChunkField{T}" />, as <see cref="ChunkDataAttribute.Length" /> does.
///     On a mixture page it holds <see cref="Length" /> values per page.
/// </remarks>
[PublicAPI]
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class PerChunkAttribute : Attribute
{
    /// <summary>
    ///     Gets or sets the number of values per chunk. The default is 1, and zero is allowed.
    /// </summary>
    public int Length { get; set; } = 1;
}
