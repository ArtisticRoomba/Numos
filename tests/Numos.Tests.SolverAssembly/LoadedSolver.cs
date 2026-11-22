using Numos.API;
using Numos.CoreSim.State;

namespace Numos.Tests.SolverAssembly;

/// <summary>
/// The settings of <see cref="LoadedSolver.Mode"/>.
/// </summary>
public enum LoadedMode
{
    /// <summary>
    /// The solver adds nothing.
    /// </summary>
    Idle,

    /// <summary>
    /// The solver adds <see cref="LoadedSolver.Rate"/> every run.
    /// </summary>
    Counting,
}

/// <summary>
/// Adds <see cref="Rate"/> to <see cref="Total"/> every tick while <see cref="Mode"/> is
/// <see cref="LoadedMode.Counting"/>.
/// </summary>
[AtmosSolver(Name, Interval = 1)]
public sealed partial class LoadedSolver : IAtmosWorldSolver
{
    /// <summary>
    /// The stage name.
    /// </summary>
    public const string Name = "numos-tests/loaded/solver";

    /// <summary>
    /// The amount added per run.
    /// </summary>
    [NumosSerialize] public float Rate = 1f;

    /// <summary>
    /// The sum of every run so far.
    /// </summary>
    [NumosSerialize] public float Total;

    /// <summary>
    /// Whether the solver counts.
    /// </summary>
    [NumosSerialize] public LoadedMode Mode = LoadedMode.Counting;

    /// <summary>
    /// A flag with no effect, so the tests have a boolean member.
    /// </summary>
    [NumosSerialize] public bool Flag;

    /// <inheritdoc/>
    public void Solve(AtmosWorldSolverContext context)
    {
        if (Mode == LoadedMode.Counting)
            Total += Rate;
    }
}

/// <summary>
/// One chunk field, so the loader has a chunk field key to report.
/// </summary>
[ChunkComponent(Key)]
public partial struct LoadedHeat
{
    /// <summary>
    /// The component key.
    /// </summary>
    public const string Key = "numos-tests/loaded/heat";

    /// <summary>
    /// A per-voxel value.
    /// </summary>
    [NumosSerialize] public float Value;
}

/// <summary>
/// One simulation component, so the loader has a component key to report.
/// </summary>
[SimulationComponent(Key)]
public sealed partial class LoadedCounter
{
    /// <summary>
    /// The component key.
    /// </summary>
    public const string Key = "numos-tests/loaded/counter";

    /// <summary>
    /// A count that only host edits change.
    /// </summary>
    [NumosSerialize] public int Count;
}
