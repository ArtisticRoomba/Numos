using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.ChunkData;

internal static class ChunkComponentDiagnostics
{
    private const string Category = "Numos.API.ChunkComponent";

    public static readonly DiagnosticDescriptor NotPartialStruct = new(
        "NUMOSCOMPONENT001",
        "Chunk component must be a non-generic partial struct",
        "Chunk component '{0}' can't be generated because '{1}' {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Inaccessible = new(
        "NUMOSCOMPONENT002",
        "Chunk component must be visible to its namespace",
        "Chunk component '{0}' must be public or internal and declared in types that are public or internal, " +
        "so the generated accessors can reach it",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankKey = new(
        "NUMOSCOMPONENT003",
        "Chunk component key is blank",
        "Chunk component '{0}' has a blank key; pass a namespaced key such as \"my-mod/fire\"",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnmarkedField = new(
        "NUMOSCOMPONENT004",
        "Chunk component field is not marked",
        "Field '{0}' of chunk component '{1}' has neither [NumosSerialize] nor [NumosScratch], so it is not a chunk " +
        "field; mark it [NumosSerialize] to save it or [NumosScratch] for scratch data, or remove it",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor InvalidMark = new(
        "NUMOSCOMPONENT005",
        "Chunk component member can't be a chunk field",
        "Member '{0}' of chunk component '{1}' {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor SavedWithUnportableType = new(
        "NUMOSCOMPONENT006",
        "Saved chunk component field needs a primitive or enum type",
        "Saved field '{0}' of chunk component '{1}' has type '{2}', which a .numos file can't hold; use a primitive " +
        "or enum type, or mark the field [NumosScratch]",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ScratchWithManagedType = new(
        "NUMOSCOMPONENT007",
        "Scratch chunk component field needs an unmanaged type",
        "Scratch field '{0}' of chunk component '{1}' has type '{2}', which is not unmanaged; use a type without " +
        "managed references",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NegativeLength = new(
        "NUMOSCOMPONENT008",
        "Per-chunk length is negative",
        "Field '{0}' of chunk component '{1}' has PerChunk Length {2}; it must be zero or greater",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor InvalidAccessorName = new(
        "NUMOSCOMPONENT009",
        "Chunk component accessor name is not a valid identifier",
        "AccessorName '{0}' on chunk component '{1}' is not a valid C# identifier",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AccessorNameCollision = new(
        "NUMOSCOMPONENT010",
        "Chunk component accessor collides with another member",
        "Accessor '{0}' for chunk component '{1}' collides with {2}; set AccessorName to pick a different name. " +
        "The component gets no accessors.",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor DuplicateWireName = new(
        "NUMOSCOMPONENT011",
        "Two chunk component fields have the same wire name",
        "Field '{0}' of chunk component '{1}' has wire name '{2}', which field '{3}' already uses; both would get " +
        "the key '{4}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor RequiresCSharp14 = new(
        "NUMOSCOMPONENT012",
        "Chunk components require C# 14",
        "[ChunkComponent] generates C# 14 extension members, but this project uses C# {0}; set LangVersion to 14 or " +
        "later",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NoFields = new(
        "NUMOSCOMPONENT013",
        "Chunk component has no fields",
        "Chunk component '{0}' has no [NumosSerialize] or [NumosScratch] instance field, so it has no storage to view",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ReservedMemberName = new(
        "NUMOSCOMPONENT014",
        "Chunk component member uses a generated name",
        "Member '{0}' of chunk component '{1}' has the name of the generated nested type '{0}'; rename the member",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor FieldInitializer = new(
        "NUMOSCOMPONENT015",
        "Chunk component field initializer is not used",
        "Field '{0}' of chunk component '{1}' has an initializer, but chunk storage starts at the default value of " +
        "the field's type and never runs it; remove the initializer",
        Category,
        DiagnosticSeverity.Warning,
        true);
}
