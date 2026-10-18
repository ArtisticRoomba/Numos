using JetBrains.Annotations;
using Numos.CoreSim.Solvers;

namespace Numos.API;

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
///         excluded from snapshots, checkpoints, recordings, and state hashes, and dropped on restore and whenever the
///         configuration changes, so the factory must be a deterministic function of authoritative inputs such as
///         <see cref="AtmosSimulation.Config" />. Evolving state that has to roll back belongs in a
///         <see cref="ChunkField{T}" /> registered with <c>rollback: true</c>.
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
///             });
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
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    public SolverDataKey(string name, Func<AtmosSimulation, T> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Descriptor = new SolverDataDescriptor(name);
        Factory = factory;
    }

    /// <summary>
    ///     Gets the diagnostic name.
    /// </summary>
    public string Name => Descriptor.Name;

    internal SolverDataDescriptor Descriptor { get; }
    internal Func<AtmosSimulation, T> Factory { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}