using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.State;

internal static class StateDiagnostics
{
    private const string Category = "Numos.State";

    public static readonly DiagnosticDescriptor TypeNotPartial = new(
        "NUMOSSTATE001",
        "State type must be partial",
        "Type '{0}' must be partial because [NumosState] type '{1}' is generated into it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Generic = new(
        "NUMOSSTATE002",
        "State type must not be generic",
        "[NumosState] type '{0}' must not be generic or nested in a generic type",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Untagged = new(
        "NUMOSSTATE003",
        "State type member is neither state nor excluded",
        "'{0}' stores a value on [NumosState] type '{1}'; mark it [StateField(id)] if it affects behavior, or " +
        "[NotState] if it's derived from the other fields",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NonPositiveId = new(
        "NUMOSSTATE004",
        "State field id must be positive",
        "State field '{0}' has id {1}; ids must be greater than zero",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DuplicateId = new(
        "NUMOSSTATE005",
        "State field id is used twice",
        "State field '{0}' reuses id {1}, already taken by '{2}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor RetiredId = new(
        "NUMOSSTATE006",
        "State field id is retired",
        "State field '{0}' uses id {1}, which [RetiredStateFields] on '{2}' lists as retired; pick a new id",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        "NUMOSSTATE007",
        "State field type is not supported",
        "State field '{0}' has type '{1}', which [NumosState] can't hash or compare; '{2}' is not a primitive, " +
        "enum, string, Int3, GasProperties, quantity alias, [NumosState] type, or supported read-only collection",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor HandWritten = new(
        "NUMOSSTATE008",
        "State type declares a member the generator owns",
        "[NumosState] type '{0}' declares '{1}', but the generator writes it from the type's [StateField] members " +
        "and [NumosState] arguments; delete the hand-written '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor WorldSolver = new(
        "NUMOSSTATE009",
        "Solver objects cannot hold state fields",
        "'{0}' is on '{1}', which implements IAtmosWorldSolver; solver objects are outside checkpoints, so replay " +
        "would never reproduce this value. Move it into a solver configuration (IAtmosSolverConfiguration) for " +
        "settings, or a rollback chunk field for per-voxel state.",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotStateType = new(
        "NUMOSSTATE010",
        "State field outside a state type",
        "'{0}' has [StateField] but '{1}' is not marked [NumosState], so nothing reads it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BothTags = new(
        "NUMOSSTATE011",
        "Member is marked both state and not state",
        "'{0}' has both [StateField] and [NotState]; keep one",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor StaticField = new(
        "NUMOSSTATE012",
        "State field must be an instance member",
        "'{0}' is static or const; [StateField] only applies to instance fields and properties",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DerivedClass = new(
        "NUMOSSTATE013",
        "State class must not derive from another class",
        "[NumosState] class '{0}' derives from '{1}', whose fields the generator can't see; derive from object, " +
        "or move the base's settings into a nested [NumosState] field",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NoConstructor = new(
        "NUMOSSTATE014",
        "State type has no constructor the reader can call",
        "[NumosState] type '{0}' has no constructor the generated reader can call: {1}. Each constructor parameter " +
        "must match a [StateField] member by name (ignoring case) with a type the field's value fits, and each " +
        "state field no parameter covers needs a set or init accessor.",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankKey = new(
        "NUMOSSTATE015",
        "State key is blank",
        "[NumosState] type '{0}' sets a blank Key; use a namespaced name such as \"my-mod/fire\", or leave Key unset",
        Category,
        DiagnosticSeverity.Error,
        true);
}
