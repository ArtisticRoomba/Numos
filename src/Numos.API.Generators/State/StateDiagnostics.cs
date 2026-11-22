using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.State;

internal static class StateDiagnostics
{
    private const string Category = "Numos.State";

    public static readonly DiagnosticDescriptor TypeNotPartial = new(
        "NUMOSSTATE001",
        "Serializable type must be partial",
        "Type '{0}' must be partial because '{1}' has [NumosSerialize] or [NumosScratch] members and code is " +
        "generated into it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Generic = new(
        "NUMOSSTATE002",
        "Serializable type must not be generic",
        "'{0}' has [NumosSerialize] or [NumosScratch] members, so it must not be generic or nested in a generic type",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor IdCollision = new(
        "NUMOSSTATE005",
        "Two serialized members hash to the same id",
        "'{0}' and '{1}' both hash to field id {2}; give one a different wire name with [NumosSerialize(\"...\")]",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        "NUMOSSTATE007",
        "Serialized member type is not supported",
        "'{0}' has type '{1}', which [NumosSerialize] can't hash or compare; '{2}' is not a primitive, enum, string, " +
        "Int3, GasProperties, quantity alias, serializable type, or supported read-only collection. Give it a Codec.",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor HandWritten = new(
        "NUMOSSTATE008",
        "Serializable type declares a member the generator owns",
        "'{0}' declares '{1}', but the generator writes it from the type's marked members; delete the " +
        "hand-written '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor WorldSolver = new(
        "NUMOSSTATE009",
        "Solver with serialized members must be a class",
        "'{0}' implements IAtmosWorldSolver and has [NumosSerialize] members, so it must be a class; state is read " +
        "back into the registered instance, which a boxed struct copy would lose track of",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BothTags = new(
        "NUMOSSTATE011",
        "Member is marked both serialized and ignored",
        "'{0}' has both [NumosSerialize] and [NumosIgnore]; keep one",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor StaticField = new(
        "NUMOSSTATE012",
        "Serialized member must be an instance member",
        "'{0}' is static or const; [NumosSerialize] only applies to instance fields and properties",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DerivedClass = new(
        "NUMOSSTATE013",
        "Serializable class derives from a class the generator can't extend",
        "'{0}' derives from '{1}', which {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    ///     The reason <see cref="DerivedClass" /> gives for a type that is built from data.
    /// </summary>
    public const string DerivedDataTypeReason =
        "is not object; only a class solver or a [SimulationComponent] class can derive from a class, so derive " +
        "from object or move the base's settings into a nested serializable member";

    /// <summary>
    ///     The reason <see cref="DerivedClass" /> gives for a base class in another assembly without generated state.
    /// </summary>
    public const string ForeignBaseReason =
        "is in another assembly and implements neither INumosStateTarget nor INumosScratchTarget from the Numos " +
        "generator, so the generator can't see whether it holds state";

    /// <summary>
    ///     The reason <see cref="DerivedClass" /> gives for a base class in another assembly without the hooks.
    /// </summary>
    public const string OldBaseReason =
        "is in another assembly and has no generated state hooks for a derived class to override; rebuild it " +
        "with the current Numos.API";

    public static readonly DiagnosticDescriptor NoConstructor = new(
        "NUMOSSTATE014",
        "Serializable type has no constructor the reader can call",
        "'{0}' has no constructor the generated reader can call: {1}. Each constructor parameter must match a " +
        "[NumosSerialize] member by name (ignoring case) with a type the member's value fits, and each serialized " +
        "member no parameter covers needs a set or init accessor.",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankKey = new(
        "NUMOSSTATE015",
        "Key is blank",
        "'{0}' returns a blank Key; use a namespaced name such as \"my-mod/fire\"",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Unmarked = new(
        "NUMOSSTATE016",
        "Mutable member is neither serialized nor ignored",
        "'{0}' can change but isn't saved with '{1}'; {2}",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor BlankWireName = new(
        "NUMOSSTATE017",
        "Wire name is blank",
        "'{0}' passes a blank wire name to [NumosSerialize]; leave the argument out to use the member's name",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NonConstantKey = new(
        "NUMOSSTATE018",
        "Solver configuration key is not a constant",
        "Solver configuration '{0}' must return a constant string from Key, such as " +
        "'public string Key => \"my-mod/fire\";', so it can be registered for loading",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotAssignable = new(
        "NUMOSSTATE020",
        "Solver state member can't be restored in place",
        "'{0}' is readonly, get-only, or init-only, but solver and component state is restored by assigning to the " +
        "existing instance; make it writable, or make it a non-nullable List<T>, HashSet<T>, or " +
        "Dictionary<TKey, TValue>, which a restore clears and refills",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotRegistrable = new(
        "NUMOSSTATE019",
        "Solver configuration can't be registered automatically",
        "Solver configuration '{0}' (or a type containing it) is private or protected, so the generated module " +
        "initializer can't register it for loading; make it internal, or call SolverConfigurationTypes.Register " +
        "before loading",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor SnapshotNotPartial = new(
        "NUMOSSTATE021",
        "Snapshot type must be partial",
        "Type '{0}' must be partial because '{1}' is a [NumosSnapshotOf] snapshot and its members are generated into it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor SnapshotShape = new(
        "NUMOSSTATE022",
        "Snapshot type has the wrong shape",
        "Snapshot '{0}' must be a non-generic, non-static, non-abstract class that isn't a record, isn't nested in a " +
        "generic type, and derives from object",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor LiveTypeInvalid = new(
        "NUMOSSTATE023",
        "Live type can't have a generated snapshot",
        "'{0}' can't be the live type of snapshot '{1}'; it must be a non-generic class declared in the same project " +
        "and not file-local",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor LiveTypeEmpty = new(
        "NUMOSSTATE024",
        "Live type has no serialized members",
        "Live type '{0}' has no [NumosSerialize] members, so snapshot '{1}' would capture nothing; mark the members " +
        "that hold state",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor LiveMemberInaccessible = new(
        "NUMOSSTATE025",
        "Live member is not visible to its snapshot",
        "'{0}' must be internal or public so the generated snapshot '{1}' can read it; for private state, mark an " +
        "internal property that reads and writes it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor LiveMemberUnsupported = new(
        "NUMOSSTATE026",
        "Live member can't be copied into a snapshot",
        "'{0}' has type '{1}', which a snapshot can't copy; use an immutable type, an array or FlatArray<T> of " +
        "immutable elements, a class with its own [NumosSnapshotOf] snapshot (or an array or list of them), or a " +
        "property whose getter returns a value nothing else holds",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor SnapshotNameClash = new(
        "NUMOSSTATE027",
        "Snapshot declares a member the generator creates",
        "Snapshot '{0}' declares '{1}', which the generator also creates from '{2}'; rename one, or give the live " +
        "member another snapshot name with [NumosSerialize(\"...\")]",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BadCountMember = new(
        "NUMOSSTATE028",
        "Count member is not a settable int",
        "'{0}' names Count member '{1}', which must be a settable instance int field or property of '{2}' that the " +
        "snapshot can read and write",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor CountWithoutCollection = new(
        "NUMOSSTATE029",
        "Count needs an array",
        "'{0}' sets Count, which only applies to an array, a FlatArray<T>, or a list of live objects with a snapshot",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DuplicateSnapshot = new(
        "NUMOSSTATE030",
        "Live type has more than one snapshot",
        "'{0}' and '{1}' are both snapshots of '{2}'; a live type can have only one",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor CountOutsideSnapshot = new(
        "NUMOSSTATE031",
        "Count only applies to live types",
        "'{0}' sets Count, which only applies to members of a live type that has a [NumosSnapshotOf] snapshot",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor FileLocal = new(
        "NUMOSSTATE032",
        "Serializable type must not be file-local",
        "'{0}' is file-local or nested in a file-local type, so code can't be generated into it from another file; " +
        "remove the 'file' modifier",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BackingFieldTarget = new(
        "NUMOSSTATE033",
        "[NumosSerialize] is on a property's backing field",
        "'{0}' has [field: NumosSerialize], which marks the backing field the generator doesn't read; mark the " +
        "property instead ([NumosSerialize], or [property: NumosSerialize] on a record parameter)",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Indexer = new(
        "NUMOSSTATE034",
        "Serialized member must not be an indexer",
        "'{0}' is an indexer; [NumosSerialize] only applies to fields and properties that hold one value",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ScratchWithOtherMark = new(
        "NUMOSSTATE035",
        "Member is marked scratch and serialized or ignored",
        "'{0}' has both [NumosScratch] and [{1}]; keep one",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ScratchOutsideSolver = new(
        "NUMOSSTATE036",
        "[NumosScratch] is outside a class solver",
        "'{0}' has [NumosScratch], which only applies to instance fields and properties declared in a class that " +
        "implements IAtmosWorldSolver or has [SimulationComponent], or in a base class of one; use [NumosIgnore] instead",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ScratchNotResettable = new(
        "NUMOSSTATE037",
        "Scratch member can't be reset",
        "'{0}' is readonly or get-only, and its type '{1}' is not a class or interface with a parameterless " +
        "Clear(), so the generated reset can't reach it; make it assignable, or reset it in OnRestored and mark it " +
        "[NumosIgnore]",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ScratchInitializer = new(
        "NUMOSSTATE038",
        "Scratch initializer can't be repeated",
        "The initializer of '{0}' {1}, so the generated reset can't repeat it; reset the member in OnRestored and " +
        "mark it [NumosIgnore]",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ComponentShape = new(
        "NUMOSSTATE039",
        "Simulation component has the wrong shape",
        "Simulation component '{0}' {1}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankComponentKey = new(
        "NUMOSSTATE040",
        "Simulation component key is blank",
        "Simulation component '{0}' passes a blank key to [SimulationComponent]; use a namespaced name such as " +
        "\"my-mod/pipes\"",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor HostEditableIgnored = new(
        "NUMOSSTATE041",
        "HostEditable has no effect here",
        "'{0}' sets HostEditable = false, which only applies to members of a class solver or a " +
        "[SimulationComponent] class; remove it",
        Category,
        DiagnosticSeverity.Warning,
        true);
}
