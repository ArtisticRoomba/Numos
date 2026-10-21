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
        "Solver objects cannot hold serialized members",
        "'{0}' implements IAtmosWorldSolver; solver objects are outside checkpoints, so replay would never reproduce " +
        "its [NumosSerialize] members. Move them into a solver configuration (IAtmosSolverConfiguration) for " +
        "settings, or a [ChunkData] field for per-voxel state.",
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

    public static readonly DiagnosticDescriptor NotRegistrable = new(
        "NUMOSSTATE019",
        "Solver configuration can't be registered automatically",
        "Solver configuration '{0}' (or a type containing it) is private or protected, so the generated module " +
        "initializer can't register it for loading; make it internal, or call SolverConfigurationTypes.Register " +
        "before loading",
        Category,
        DiagnosticSeverity.Warning,
        true);
}
