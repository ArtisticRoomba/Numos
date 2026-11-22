using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Describes a class solver's stage name, placement, rate, and neighborhood, and creates its instances.
/// </summary>
/// <remarks>
///     <para>
///         The <c>Numos.API</c> source generator builds one descriptor for each class marked
///         <see cref="AtmosSolverAttribute" /> and exposes it as <see cref="IAtmosSolverDefinition{TSelf}.Descriptor" />.
///         A descriptor is immutable.
///     </para>
///     <para>
///         <see cref="RunsPerSecond" /> and <see cref="Interval" /> are both zero when the declaration sets no rate.
///         The stage then runs every tick.
///     </para>
/// </remarks>
[PublicAPI]
public sealed class AtmosSolverDescriptor
{
    /// <summary>
    ///     Creates a descriptor.
    /// </summary>
    /// <param name="name">The stage name.</param>
    /// <param name="solverType">The solver class. It must implement <see cref="IAtmosWorldSolver" />.</param>
    /// <param name="create">Creates a new instance of <paramref name="solverType" /> on each call.</param>
    /// <param name="after">The stage this one runs directly after, or <see langword="null" />.</param>
    /// <param name="before">The stage this one runs directly before, or <see langword="null" />.</param>
    /// <param name="runsPerSecond">Runs per second, or zero when unset.</param>
    /// <param name="interval">The interval in ticks, or zero when unset.</param>
    /// <param name="neighbors">The neighborhood to compile, or <see langword="null" /> for an empty view.</param>
    /// <exception cref="ArgumentException">
    ///     <paramref name="name" /> is null, empty, or whitespace. Or <paramref name="solverType" /> does not implement
    ///     <see cref="IAtmosWorldSolver" />. Or both <paramref name="after" /> and <paramref name="before" /> are set. Or
    ///     both <paramref name="runsPerSecond" /> and <paramref name="interval" /> are set.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="solverType" /> or <paramref name="create" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="runsPerSecond" /> or <paramref name="interval" /> is negative.
    /// </exception>
    public AtmosSolverDescriptor(
        string name, Type solverType, Func<IAtmosWorldSolver> create,
        string? after = null, string? before = null, int runsPerSecond = 0, int interval = 0,
        AtmosNeighborSelection? neighbors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(solverType);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentOutOfRangeException.ThrowIfNegative(runsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(interval);

        if (!typeof(IAtmosWorldSolver).IsAssignableFrom(solverType))
        {
            throw new ArgumentException(
                $"Solver type {solverType.FullName} does not implement {nameof(IAtmosWorldSolver)}.",
                nameof(solverType));
        }

        if (after is not null && before is not null)
            throw new ArgumentException($"Solver '{name}' sets both after and before.", nameof(before));

        if (runsPerSecond > 0 && interval > 0)
            throw new ArgumentException($"Solver '{name}' sets both runsPerSecond and interval.", nameof(interval));

        Name = name;
        SolverType = solverType;
        Create = create;
        After = after;
        Before = before;
        RunsPerSecond = runsPerSecond;
        Interval = interval;
        Neighbors = neighbors;
    }

    /// <summary>
    ///     Gets the stage name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Gets the solver class.
    /// </summary>
    public Type SolverType { get; }

    /// <summary>
    ///     Gets the stage this one runs directly after, or <see langword="null" />.
    /// </summary>
    public string? After { get; }

    /// <summary>
    ///     Gets the stage this one runs directly before, or <see langword="null" />.
    /// </summary>
    public string? Before { get; }

    /// <summary>
    ///     Gets how many times a second the stage runs, or zero when the rate is unset.
    /// </summary>
    public int RunsPerSecond { get; }

    /// <summary>
    ///     Gets the stage's interval in ticks, or zero when the interval is unset.
    /// </summary>
    public int Interval { get; }

    /// <summary>
    ///     Gets the neighborhood Numos compiles for the stage, or <see langword="null" /> for an empty view.
    /// </summary>
    public AtmosNeighborSelection? Neighbors { get; }

    /// <summary>
    ///     Gets the factory that creates a new solver instance on each call.
    /// </summary>
    public Func<IAtmosWorldSolver> Create { get; }

    /// <summary>
    ///     Returns the stage name and solver class.
    /// </summary>
    public override string ToString()
    {
        return $"{Name}: {SolverType.FullName}";
    }
}

/// <summary>
///     A solver class that carries its own <see cref="AtmosSolverDescriptor" />.
/// </summary>
/// <typeparam name="TSelf">The solver class itself.</typeparam>
/// <remarks>
///     The <c>Numos.API</c> source generator implements this interface on every class marked
///     <see cref="AtmosSolverAttribute" />. Do not implement it by hand on such a class. That is a build error
///     (<c>NUMOSSOLVER020</c>).
/// </remarks>
[PublicAPI]
public interface IAtmosSolverDefinition<TSelf>
    where TSelf : class, IAtmosWorldSolver, IAtmosSolverDefinition<TSelf>
{
    /// <summary>
    ///     Gets the descriptor of <typeparamref name="TSelf" />.
    /// </summary>
    static abstract AtmosSolverDescriptor Descriptor { get; }
}
