using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Extra rules a <c>[NumosSerialize]</c> wire name must meet, on top of not being blank.
/// </summary>
[Flags]
internal enum WireNameChecks
{
    None = 0,

    /// <summary>
    ///     The name must be a C# identifier, because it becomes a member name.
    /// </summary>
    Identifier = 1,

    /// <summary>
    ///     A null argument means the member's own name, and only an empty or white-space one is blank.
    /// </summary>
    NullIsMemberName = 2,
}

/// <summary>
///     A field or property that passed the checks every <c>[NumosSerialize]</c> member must pass.
/// </summary>
internal sealed class SerializedMember
{
    private const string SolverChoices =
        "mark it [NumosSerialize] if it affects behavior, [NumosScratch] if a restore should reset it, or " +
        "[NumosIgnore] if it's derived or OnRestored resets it";

    private const string OtherChoices =
        "mark it [NumosSerialize] if it affects behavior, or [NumosIgnore] if it's derived, scratch, or saved through " +
        "another member";

    private SerializedMember(string displayName, string wireName, int id, AttributeData attribute, bool settable, bool initOnly)
    {
        DisplayName = displayName;
        WireName = wireName;
        Id = id;
        Attribute = attribute;
        Settable = settable;
        InitOnly = initOnly;
    }

    /// <summary>
    ///     Gets the member qualified by its owner, for diagnostics.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    ///     Gets the name whose hash is the wire id.
    /// </summary>
    public string WireName { get; }

    /// <summary>
    ///     Gets the wire id.
    /// </summary>
    public int Id { get; }

    /// <summary>
    ///     Gets the member's <c>[NumosSerialize]</c>, for the named arguments the caller reads.
    /// </summary>
    public AttributeData Attribute { get; }

    /// <summary>
    ///     Gets the codec the attribute names, or null.
    /// </summary>
    public ITypeSymbol? Codec => StateSymbols.CodecOf(Attribute);

    /// <summary>
    ///     Gets whether the member is a writable field or has a set or init accessor.
    /// </summary>
    public bool Settable { get; }

    /// <summary>
    ///     Gets whether the member's accessor is <c>init</c>.
    /// </summary>
    public bool InitOnly { get; }

    /// <summary>
    ///     Applies the checks that <see cref="StateGenerator" /> and <see cref="SnapshotGenerator" /> share for a member
    ///     and claims its wire id.
    /// </summary>
    /// <param name="member">A field or property.</param>
    /// <param name="serialize">The member's <c>[NumosSerialize]</c>, or null when it has none.</param>
    /// <param name="hasStorage">
    ///     Whether the member stores a value, so an unmarked one can draw the unmarked warning. Ignored when
    ///     <paramref name="serialize" /> is set.
    /// </param>
    /// <param name="ownerDisplayName">The type that declares the member, for diagnostics.</param>
    /// <param name="location">Where diagnostics about the member point.</param>
    /// <param name="checks">The rules the wire name must meet.</param>
    /// <param name="classSolver">
    ///     Whether the owner is a class solver. Its readonly collections can draw the unmarked warning, and the warning
    ///     offers <c>[NumosScratch]</c>.
    /// </param>
    /// <param name="ids">The wire ids already claimed, with the member that claimed each. A success adds to it.</param>
    /// <param name="diagnostics">Receives the diagnostic for each check that fails.</param>
    /// <returns>The member, or null when it is not state or a check failed.</returns>
    /// <remarks>
    ///     <para>
    ///         A failure at or before the id check leaves <paramref name="ids" /> alone. The caller's later checks run
    ///         after the id is claimed, so a member they reject still blocks a second member with the same wire name.
    ///     </para>
    ///     <para>
    ///         <see cref="StateGenerator" /> hands every <c>[NumosScratch]</c> member to <see cref="ScratchMember" />
    ///         first. One that reaches this method belongs to a snapshot or its live type, where it is misplaced.
    ///     </para>
    /// </remarks>
    public static SerializedMember? TryParse(
        ISymbol member, AttributeData? serialize, bool hasStorage, string ownerDisplayName, LocationInfo? location,
        WireNameChecks checks, bool classSolver, Dictionary<int, string> ids, List<DiagnosticInfo> diagnostics)
    {
        string displayName = ownerDisplayName + "." + member.Name;
        if (StateSymbols.IsScratch(member))
        {
            ScratchMember.ReportMisplaced(member, serialize, displayName, location, diagnostics);
            return null;
        }

        if (member.IsStatic || member is IFieldSymbol { IsConst: true })
        {
            // Static members are never state. Flag only the ones claiming otherwise.
            if (serialize is not null)
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.StaticField, location, displayName));

            return null;
        }

        bool ignored = StateSymbols.IsIgnored(member);
        if (serialize is not null && ignored)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.BothTags, location, displayName));
            return null;
        }

        if (serialize is null)
        {
            // [field: NumosSerialize] already drew its own error, so the property isn't also "unmarked".
            if (member is IPropertySymbol property && StateSymbols.HasMarkedBackingField(property))
                return null;

            if (!ignored && hasStorage && !member.IsImplicitlyDeclared &&
                (StateSymbols.IsMutable(member) || (classSolver && StateSymbols.HoldsMutableCollection(member))))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.Unmarked, location, displayName, ownerDisplayName,
                        classSolver ? SolverChoices : OtherChoices));
            }

            return null;
        }

        if (!TryReadWireName(serialize, member.Name, checks, out string wireName))
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.BlankWireName, location, displayName));
            return null;
        }

        int id = FieldIds.Compute(wireName);
        if (ids.TryGetValue(id, out string? existing))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    StateDiagnostics.IdCollision, location, displayName, existing, id.ToString(CultureInfo.InvariantCulture)));

            return null;
        }

        ids.Add(id, displayName);
        return new SerializedMember(
            displayName,
            wireName,
            id,
            serialize,
            StateSymbols.IsMutable(member),
            member is IPropertySymbol { SetMethod.IsInitOnly: true });
    }

    private static bool TryReadWireName(AttributeData serialize, string memberName, WireNameChecks checks, out string wireName)
    {
        wireName = memberName;
        if (serialize.ConstructorArguments.Length != 1)
            return true;

        string? declared = serialize.ConstructorArguments[0].Value as string;
        if (declared is null && (checks & WireNameChecks.NullIsMemberName) != 0)
            return true;

        if (declared is null ||
            declared.Trim().Length == 0 ||
            ((checks & WireNameChecks.Identifier) != 0 && !SyntaxFacts.IsValidIdentifier(declared)))
        {
            return false;
        }

        wireName = declared;
        return true;
    }
}
