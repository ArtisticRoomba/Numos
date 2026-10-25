using Numos.API;
using Numos.CoreSim;

namespace Numos.Workbench;

/// <summary>
///     Pure list logic behind a solver pipeline editor.
/// </summary>
/// <remarks>
///     <para>
///         Every edit method takes the current <see cref="AtmosWorldSolverPipeline.Steps" /> and returns the full
///         requested layout, or <see langword="null" /> when the edit does nothing. Pass the result to
///         <see cref="WorkbenchSession.SetSolverLayout" />, which records one operation.
///     </para>
///     <para>
///         The helper has no UI dependency, so the viewer and any other front end share one set of rules.
///     </para>
/// </remarks>
public static class SolverPipelineEditor
{
    /// <summary>
    ///     Returns the layout with the stage at <paramref name="index" /> moved one place earlier.
    /// </summary>
    /// <param name="steps">The current stages in execution order.</param>
    /// <param name="index">The stage to move.</param>
    /// <returns>The new layout, or <see langword="null" /> when the stage is first or the index is out of range.</returns>
    public static IReadOnlyList<AtmosWorldSolverLayoutEntry>? MoveUp(IReadOnlyList<AtmosWorldSolverStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Swap(steps, index - 1, index);
    }

    /// <summary>
    ///     Returns the layout with the stage at <paramref name="index" /> moved one place later.
    /// </summary>
    /// <param name="steps">The current stages in execution order.</param>
    /// <param name="index">The stage to move.</param>
    /// <returns>The new layout, or <see langword="null" /> when the stage is last or the index is out of range.</returns>
    public static IReadOnlyList<AtmosWorldSolverLayoutEntry>? MoveDown(IReadOnlyList<AtmosWorldSolverStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Swap(steps, index, index + 1);
    }

    /// <summary>
    ///     Returns the layout without the built-in stage at <paramref name="index" />.
    /// </summary>
    /// <param name="steps">The current stages in execution order.</param>
    /// <param name="index">The stage to remove.</param>
    /// <returns>
    ///     The new layout, or <see langword="null" /> when the index is out of range or the stage is custom.
    ///     The host owns custom stages, so an editor cannot recreate one after it removes it.
    /// </returns>
    public static IReadOnlyList<AtmosWorldSolverLayoutEntry>? Remove(IReadOnlyList<AtmosWorldSolverStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if ((uint)index >= (uint)steps.Count || steps[index].Kind != AtmosWorldSolverKind.BuiltIn)
            return null;

        var result = ToLayout(steps);
        result.RemoveAt(index);
        return result;
    }

    /// <summary>
    ///     Lists the built-in stage names that the pipeline does not contain, in canonical order.
    /// </summary>
    /// <param name="steps">The current stages in execution order.</param>
    public static IReadOnlyList<string> GetAddableStages(IReadOnlyList<AtmosWorldSolverStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return AtmosWorldSolverPipeline.BuiltInStageNames
            .Where(name => steps.All(step => step.Name != name))
            .ToArray();
    }

    /// <summary>
    ///     Returns the layout with an enabled built-in stage inserted at its canonical position.
    /// </summary>
    /// <param name="steps">The current stages in execution order.</param>
    /// <param name="name">A built-in stage name from <see cref="AtmosWorldSolverPipeline.BuiltInStageNames" />.</param>
    /// <returns>The new layout, or <see langword="null" /> when the name is unknown or already present.</returns>
    /// <remarks>
    ///     The stage goes before the first existing built-in stage that follows it in the canonical order. When none
    ///     follows it, the stage goes last. Custom stages never decide the position, so the result does not depend on
    ///     where the host registered them.
    /// </remarks>
    public static IReadOnlyList<AtmosWorldSolverLayoutEntry>? Add(IReadOnlyList<AtmosWorldSolverStep> steps, string name)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(name);
        var canonical = AtmosWorldSolverPipeline.BuiltInStageNames;
        int rank = IndexOf(canonical, name);
        if (rank < 0 || steps.Any(step => step.Name == name))
            return null;

