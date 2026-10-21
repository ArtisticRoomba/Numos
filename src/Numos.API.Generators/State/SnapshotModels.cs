using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     How a marked live member stores its value.
/// </summary>
internal enum LiveStorage
{
    /// <summary>
    ///     A field.
    /// </summary>
    Field,

    /// <summary>
    ///     An auto-property, or any property with a backing field.
    /// </summary>
    AutoProperty,

    /// <summary>
    ///     A property with a hand-written getter. Its value is taken as returned, and given back to its setter.
    /// </summary>
    Adapter,
}

/// <summary>
///     The collection a live member keeps a list of live children in.
/// </summary>
internal enum LiveCollection
{
    None,
    Array,

    /// <summary>
    ///     Anything indexable with a <c>Count</c>, such as <c>List&lt;T&gt;</c> or <c>IReadOnlyList&lt;T&gt;</c>.
    /// </summary>
    List,
}

/// <summary>
///     What the generator does with one live member, decided once every snapshot in the project is known.
/// </summary>
internal enum SnapshotMemberKind
{
    /// <summary>
    ///     Copied by value: an immutable type, an adapter's value, or a member with a codec.
    /// </summary>
    Value,

    /// <summary>
    ///     An array or <c>FlatArray&lt;T&gt;</c>, copied into a list.
    /// </summary>
    Array,

    /// <summary>
    ///     A live object with a snapshot of its own.
    /// </summary>
    Nested,

    /// <summary>
    ///     An array or list of live objects with a snapshot of their own.
    /// </summary>
    NestedList,
}

/// <summary>
///     How <c>RestoreInto</c> puts a member back.
/// </summary>
internal enum RestoreMode
{
    /// <summary>
    ///     It can't, so the snapshot is capture-only.
    /// </summary>
    None,

    /// <summary>
    ///     Assigns the value.
    /// </summary>
    Assign,

    /// <summary>
    ///     Assigns a fresh copy of the array.
    /// </summary>
    AssignArray,

    /// <summary>
    ///     Requires the read-only live value to already match.
    /// </summary>
    Check,

    /// <summary>
    ///     Copies into the live array, whose length must match (or hold the prefix, with a count member).
    /// </summary>
    CopyInPlace,

    /// <summary>
    ///     Restores the nested live object through its own snapshot.
    /// </summary>
    Nested,
}

/// <summary>
///     One marked live member as the transform saw it, before the project's other snapshots are known.
/// </summary>
/// <param name="LiveName">The escaped live member name.</param>
/// <param name="Name">The snapshot member and wire name.</param>
/// <param name="Id">The wire id, hashed from <paramref name="Name" />.</param>
/// <param name="Accessibility">The generated member's accessibility keyword.</param>
/// <param name="Storage">How the live member stores its value.</param>
/// <param name="Assignable">Whether the snapshot can assign the live member.</param>
/// <param name="Shape">The member type's shape, used when it's copied by value.</param>
/// <param name="Immutable">Whether the member type is provably immutable.</param>
/// <param name="ValueType">Whether the member type is a value type or a string, so a read-only one can be checked.</param>
/// <param name="HasCodec">Whether the member names a codec.</param>
/// <param name="TypeName">The member type, fully qualified, for nested snapshot lookup.</param>
/// <param name="ArrayElement">The element shape when the member is an array or <c>FlatArray&lt;T&gt;</c>.</param>
/// <param name="ArrayElementImmutable">Whether the array's element type is provably immutable.</param>
/// <param name="IsFlatArray">Whether the member is a <c>FlatArray&lt;T&gt;</c>.</param>
/// <param name="Collection">The collection holding live children, if the member could be a list of them.</param>
/// <param name="ElementTypeName">The element type, fully qualified, for nested snapshot lookup.</param>
/// <param name="Count">The escaped count member, or null.</param>
/// <param name="Documentation">The doc comment lines to put on the generated member.</param>
/// <param name="DisplayName">The live member for diagnostics.</param>
/// <param name="TypeDisplay">The member type for diagnostics.</param>
/// <param name="Location">Where diagnostics about the member point.</param>
internal sealed record LiveMemberDeclaration(
    string LiveName,
    string Name,
    int Id,
    string Accessibility,
    LiveStorage Storage,
    bool Assignable,
    StateShape Shape,
    bool Immutable,
    bool ValueType,
    bool HasCodec,
    string TypeName,
    StateShape? ArrayElement,
    bool ArrayElementImmutable,
    bool IsFlatArray,
    LiveCollection Collection,
    string? ElementTypeName,
    string? Count,
    EquatableArray<string> Documentation,
    string DisplayName,
    string TypeDisplay,
    LocationInfo? Location);

