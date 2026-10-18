using Microsoft.CodeAnalysis;

namespace Numos.Replay.Generators;

internal static class ReplayDiagnostics
{
    private const string Category = "Numos.Replay";

    public static readonly DiagnosticDescriptor UnsupportedFieldType = new(
        "NUMOSREPLAYGEN001",
        "Unsupported wire field type",
        "Operation '{0}' has a field '{1}' of type '{2}' with no wire encoding; add Write{3}/Read{3} to '{4}', or " +
        "hand-write Write{5}/Read{5} for the whole operation",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AmbiguousConstructor = new(
        "NUMOSREPLAYGEN002",
        "Operation record has no single primary constructor",
        "Operation '{0}' must declare exactly one non-copy constructor for its wire fields, or have hand-written " +
        "Write{1}/Read{1} methods",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DuplicateCode = new(
        "NUMOSREPLAYGEN004",
        "Duplicate operation code",
        "Operation '{0}' returns code '{1}', which '{2}' already uses",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor MismatchedCustomMethod = new(
        "NUMOSREPLAYGEN007",
        "Missing or mismatched custom codec method",
        "Operation '{0}' has a hand-written codec but no matching '{1}' on '{2}'; expected {3}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor CodeWithoutOperation = new(
        "NUMOSREPLAYGEN008",
        "Opcode has no operation type",
        "'{0}.{1}' is not returned by any operation's Code, so it can never be written or read",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NonConstantCode = new(
        "NUMOSREPLAYGEN011",
        "Operation code is not a constant opcode",
        "Operation '{0}' must override Code as '=> {1}.Member' so the generator can read its opcode",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnsupportedCheckpointField = new(
        "NUMOSCHECKPTGEN001",
        "Unsupported checkpoint field type",
        "Checkpoint field '{0}' has type '{1}', which the checkpoint field generator can't copy, hash, or encode; " +
        "leave it untagged and hand-write its capture, hash, and wire code",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DuplicateCheckpointOrder = new(
        "NUMOSCHECKPTGEN002",
        "Duplicate checkpoint field order",
        "More than one member in '{0}' declares checkpoint field order {1}",
        Category,
        DiagnosticSeverity.Error,
        true);
}