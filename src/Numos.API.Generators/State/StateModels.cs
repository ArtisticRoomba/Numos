using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <param name="Id">The field's wire id, hashed from its wire name.</param>
/// <param name="MemberName">The escaped member name to read the value from.</param>
/// <param name="Shape">The member's type. Aliases are resolved by the time a field reaches the emitter.</param>
/// <param name="Name">The unescaped member name, which constructor parameters bind to.</param>
/// <param name="Settable">Whether an object initializer can assign the member (a mutable field, or a set/init accessor).</param>
/// <param name="InitOnly">
///     Whether the member can only be assigned in an object initializer. Other settable members are assigned after
///     construction, and only when the data contains them, so a missing one keeps whatever the constructor set.
/// </param>
/// <param name="WireName">The name <paramref name="Id" /> hashes, registered at run time so diffs can print it.</param>
/// <param name="HostEditable">
///     False for a member marked <c>[NumosSerialize(HostEditable = false)]</c> on a type restored in place, which the
///     recording boundary leaves out of its hash.
/// </param>
/// <param name="Unit">
///     The quantity alias the member is declared with, such as <c>Kelvin</c>, or null. Resolving the shape loses the
///     alias, so the declaration keeps it here for the member descriptors.
/// </param>
/// <param name="Order">The member's position in declaration order, which the member descriptors follow.</param>
internal sealed record StateFieldModel(
    int Id, string MemberName, StateShape Shape, string Name, bool Settable, bool InitOnly, string WireName,
    bool HostEditable = true, string? Unit = null, int Order = 0);

/// <param name="Name">The parameter name.</param>
/// <param name="Shape">The parameter type, classified with <see cref="StateShape.FromParameter" />.</param>
/// <param name="TypeDisplay">The parameter type as written, for diagnostics.</param>
/// <param name="DefaultLiteral">
///     The parameter's default value as a C# literal (<c>default</c> for null), or null when it has none.
/// </param>
internal sealed record StateParameterModel(string Name, StateShape Shape, string TypeDisplay, string? DefaultLiteral);

/// <param name="Parameters">The constructor's parameters in order.</param>
internal sealed record StateConstructorModel(EquatableArray<StateParameterModel> Parameters);

/// <summary>
///     How the generated reader builds a value: constructor arguments, then initializer assignments, both as complete
///     expressions over the <c>field{id}</c>/<c>has{id}</c> locals.
/// </summary>
internal sealed record StateReadPlan(EquatableArray<string> Arguments, EquatableArray<StateInitializerModel> Initializers)
{
    /// <summary>
    ///     Gets the plan with no arguments and no initializers, for a type that is restored in place.
    /// </summary>
    public static StateReadPlan Empty { get; } = new(EquatableArray<string>.Empty, EquatableArray<StateInitializerModel>.Empty);
}

/// <param name="MemberName">The escaped member to assign.</param>
/// <param name="Expression">The value to assign.</param>
/// <param name="Id">The field id, whose <c>has{id}</c> local guards an assignment after construction.</param>
/// <param name="AfterConstruction">
///     Whether to assign after construction when the field was read, rather than in the object initializer.
/// </param>
internal sealed record StateInitializerModel(string MemberName, string Expression, int Id, bool AfterConstruction);

/// <summary>
///     How a solver configuration's generated <c>CreateSnapshot</c> works.
/// </summary>
internal enum SnapshotKind
{
    /// <summary>
    ///     Not generated: the type isn't a solver configuration, or it writes its own.
    /// </summary>
    None,

    /// <summary>
    ///     Every member is provably immutable, so the snapshot is the instance itself.
    /// </summary>
    Self,

    /// <summary>
    ///     A round trip through the wire format, which deep-copies and re-resolves gas references.
    /// </summary>
    Copy,
}

/// <summary>
///     How one class takes part in a group of generated hooks that a derived class overrides.
/// </summary>
internal enum StateHookKind
{
    /// <summary>
    ///     The class has no hooks of the group. Its generated interface members, if any, do the work inline.
    /// </summary>
    None,

    /// <summary>
    ///     The class declares the hooks as <c>protected virtual</c>, because no base class has them.
    /// </summary>
    Introduce,

    /// <summary>
    ///     The class overrides a base class's hooks for its own members and calls the base first.
    /// </summary>
    Override,

    /// <summary>
    ///     A base class has the hooks and this class adds nothing to them.
    /// </summary>
    Inherit,
}

/// <param name="Id">The field's wire id.</param>
/// <param name="WireName">The name the id hashes.</param>
/// <param name="Description">What the field's codec writes, from <see cref="SchemaFingerprints.Describe" />.</param>
internal sealed record SchemaEntry(int Id, string WireName, string Description);

