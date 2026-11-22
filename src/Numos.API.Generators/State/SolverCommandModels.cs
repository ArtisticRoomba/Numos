using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <param name="Name">The escaped parameter name.</param>
/// <param name="Id">The field id of the parameter in the payload, hashed from its unescaped name.</param>
/// <param name="DeclaredType">The parameter type as the generated signature spells it.</param>
/// <param name="Shape">The parameter type for the codec. Aliases are resolved by the time a model reaches the emitter.</param>
internal sealed record SolverCommandParameterModel(string Name, int Id, string DeclaredType, StateShape Shape);

/// <param name="MethodName">The escaped method name.</param>
/// <param name="Modifiers">The declaration's modifiers without <c>partial</c>, repeated on the implementation.</param>
/// <param name="WireName">The name <paramref name="Id" /> hashes.</param>
/// <param name="Id">The command id.</param>
/// <param name="ApplyName">The escaped name of the method that holds the command's logic.</param>
/// <param name="PassesWorld">Whether the apply method takes the solver's world before the command's parameters.</param>
/// <param name="Parameters">The parameters in declaration order.</param>
internal sealed record SolverCommandModel(
    string MethodName,
    string Modifiers,
    string WireName,
    int Id,
    string ApplyName,
    bool PassesWorld,
    EquatableArray<SolverCommandParameterModel> Parameters);

/// <summary>
///     How the generated <c>InvokeSolverCommand</c> dispatch hooks into the class's base classes.
/// </summary>
/// <param name="IsOverride">
///     Whether a base class has commands, so the class overrides the base's dispatch and falls back to it instead of
///     implementing the interface.
/// </param>
/// <param name="Accessibility">The dispatch method's accessibility, as C# modifiers.</param>
internal sealed record SolverCommandHookModel(bool IsOverride, string Accessibility);

/// <summary>
///     A command a base class declares, which a command of the derived class must not share an id with.
/// </summary>
/// <param name="Id">The command id.</param>
/// <param name="DisplayName">The command for messages, such as <c>MyMod.FireBase.Ignite</c>.</param>
internal sealed record SolverCommandBaseCommand(int Id, string DisplayName);

/// <summary>
///     A parameter before alias resolution, with the location its type diagnostic points at.
/// </summary>
internal sealed record SolverCommandParameterDeclaration(SolverCommandParameterModel Parameter, LocationInfo? Location);

/// <summary>
///     One <c>[SolverCommand]</c> method with where it was declared and what the per-method checks found.
/// </summary>
/// <param name="Type">The declaring class.</param>
/// <param name="TypeName">The fully qualified class.</param>
/// <param name="TypeDisplayName">The class for messages.</param>
/// <param name="DisplayName">The command for messages, such as <c>MyMod.FireSolver.Ignite</c>.</param>
/// <param name="Command">The command, with unresolved parameter shapes. Null when a check ruled it out.</param>
/// <param name="Parameters">The parameters with their display names, before alias resolution.</param>
/// <param name="Location">The method's identifier.</param>
/// <param name="TypeLocation">The identifier of the type declaration that contains the method.</param>
/// <param name="Diagnostics">Problems with the method itself.</param>
/// <param name="TypeDiagnostics">Problems with the containing types, reported once per type.</param>
/// <param name="Hook">How the class's dispatch relates to its base classes.</param>
/// <param name="BaseCommands">The commands of every base class, nearest base first.</param>
internal sealed record SolverCommandDeclaration(
    PartialTypeModel Type,
    string TypeName,
    string TypeDisplayName,
    string DisplayName,
    SolverCommandModel? Command,
    EquatableArray<SolverCommandParameterDeclaration> Parameters,
    LocationInfo? Location,
    LocationInfo? TypeLocation,
    EquatableArray<DiagnosticInfo> Diagnostics,
    EquatableArray<DiagnosticInfo> TypeDiagnostics,
    SolverCommandHookModel Hook,
    EquatableArray<SolverCommandBaseCommand> BaseCommands);

/// <summary>
///     The generated file for one class.
/// </summary>
/// <param name="Type">The class to reopen.</param>
/// <param name="TypeName">The fully qualified class, hashed into the name of its module initializer.</param>
/// <param name="Commands">The commands that passed every check, in source order.</param>
/// <param name="Hook">How the class's dispatch relates to its base classes.</param>
internal sealed record SolverCommandTypeModel(
    PartialTypeModel Type,
    string TypeName,
    EquatableArray<SolverCommandModel> Commands,
    SolverCommandHookModel Hook);
