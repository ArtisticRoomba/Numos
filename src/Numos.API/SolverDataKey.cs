using JetBrains.Annotations;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Says when Numos drops a value cached under a <see cref="SolverDataKey{T}" />.
/// </summary>
[PublicAPI]
public enum SolverDataLifetime : byte
{
    /// <summary>
    ///     Kept across ticks and configuration changes; dropped when a checkpoint is restored or the simulation is
    ///     disposed.
    /// </summary>
    UntilRestore,

    /// <summary>
    ///     Also dropped when the simulation's configuration changes. During a tick the value belongs to the
    ///     tick-start configuration, so a configuration applied by a solver callback takes effect on the next tick.
    ///     Use this for caches derived from the gas registry or <see cref="Numos.CoreSim.IAtmosSolverConfiguration" />
    ///     settings.
    /// </summary>
    UntilConfigChange
}

/// <summary>
///     Names one piece of transient data shared by solvers in a simulation, together with the factory that builds it.
/// </summary>
/// <typeparam name="T">The stored reference type. Interfaces, classes, arrays, and collections all work.</typeparam>
/// <remarks>
///     <para>
///         The key instance is the identity: two keys with the same <see cref="Name" /> are still different slots.
///         Declare keys as <see langword="static readonly" /> fields and share the field between cooperating solvers.
///         Every key consumes a slot that is never reclaimed, so don't create keys per tick or per simulation.
///     </para>
///     <para>
///         Values are cached per simulation and read with <see cref="AtmosSimulation.GetSolverData{T}" />. They are
///         excluded from snapshots, checkpoints, recordings, and state hashes, and dropped on restore, so the factory
///         must be a deterministic function of authoritative inputs such as <see cref="AtmosSimulation.Config" />.
///         Evolving state that has to roll back belongs in a <see cref="ChunkField{T}" /> registered with
///         <c>rollback: true</c>.
///     </para>
///     <para>
///         A factory may read other keys. Requesting its own key, directly or through another factory, throws instead
///         of recursing. A factory that throws or returns null caches nothing, so the next read tries again.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     static class HeaterData
///     {
///         // One coefficient per registered gas, rebuilt when the gas registry changes.
///         public static readonly SolverDataKey&lt;float[]&gt; Coefficients = new(
///             "my-mod/heater/coefficients",
///             static simulation =>
///             {
///                 var gases = simulation.Config.GasRegistry;
///                 var coefficients = new float[gases.Count];
///                 for (int gasId = 0; gasId &lt; coefficients.Length; gasId++)
///                     coefficients[gasId] = 1f / gases[gasId].MolarHeatCapacityAtConstantVolume;
///
///                 return coefficients;
///             },
///             SolverDataLifetime.UntilConfigChange);
///     }
///
///     float[] coefficients = simulation.GetSolverData(HeaterData.Coefficients);
///     </code>
/// </example>
[PublicAPI]
public sealed class SolverDataKey<T> where T : class
{
    /// <summary>
    ///     Creates a key with its own storage slot.
    /// </summary>
    /// <param name="name">A diagnostic name used in error messages. It does not need to be unique.</param>
    /// <param name="factory">
    ///     Builds the value the first time a simulation reads the key, or the first time after the value was dropped.
    ///     It runs under the simulation's state lock, must not mutate the simulation, and must not return null.
    /// </param>
    /// <param name="lifetime">When cached values are dropped.</param>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime" /> is not a defined value.</exception>
    public SolverDataKey(
        string name, Func<AtmosSimulation, T> factory, SolverDataLifetime lifetime = SolverDataLifetime.UntilRestore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (lifetime is not (SolverDataLifetime.UntilRestore or SolverDataLifetime.UntilConfigChange))
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Unknown solver data lifetime.");

        Name = name;
        Factory = factory;
        Lifetime = lifetime;
        SlotId = SolverDataSlotIds.Next();
    }

    /// <summary>
    ///     Gets the diagnostic name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Gets when cached values are dropped.
    /// </summary>
    public SolverDataLifetime Lifetime { get; }

    internal int SlotId { get; }
    internal Func<AtmosSimulation, T> Factory { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