        var result = ToLayout(steps);
        int position = result.Count;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Kind == AtmosWorldSolverKind.BuiltIn && IndexOf(canonical, steps[i].Name) > rank)
            {
                position = i;
                break;
            }
        }

        result.Insert(position, new AtmosWorldSolverLayoutEntry(name, true));
        return result;
    }

    /// <summary>
    ///     Lists the pipeline problems that a stage list can show. Order is never a warning.
    /// </summary>
    /// <param name="steps">The current stages in execution order.</param>
    /// <returns>One sentence per problem, or an empty list.</returns>
    /// <remarks>
    ///     A disabled stage does not run, so the checks count only enabled stages.
    /// </remarks>
    public static IReadOnlyList<string> GetWarnings(IReadOnlyList<AtmosWorldSolverStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        bool euler = IsRunning(steps, AtmosBuiltInSolvers.EulerFlow);
        bool advection = IsRunning(steps, AtmosBuiltInSolvers.Advection);
        bool boundary = IsRunning(steps, AtmosBuiltInSolvers.BoundaryFlow);
        var warnings = new List<string>();
        if (!euler && !advection)
            warnings.Add("No enabled stage moves gas between voxels. Enable euler-flow or advection.");

        if (euler && advection)
            warnings.Add("Both euler-flow and advection are enabled. Gas moves twice per tick.");

        if (boundary && !advection)
            warnings.Add("boundary-flow is enabled without advection. Euler-flow already crosses chunk faces.");

        return warnings;
    }

    /// <summary>
    ///     Returns a one-line description of a stage.
    /// </summary>
    /// <param name="name">A stage name.</param>
    /// <returns>The description of a built-in stage, or a generic line for any other name.</returns>
    public static string Describe(string name)
    {
        return name switch
        {
            AtmosBuiltInSolvers.EulerFlow =>
                "Compressible Euler gas transport inside and between chunks, with CFL substeps.",
            AtmosBuiltInSolvers.Advection =>
                "Legacy pressure-relaxation gas advection inside each chunk.",
            AtmosBuiltInSolvers.ExplicitGasTransport =>
                "Gas transport across portals, docks, and other explicit links.",
            AtmosBuiltInSolvers.BoundaryFlow =>
                "Legacy gas transport across chunk faces. Pairs with advection.",
            AtmosBuiltInSolvers.Thermodynamics =>
                "Thermal diffusion and phase changes inside each chunk.",
            AtmosBuiltInSolvers.ExplicitThermalTransport =>
                "Heat transport across portals, docks, and other explicit links.",
            AtmosBuiltInSolvers.ThermalBoundary =>
                "Heat transport across chunk faces.",
            AtmosBuiltInSolvers.GasReactions =>
                "Per-voxel gas reactions from the reaction configuration.",
            _ => "A stage registered by the host."
        };
    }

    private static IReadOnlyList<AtmosWorldSolverLayoutEntry>? Swap(
        IReadOnlyList<AtmosWorldSolverStep> steps,
        int first,
        int second)
    {
        if (first < 0 || second >= steps.Count)
            return null;

        var result = ToLayout(steps);
        (result[first], result[second]) = (result[second], result[first]);
        return result;
    }

    private static List<AtmosWorldSolverLayoutEntry> ToLayout(IReadOnlyList<AtmosWorldSolverStep> steps)
    {
        var result = new List<AtmosWorldSolverLayoutEntry>(steps.Count + 1);
        foreach (var step in steps)
            result.Add(new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled));

        return result;
    }

    private static bool IsRunning(IReadOnlyList<AtmosWorldSolverStep> steps, string name)
    {
        return steps.Any(step => step.Name == name && step.IsEnabled);
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
                return i;
        }

        return -1;
    }
}
