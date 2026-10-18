using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.ChunkData;

internal static class ChunkDataDiagnostics
{
    private const string Category = "Numos.API.ChunkData";

    public static readonly DiagnosticDescriptor NotPartialDefinition = new(
        "NUMOSCHUNK001",
        "Chunk data property must be an unimplemented partial definition",
        "Chunk data property '{0}' must be a partial property declaration with only a get accessor and no body; " +
        "the generator supplies the implementation",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotStatic = new(
        "NUMOSCHUNK002",
        "Chunk data property must be static",
        "Chunk data property '{0}' must be static",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotChunkField = new(
        "NUMOSCHUNK003",
        "Chunk data property must have type ChunkField<T>",
        "Chunk data property '{0}' has type '{1}'; it must be Numos.API.ChunkField<T>",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        "NUMOSCHUNK004",
        "Type containing chunk data must be partial",
        "Type '{0}' must be partial because it contains chunk data property '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ContainingTypeGeneric = new(
        "NUMOSCHUNK005",
        "Type containing chunk data must not be generic",
        "Type '{0}' is generic, so chunk data property '{1}' would register one field per construction; " +
        "declare it in a non-generic type",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankKey = new(
        "NUMOSCHUNK006",
        "Chunk data key is blank",
        "Chunk data property '{0}' has a blank key; pass a non-empty key or use [ChunkData] without arguments",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor RollbackWithoutKey = new(
        "NUMOSCHUNK007",
        "Rollback chunk data needs an explicit key",
        "Chunk data property '{0}' sets Rollback but has no explicit key; the key is stored in checkpoints, " +
        "so it can't be derived from a name that may be refactored",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor RollbackWithManagedType = new(
        "NUMOSCHUNK008",
        "Rollback chunk data needs an unmanaged element type",
        "Chunk data property '{0}' sets Rollback, but element type '{1}' is not unmanaged; " +
        "checkpoints can only capture element types without managed references",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NegativeLength = new(
        "NUMOSCHUNK009",
        "Chunk data length is negative",
        "Chunk data property '{0}' has Length {1}; it must be zero or greater",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Inaccessible = new(
        "NUMOSCHUNK010",
        "Chunk data property must be visible to its namespace",
        "Chunk data property '{0}' must be public or internal and declared in types that are public or internal, " +
        "so the generated chunk accessors can reach it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ConflictingKey = new(
        "NUMOSCHUNK011",
        "Chunk data key registered with conflicting definitions",
        "Chunk data key '{0}' on '{1}' is also declared on {2} with a different element type, length, " +
        "or rollback setting; registration would throw at run time",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DuplicateKey = new(
        "NUMOSCHUNK012",
        "Chunk data key declared more than once",
        "Chunk data key '{0}' on '{1}' is also declared on '{2}'; both resolve to the same field, " +
        "so one declaration is redundant",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor RequiresCSharp14 = new(
        "NUMOSCHUNK013",
        "Chunk data requires C# 14",
        "[ChunkData] generates C# 14 extension properties, but this project uses C# {0}; set LangVersion to 14 or " +
        "later",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AccessorNameCollision = new(
        "NUMOSCHUNK014",
        "Chunk data accessor name collides with another member",
        "Chunk accessor '{0}' for '{1}' collides with {2}; set AccessorName to pick a different name",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor InvalidAccessorName = new(
        "NUMOSCHUNK015",
        "Chunk data accessor name is not a valid identifier",
        "AccessorName '{0}' on chunk data property '{1}' is not a valid C# identifier",
        Category,
        DiagnosticSeverity.Error,
        true);
}
