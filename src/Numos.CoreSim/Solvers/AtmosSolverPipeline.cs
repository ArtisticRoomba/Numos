namespace Numos.CoreSim.Solvers;

/// <summary>
///     Ordered, mutable collection of solver delegates executed for every fixed tick.
/// </summary>
internal sealed class AtmosSolverPipeline : IDisposable
{
    private readonly Func<SolverStep[]> _createDefaults;
    private readonly IDisposable? _defaultLifetime;
    private readonly List<SolverStep> _steps = [];

    internal AtmosSolverPipeline(Func<SolverStep[]> createDefaults, IDisposable? defaultLifetime = null)
    {
        _createDefaults = createDefaults;
        _defaultLifetime = defaultLifetime;
        Reset();
    }

    internal int Count => _steps.Count;

    internal SolverStepInfo[] GetSteps()
    {
        return _steps.Select(static step =>
                new SolverStepInfo(step.Name, step.Enabled, step.Kind))
            .ToArray();
    }

    internal void Register(string name, SolverStepKind kind,
        Action<AtmosSolverExecutionContext> solver, int index)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(solver);
        if (_steps.Any(step => string.Equals(step.Name, name, StringComparison.Ordinal)))
            throw new InvalidOperationException($"A solver named '{name}' is already registered.");
        if ((uint)index > (uint)_steps.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        _steps.Insert(index, new SolverStep(name, kind, solver));
    }

    internal int IndexOf(string name)
    {
        ValidateName(name);
        return _steps.FindIndex(step => string.Equals(step.Name, name, StringComparison.Ordinal));
    }

    internal bool Unregister(string name)
    {
        int index = IndexOf(name);
        if (index < 0)
            return false;

        _steps.RemoveAt(index);
        return true;
    }

    internal bool SetEnabled(string name, bool enabled)
    {
        int index = IndexOf(name);
        if (index < 0)
            return false;

        _steps[index].Enabled = enabled;
        return true;
    }

    internal void Reset()
    {
        _steps.Clear();
        _steps.AddRange(_createDefaults());
    }

    internal void Execute(AtmosSolverExecutionContext context)
    {
        // A stage may edit the pipeline. Snapshotting makes those edits take effect on the next tick and keeps
        // the current tick deterministic.
        SolverStep[] steps = _steps.Where(static step => step.Enabled).ToArray();
        foreach (SolverStep step in steps)
            step.Solver(context);
    }

    public void Dispose()
    {
        _defaultLifetime?.Dispose();
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }
}

internal sealed class SolverStep(
    string name,
    SolverStepKind kind,
    Action<AtmosSolverExecutionContext> solver)
{
    internal string Name { get; } = name;
    internal SolverStepKind Kind { get; } = kind;
    internal Action<AtmosSolverExecutionContext> Solver { get; } = solver;
    internal bool Enabled { get; set; } = true;
}

internal enum SolverStepKind
{
    BuiltIn,
    Standard,
    Dangerous
}

internal readonly record struct SolverStepInfo(string Name, bool Enabled, SolverStepKind Kind);