/// <summary>
///     One snapshot declaration as the transform saw it.
/// </summary>
/// <param name="Type">The snapshot type to reopen.</param>
/// <param name="TypeName">The snapshot type, fully qualified.</param>
/// <param name="DisplayName">The snapshot type for diagnostics.</param>
/// <param name="LiveTypeName">The live type, fully qualified, or empty when it couldn't be resolved.</param>
/// <param name="LiveDisplayName">The live type for diagnostics.</param>
/// <param name="EmitConstructor">Whether to generate the private parameterless constructor.</param>
/// <param name="CanGenerate">False once a diagnostic has ruled out generating anything.</param>
/// <param name="Members">The marked live members in declaration order.</param>
/// <param name="HandWritten">The snapshot's own marked members, part of the schema but filled by hooks.</param>
/// <param name="Diagnostics">Diagnostics found so far.</param>
/// <param name="Location">Where type-level diagnostics point.</param>
internal sealed record SnapshotDeclaration(
    PartialTypeModel Type,
    string TypeName,
    string DisplayName,
    string LiveTypeName,
    string LiveDisplayName,
    bool EmitConstructor,
    bool CanGenerate,
    EquatableArray<LiveMemberDeclaration> Members,
    EquatableArray<StateFieldDeclaration> HandWritten,
    EquatableArray<DiagnosticInfo> Diagnostics,
    LocationInfo? Location);

/// <summary>
///     One generated snapshot member, ready to emit.
/// </summary>
/// <param name="LiveName">The escaped live member name.</param>
/// <param name="Name">The escaped snapshot member name.</param>
/// <param name="Accessibility">The generated member's accessibility keyword.</param>
/// <param name="Kind">How the member is captured.</param>
/// <param name="Storage">How the live member stores its value.</param>
/// <param name="Shape">The snapshot member's shape, which also names its codec.</param>
/// <param name="ElementType">The list element type for list members, or empty.</param>
/// <param name="IsReference">Whether the snapshot member needs a non-null initializer.</param>
/// <param name="IsFlatArray">Whether the live member is a <c>FlatArray&lt;T&gt;</c>.</param>
/// <param name="Collection">The live collection kind for <see cref="SnapshotMemberKind.NestedList" />.</param>
/// <param name="Nested">The nested snapshot type, fully qualified, or empty.</param>
/// <param name="Count">The escaped live count member, or null.</param>
/// <param name="Restore">How restore puts the member back.</param>
/// <param name="Documentation">Doc comment lines.</param>
internal sealed record SnapshotMemberModel(
    string LiveName,
    string Name,
    string Accessibility,
    SnapshotMemberKind Kind,
    LiveStorage Storage,
    StateShape Shape,
    string ElementType,
    bool IsReference,
    bool IsFlatArray,
    LiveCollection Collection,
    string Nested,
    string? Count,
    RestoreMode Restore,
    EquatableArray<string> Documentation)
{
    /// <summary>
    ///     Whether the member is an <c>IReadOnlyList&lt;T&gt;</c> whose setter wraps the value.
    /// </summary>
    public bool IsList => ElementType.Length > 0;
}

/// <param name="Type">The snapshot type to reopen.</param>
/// <param name="TypeName">The snapshot type, fully qualified.</param>
/// <param name="LiveTypeName">The live type, fully qualified.</param>
/// <param name="EmitConstructor">Whether to generate the private parameterless constructor.</param>
/// <param name="Restorable">Whether every member can be put back, so <c>RestoreInto</c> is generated.</param>
/// <param name="Members">Generated members in the live type's declaration order.</param>
/// <param name="State">Every schema field, generated and hand-written, for the state walker.</param>
/// <param name="InitOnly">Hand-written members that can only be set in an object initializer.</param>
internal sealed record SnapshotModel(
    PartialTypeModel Type,
    string TypeName,
    string LiveTypeName,
    bool EmitConstructor,
    bool Restorable,
    EquatableArray<SnapshotMemberModel> Members,
    StateTypeModel State,
    EquatableArray<StateFieldModel> InitOnly);
