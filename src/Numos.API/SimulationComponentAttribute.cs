using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     Makes a class per-simulation state: one instance per <see cref="AtmosSimulation" />, checkpointed, hashed,
///     saved, and recorded like the state of a class solver.
/// </summary>
/// <param name="key">
///     The component key. Checkpoints, recordings, and <c>.numos</c> files store it, so it must stay stable across
///     versions. Namespace it (for example <c>my-mod/pipes</c>).
/// </param>
/// <remarks>
///     <para>
///         Put the attribute on a <see langword="partial" /> class. Every type the class is nested in must be
///         <see langword="partial" /> too. The class must not be abstract, generic, or <see langword="file" />-local, and
///         it must not derive from another class. It needs a public or internal parameterless constructor, which Numos
///         calls to create the component on first use and during a restore.
///     </para>
///     <para>
///         Mark the state members <c>[NumosSerialize]</c>, as on a class solver. A restore writes them into the
///         existing instance and refills a <see cref="List{T}" />, <see cref="HashSet{T}" />, or
///         <see cref="Dictionary{TKey, TValue}" /> member in place. <c>[NumosScratch]</c> members are reset after a
///         restore. An unmarked mutable member is a build warning (<c>NUMOSSTATE016</c>).
///     </para>
///     <para>
///         The generator implements <see cref="ISimulationComponentDefinition{TSelf}" /> on the class. The
///         assembly's module initializer adds the class to <see cref="SimulationComponentCatalog" /> when the assembly
///         loads.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [SimulationComponent("my-mod/pipes")]
///     public sealed partial class PipeNetwork
///     {
///         [NumosSerialize] public int NextId;
///         [NumosSerialize(HostEditable = false)] public readonly List&lt;PipeNode&gt; Nodes = [];
///     }
///
///     // In a stage:
///     PipeNetwork pipes = context.Get&lt;PipeNetwork&gt;(simulation);
///     </code>
/// </example>
[PublicAPI]
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SimulationComponentAttribute(string key) : Attribute
{
    /// <summary>
    ///     Gets the component key.
    /// </summary>
    public string Key { get; } = key;
}

/// <summary>
///     A <see cref="SimulationComponentAttribute" /> class that carries its own key and factory.
/// </summary>
/// <typeparam name="TSelf">The component class itself.</typeparam>
/// <remarks>
///     The <c>Numos.API</c> source generator implements this interface on every class marked
///     <see cref="SimulationComponentAttribute" />. Do not implement it by hand. Generic methods such as
///     <see cref="AtmosSimulation.GetComponent{T}" /> use it to find the key and create an instance without reflection.
/// </remarks>
[PublicAPI]
public interface ISimulationComponentDefinition<TSelf> : INumosStateTarget
    where TSelf : class, ISimulationComponentDefinition<TSelf>
{
    /// <summary>
    ///     Gets the key the <see cref="SimulationComponentAttribute" /> declares.
    /// </summary>
    static abstract string ComponentKey { get; }

    /// <summary>
    ///     Creates a new instance with the parameterless constructor.
    /// </summary>
    /// <returns>The new instance.</returns>
    static abstract TSelf CreateComponent();
}

/// <summary>
///     Holds every <see cref="SimulationComponentAttribute" /> class known to the process, by key.
/// </summary>
/// <remarks>
///     <para>
///         The catalog is process-wide. Every world and assembly in the process shares it. A restore or a replayed
///         operation creates a component that the receiving simulation lacks from its entry here. An assembly with
///         component classes registers them from its module initializer, which runs the first time code in the
///         assembly runs. A host that loads a file before that can call the assembly's generated
///         <c>NumosModule.EnsureRegistered()</c>.
///     </para>
///     <para>
///         Every member is thread-safe. Entries are never removed.
///     </para>
/// </remarks>
[PublicAPI]
public static class SimulationComponentCatalog
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> ByKey = new(StringComparer.Ordinal);
    private static ReadOnlyCollection<string> _keys = ReadOnlyCollection<string>.Empty;

    /// <summary>
    ///     Gets a snapshot of every registered key, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> Keys => Volatile.Read(ref _keys);

    /// <summary>
    ///     Adds <typeparamref name="T" /> under its key.
    /// </summary>
    /// <typeparam name="T">The component class.</typeparam>
    /// <remarks>
    ///     Registering a key again with the same class does nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The key is already registered by a different class. The message names both classes.
    /// </exception>
    public static void Register<T>() where T : class, ISimulationComponentDefinition<T>
    {
        string key = T.ComponentKey;
        lock (Gate)
        {
            if (ByKey.TryGetValue(key, out Entry existing))
            {
                if (existing.Type == typeof(T))
                    return;

                throw new InvalidOperationException(
                    $"Simulation component '{key}' is already registered by {existing.Type.FullName} and cannot be " +
                    $"registered again by {typeof(T).FullName}.");
            }

            ByKey.Add(key, new Entry(typeof(T), static () => T.CreateComponent()));
            string[] keys = [..ByKey.Keys];
            Array.Sort(keys, StringComparer.Ordinal);
            Volatile.Write(ref _keys, Array.AsReadOnly(keys));
        }
    }

    /// <summary>
    ///     Gets whether a class is registered under <paramref name="key" />.
    /// </summary>
    /// <param name="key">The ordinal component key.</param>
    /// <returns><see langword="true" /> when a class is registered under the key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
    public static bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (Gate)
            return ByKey.ContainsKey(key);
    }

    /// <summary>
    ///     Creates a new instance of the class registered under <paramref name="key" />.
    /// </summary>
    internal static bool TryCreate(string key, [NotNullWhen(true)] out INumosStateTarget? component)
    {
        Entry entry;
        lock (Gate)
        {
            if (!ByKey.TryGetValue(key, out entry))
            {
                component = null;
                return false;
            }
        }

        component = entry.Create();
        return true;
    }

    /// <summary>
    ///     Builds the message for a key that no loaded assembly registered.
    /// </summary>
    internal static string DescribeUnknown(string key)
    {
        return $"Simulation component '{key}' isn't registered. Load the assembly that declares it with " +
               "[SimulationComponent], or call that assembly's NumosModule.EnsureRegistered(), first.";
    }

    private readonly record struct Entry(Type Type, Func<INumosStateTarget> Create);
}