/// <summary>
///     The state hooks of one class in a chain of classes restored in place, and what its generated interface members
///     cover.
/// </summary>
/// <param name="Kind">How the class takes part in the state hooks. Never <see cref="StateHookKind.None" />.</param>
/// <param name="IsLevelOnly">
///     Whether the class is a base class that is neither a class solver nor a simulation component. It gets the hooks
///     and no interfaces.
/// </param>
/// <param name="ImplementsTarget">
///     Whether the class implements <c>INumosStateTarget</c>, because no base class does.
/// </param>
/// <param name="ChainHasFields">Whether the class or a base class has a state field.</param>
/// <param name="ChainHasBulk">Whether the class or a base class has a field that is not host-editable.</param>
/// <param name="EmitsSchemaFields">
///     Whether the class publishes its schema entries as a <c>protected</c> constant, for a derived class in another
///     assembly. Only a class restored in place that isn't sealed does.
/// </param>
/// <param name="HidesSchemaFields">Whether a base class already has that constant, so this one hides it.</param>
/// <param name="Fingerprint">The schema fingerprint over every state field of the class and its base classes.</param>
/// <param name="SchemaFields">The constant's text: wire name and description of every entry, each ended by a zero.</param>
internal sealed record StateHooksModel(
    StateHookKind Kind,
    bool IsLevelOnly,
    bool ImplementsTarget,
    bool ChainHasFields,
    bool ChainHasBulk,
    bool EmitsSchemaFields,
    bool HidesSchemaFields,
    ulong Fingerprint = 0,
    string SchemaFields = "");

/// <param name="Type">The type to reopen.</param>
/// <param name="TypeName">The fully qualified type, used as the interface's type argument.</param>
/// <param name="IsReferenceType">Whether generated equality and ordering need null checks.</param>
/// <param name="IsSolverConfiguration">Whether to implement <c>IAtmosSolverConfiguration</c>'s equality and hash.</param>
/// <param name="IsWorldSolver">
///     Whether the type is a class solver, which gets <c>INumosStateTarget</c> rather than <c>INumosState&lt;T&gt;</c>.
/// </param>
/// <param name="StateKey">The constant the type's <c>Key</c> property returns, or null.</param>
/// <param name="Snapshot">Whether and how to generate <c>CreateSnapshot</c>.</param>
/// <param name="Fields">The state fields in ascending id order.</param>
/// <param name="Read">How the reader constructs the value.</param>
/// <param name="ComponentKey">
///     The <c>[SimulationComponent]</c> key, or null when the type is not a simulation component. A component is
///     restored in place like a class solver, and also gets <c>ISimulationComponentDefinition&lt;T&gt;</c>.
/// </param>
/// <param name="Hooks">
///     The state hooks, or null when the type has none and its interface members do the work inline: a sealed type
///     whose base classes hold no state.
/// </param>
internal sealed record StateTypeModel(
    PartialTypeModel Type,
    string TypeName,
    bool IsReferenceType,
    bool IsSolverConfiguration,
    bool IsWorldSolver,
    string? StateKey,
    SnapshotKind Snapshot,
    EquatableArray<StateFieldModel> Fields,
    StateReadPlan Read,
    string? ComponentKey = null,
    StateHooksModel? Hooks = null)
{
    /// <summary>
    ///     Gets whether Numos reads state back into an existing instance, so the type gets
    ///     <c>INumosStateTarget</c> rather than <c>INumosState&lt;T&gt;</c>.
    /// </summary>
    public bool RestoresInPlace => IsWorldSolver || ComponentKey is not null;
}

/// <summary>
///     A state field before alias resolution, with the location its type diagnostic points at.
/// </summary>
internal sealed record StateFieldDeclaration(StateFieldModel Field, string DisplayName, LocationInfo? Location)
{
    /// <summary>
    ///     Resolves quantity aliases in the field's shape and reports a type the generator cannot handle.
    /// </summary>
    /// <param name="aliases">The units catalog.</param>
    /// <param name="diagnostics">Receives the unsupported-type diagnostic.</param>
    /// <param name="describeDeclared">
    ///     Whether the diagnostic names the type as declared. Otherwise it names only the unsupported part.
    /// </param>
    /// <returns>The field with its shape resolved, or null when part of the shape is unsupported.</returns>
    public StateFieldModel? Resolve(
        EquatableArray<QuantityAlias> aliases, List<DiagnosticInfo> diagnostics, bool describeDeclared)
    {
        StateShape shape = Field.Shape.Resolve(aliases);
        if (shape.FindUnsupported() is not { } unsupported)
            return Field with { Shape = shape };

        diagnostics.Add(
            DiagnosticInfo.Create(
                StateDiagnostics.UnsupportedType,
                Location,
                DisplayName,
                describeDeclared && shape.Kind != StateShapeKind.Unsupported ? shape.DescribeDeclared() : unsupported,
                unsupported));

        return null;
    }
}

