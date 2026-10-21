using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.State;

internal static class StateDiagnostics
{
    private const string Category = "Numos.State";

    public static readonly DiagnosticDescriptor TypeNotPartial = new(
        "NUMOSSTATE001",
        "Serializable type must be partial",
        "Type '{0}' must be partial because '{1}' has [NumosSerialize] members and code is generated into it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Generic = new(
        "NUMOSSTATE002",
        "Serializable type must not be generic",
        "'{0}' has [NumosSerialize] members, so it must not be generic or nested in a generic type",
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
        "'{0}' declares '{1}', but the generator writes it from the type's [NumosSerialize] members; delete the " +
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
        "Serializable class must not derive from another class",
        "'{0}' derives from '{1}', whose members the generator can't see; derive from object, or move the base's " +
        "settings into a nested serializable member",
        Category,
        DiagnosticSeverity.Error,
        true);

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
        "'{0}' can change but isn't saved with '{1}'; mark it [NumosSerialize] if it affects behavior, or " +
        "[NumosIgnore] if it's derived or scratch",
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
        "'{0}' is readonly, get-only, or init-only, but solver state is restored by assigning to the registered " +
        "instance; make it writable",
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
        "'{0}' can't be the live type of snapshot '{1}'; it must be a non-generic class declared in the same project",
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
}
