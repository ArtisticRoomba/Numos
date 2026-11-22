using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim;

/// <summary>
///     One built-in solver stage, as <see cref="BuiltInStageTable" /> lists it.
/// </summary>
/// <param name="Name">The stage name. The public <c>AtmosBuiltInSolvers</c> constants are these strings.</param>
/// <param name="RunsPerSecond">The default runs per second of simulated time.</param>
/// <param name="MarksOwnChanges">
///     Whether the stage advances the revision of every chunk it writes. A tick then skips the revision bump of every
///     awake chunk before the stage runs.
/// </param>
/// <param name="EulerPosition">
///     The index of the stage in the <see cref="AtmosTransportModel.Euler" /> default layout, or
///     <see cref="BuiltInStageTable.Absent" />.
/// </param>
/// <param name="LegacyPosition">
///     The index of the stage in the <see cref="AtmosTransportModel.Legacy" /> default layout, or
///     <see cref="BuiltInStageTable.Absent" />.
/// </param>
/// <param name="SolveKernel">
///     Runs the stage on one kernel. <see langword="null" /> for the explicit-link stages, which need world topology.
///     The standalone <see cref="AtmosKernel" /> tick skips those.
/// </param>
internal sealed record BuiltInStage(
    string Name,
    int RunsPerSecond,
    bool MarksOwnChanges,
    int EulerPosition,
    int LegacyPosition,
    Action<AtmosKernel, AtmosSolverExecutionContext>? SolveKernel)
{
    /// <summary>
    ///     Gets the index of the stage in a model's default layout.
    /// </summary>
    /// <param name="model">The transport model.</param>
    /// <returns>The index, or <see cref="BuiltInStageTable.Absent" /> when the layout leaves the stage out.</returns>
    internal int PositionIn(AtmosTransportModel model)
    {
        return model switch
        {
            AtmosTransportModel.Euler => EulerPosition,
            AtmosTransportModel.Legacy => LegacyPosition,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown transport model.")
        };
    }
}

/// <summary>
///     The built-in solver stages, in canonical order.
/// </summary>
/// <remarks>
///     <para>
///         Every list of built-in stages reads this table: the world pipeline and its default layouts, the stage
///         schedule, and the standalone <see cref="AtmosKernel" /> tick. <c>Numos.API</c> pairs each entry with the
///         world method that runs it.
///     </para>
///     <para>
///         <c>derived-refresh</c> and <c>advection</c> share a rate, as do <c>advection</c> and <c>boundary-flow</c>,
///         and <c>thermodynamics</c> and <c>thermal-boundary</c>. Each pair hands data over within a tick.
///     </para>
/// </remarks>
internal static class BuiltInStageTable
{
    /// <summary>
    ///     The layout position of a stage that a default layout leaves out.
    /// </summary>
    internal const int Absent = -1;

    internal const string DerivedRefresh = "derived-refresh";
    internal const string EulerFlow = "euler-flow";
    internal const string Advection = "advection";
    internal const string VacuumCleanup = "vacuum-cleanup";
    internal const string ExplicitGasTransport = "explicit-gas-transport";
    internal const string BoundaryFlow = "boundary-flow";
    internal const string Thermodynamics = "thermodynamics";
    internal const string ExplicitThermalTransport = "explicit-thermal-transport";
    internal const string ThermalBoundary = "thermal-boundary";
    internal const string GasReactions = "gas-reactions";

    private const int Gas = BuiltInStageSchedule.GasRunsPerSecond;
    private const int Thermal = BuiltInStageSchedule.ThermalRunsPerSecond;

    // The static initializers run in text order, so the lookups below can read this array.
    private static readonly ImmutableArray<BuiltInStage> Stages =
    [
        new(DerivedRefresh, Gas, MarksOwnChanges: false, EulerPosition: Absent, LegacyPosition: 0,
            static (kernel, context) => kernel.SolveDerivedRefresh(context)),
        new(EulerFlow, BuiltInStageSchedule.EulerRunsPerSecond, MarksOwnChanges: true, EulerPosition: 0,
            LegacyPosition: Absent, static (kernel, context) => kernel.SolveEulerFlow(context)),
        new(Advection, Gas, MarksOwnChanges: false, EulerPosition: Absent, LegacyPosition: 1,
            static (kernel, context) => kernel.SolveAdvection(context)),
        new(VacuumCleanup, Gas, MarksOwnChanges: true, EulerPosition: 1, LegacyPosition: 2,
            static (kernel, context) => kernel.SolveVacuumCleanup(context)),
        new(ExplicitGasTransport, Gas, MarksOwnChanges: false, EulerPosition: 2, LegacyPosition: 3, null),
        new(BoundaryFlow, Gas, MarksOwnChanges: false, EulerPosition: Absent, LegacyPosition: 4,
            static (kernel, context) => kernel.SolveBoundaryFlow(context)),
        new(Thermodynamics, Thermal, MarksOwnChanges: false, EulerPosition: 3, LegacyPosition: 5,
            static (kernel, context) => kernel.SolveThermodynamics(context)),
        new(ExplicitThermalTransport, Thermal, MarksOwnChanges: false, EulerPosition: 4, LegacyPosition: 6, null),
        new(ThermalBoundary, Thermal, MarksOwnChanges: false, EulerPosition: 5, LegacyPosition: 7,
            static (kernel, context) => kernel.SolveThermalBoundary(context)),
        new(GasReactions, Gas, MarksOwnChanges: false, EulerPosition: 6, LegacyPosition: 8,
            static (kernel, context) => kernel.SolveGasReactions(context))
    ];

    private static readonly Dictionary<string, BuiltInStage> ByName =
        Stages.ToDictionary(static stage => stage.Name, StringComparer.Ordinal);

    private static readonly ImmutableArray<BuiltInStage> EulerLayout = CreateLayout(AtmosTransportModel.Euler);
    private static readonly ImmutableArray<BuiltInStage> LegacyLayout = CreateLayout(AtmosTransportModel.Legacy);

    /// <summary>
    ///     Gets every built-in stage, in canonical order.
    /// </summary>
    internal static ImmutableArray<BuiltInStage> All => Stages;

    /// <summary>
    ///     Finds a built-in stage by name.
    /// </summary>
    /// <param name="name">The stage name.</param>
    /// <param name="stage">The stage, or <see langword="null" /> when <paramref name="name" /> is not built in.</param>
    /// <returns><see langword="true" /> when <paramref name="name" /> is a built-in stage.</returns>
    internal static bool TryGet(string name, [NotNullWhen(true)] out BuiltInStage? stage)
    {
        return ByName.TryGetValue(name, out stage);
    }

    /// <summary>
    ///     Gets the stages that a new world with this transport model runs, in execution order.
    /// </summary>
    /// <param name="model">The transport model.</param>
    /// <returns>The layout. Every stage in it starts enabled.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model" /> is not a defined model.</exception>
    internal static ImmutableArray<BuiltInStage> GetLayout(AtmosTransportModel model)
    {
        return model switch
        {
            AtmosTransportModel.Euler => EulerLayout,
            AtmosTransportModel.Legacy => LegacyLayout,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown transport model.")
        };
    }

    private static ImmutableArray<BuiltInStage> CreateLayout(AtmosTransportModel model)
    {
        return
        [
            ..Stages.Where(stage => stage.PositionIn(model) != Absent).OrderBy(stage => stage.PositionIn(model))
        ];
    }
}