/// <summary>
///     What the transform knows about one type with <c>[NumosSerialize]</c> or <c>[NumosScratch]</c> members.
///     <see cref="Fields" /> is empty when a diagnostic already ruled out generating anything.
/// </summary>
/// <param name="Prototype">
///     The model without its fields and read plan. Resolving the declaration fills those in.
/// </param>
/// <param name="DisplayName">The type for diagnostics.</param>
/// <param name="Location">Where type-level diagnostics point.</param>
/// <param name="Registration">The fully qualified type to register for loading, or null.</param>
/// <param name="CanGenerate">False once a diagnostic has ruled out generating anything.</param>
/// <param name="HasState">
///     Whether the type marks a <c>[NumosSerialize]</c> member. A class solver with only <c>[NumosScratch]</c> members
///     gets <see cref="Scratch" /> and no state interface.
/// </param>
/// <param name="Fields">The state fields in ascending id order, before alias resolution.</param>
/// <param name="Constructors">Every constructor the generated reader could call.</param>
/// <param name="Scratch">The class solver's scratch resets, or null when it has no <c>[NumosScratch]</c> member.</param>
/// <param name="Diagnostics">Diagnostics found so far.</param>
/// <param name="ComponentRegistration">
///     The fully qualified simulation component to add to the catalog, or null when the type is not one.
/// </param>
/// <param name="Chain">What the type's base classes add, or null when it derives from object.</param>
internal sealed record StateTypeDeclaration(
    StateTypeModel Prototype,
    string DisplayName,
    LocationInfo? Location,
    string? Registration,
    bool CanGenerate,
    bool HasState,
    EquatableArray<StateFieldDeclaration> Fields,
    EquatableArray<StateConstructorModel> Constructors,
    ScratchTypeModel? Scratch,
    EquatableArray<DiagnosticInfo> Diagnostics,
    string? ComponentRegistration = null,
    StateChainDeclaration? Chain = null);

/// <summary>
///     A base class that is neither a class solver nor a simulation component, analyzed by the class restored in place
///     that derives from it. That class emits the base's hooks.
/// </summary>
/// <param name="Prototype">The base's model without its fields. <see cref="StateTypeModel.Hooks" /> is set.</param>
/// <param name="Fields">The base's own state fields in ascending id order, before alias resolution.</param>
/// <param name="Scratch">The base's scratch hook, or null when it has no <c>[NumosScratch]</c> member.</param>
/// <param name="CanGenerate">False once a diagnostic has ruled out generating the base's hooks.</param>
/// <remarks>
///     The diagnostics about the base live in <see cref="StateChainDeclaration.LevelDiagnostics" />.
/// </remarks>
internal sealed record StateLevelDeclaration(
    StateTypeModel Prototype,
    EquatableArray<StateFieldDeclaration> Fields,
    ScratchTypeModel? Scratch,
    bool CanGenerate);

/// <summary>
///     What the base classes of a class restored in place contribute, as the derived class's transform found it.
/// </summary>
/// <param name="Levels">
///     The base classes the derived class emits, outermost first: the ones between it and the nearest base class
///     restored in place that hold state or scratch members.
/// </param>
/// <param name="LevelDiagnostics">
///     The diagnostics about those base classes and their members. Two derived classes find the same ones, so they are
///     reported once.
/// </param>
/// <param name="PlainBaseNames">
///     Every base class the derived class analyzes as a level, by fully qualified name. Their own state output is
///     dropped, since a level is restored in place rather than built from data.
/// </param>
/// <param name="ChainFields">
///     Every state field of every base class in this compilation, before alias resolution, for the fingerprint.
/// </param>
/// <param name="MetadataSchema">The schema entries a base class in another assembly published.</param>
internal sealed record StateChainDeclaration(
    EquatableArray<StateLevelDeclaration> Levels,
    EquatableArray<DiagnosticInfo> LevelDiagnostics,
    EquatableArray<string> PlainBaseNames,
    EquatableArray<StateFieldDeclaration> ChainFields,
    EquatableArray<SchemaEntry> MetadataSchema);

/// <param name="Model">The base's hooks to emit, or null when the base has errors or no state fields of its own.</param>
/// <param name="Scratch">The base's scratch hook to emit, or null.</param>
internal sealed record StateLevelOutput(StateTypeModel? Model, ScratchTypeModel? Scratch);

/// <summary>
///     A resolved declaration. It holds the diagnostics to report and, when there were no errors, the models to emit.
///     It also holds the fully qualified type to register for loading, if it is a solver configuration that the module
///     initializer can name.
/// </summary>
/// <param name="Model">The model to emit, or null.</param>
/// <param name="Scratch">The scratch resets to emit, or null.</param>
/// <param name="Registration">The solver configuration to register for loading, or null.</param>
/// <param name="Diagnostics">The diagnostics to report.</param>
/// <param name="ComponentRegistration">The simulation component to add to the catalog, or null.</param>
/// <param name="Levels">The base classes to emit hooks into.</param>
/// <param name="LevelDiagnostics">The diagnostics about those base classes, reported once across derived classes.</param>
internal sealed record StateTypeResult(
    StateTypeModel? Model, ScratchTypeModel? Scratch, string? Registration, EquatableArray<DiagnosticInfo> Diagnostics,
    string? ComponentRegistration = null, EquatableArray<StateLevelOutput> Levels = default,
    EquatableArray<DiagnosticInfo> LevelDiagnostics = default);
