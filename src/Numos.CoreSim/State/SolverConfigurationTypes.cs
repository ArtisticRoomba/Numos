using Numos.CoreSim.GasReactions;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.State;

/// <summary>
///     Maps solver configuration keys to the types that read them, so loading a world archive can turn each saved
///     configuration back into an object.
/// </summary>
/// <remarks>
///     <para>
///         Usually nothing needs registering. A module initializer that the generator adds to the assembly registers
///         every solver configuration with <see cref="NumosSerializeAttribute" /> members. The configuration is
///         therefore known as soon as that assembly is in use. That covers Numos's own configurations, such as
///         <see cref="GasReactionConfig" />, and downstream ones alike. <see cref="Register{T}" /> is for types whose
///         assembly may not have run yet when a file loads, or that the generator could not name (private nested
///         types).
///     </para>
///     <para>
///         A key with no registered type fails the load rather than being skipped. Dropping a configuration would
///         change what the solvers do, and replay would quietly diverge from the recording.
///     </para>
/// </remarks>
public sealed class SolverConfigurationTypes
{
    // Only read from, never handed out, so it can stand in for "no host registrations" without anyone mutating it.
    internal static readonly SolverConfigurationTypes BuiltIn = new();

    private static readonly object DefaultsLock = new();
    private static readonly Dictionary<string, Entry> Defaults = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the registered keys, including every automatically registered configuration.
    /// </summary>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            var keys = new SortedSet<string>(_entries.Keys, StringComparer.Ordinal);
            lock (DefaultsLock)
                keys.UnionWith(Defaults.Keys);

            return keys;
        }
    }

    /// <summary>
    ///     Registers <typeparamref name="T" /> with every registry, under its key.
    /// </summary>
    /// <typeparam name="T">A solver configuration with <see cref="NumosSerializeAttribute" /> members.</typeparam>
    /// <remarks>
    ///     Generated module initializers call this. Hosts rarely need to. A second type that claims a key already taken
    ///     does not throw here, because that would fail the load of the whole assembly. Loading a configuration under
    ///     that key does throw, until a registry picks one with <see cref="Register{T}" />.
    /// </remarks>
    public static void RegisterDefault<T>() where T : IAtmosSolverConfiguration, INumosState<T>
    {
        Entry entry = CreateEntry<T>();
        lock (DefaultsLock)
        {
            if (!Defaults.TryGetValue(entry.Key, out Entry? existing))
                Defaults.Add(entry.Key, entry);
            else if (existing.Type != typeof(T))
                Defaults[entry.Key] = existing with { Conflict = typeof(T) };
        }
    }

    /// <summary>
    ///     Registers <typeparamref name="T" /> with this registry under its key.
    /// </summary>
    /// <typeparam name="T">A solver configuration with <see cref="NumosSerializeAttribute" /> members.</typeparam>
    /// <returns>This registry, for chaining.</returns>
    /// <remarks>
    ///     Registering the same type again does nothing. When two assemblies register different types under one key,
    ///     registering one of them here settles which this registry loads.
    /// </remarks>
    /// <exception cref="ArgumentException">The key is already registered to a different type.</exception>
    public SolverConfigurationTypes Register<T>() where T : IAtmosSolverConfiguration, INumosState<T>
    {
        Entry entry = CreateEntry<T>();
        Entry? existing = _entries.GetValueOrDefault(entry.Key);
        if (existing is null)
        {
            lock (DefaultsLock)
            {
                if (Defaults.TryGetValue(entry.Key, out Entry? fallback) && fallback.Conflict is null)
                    existing = fallback;
            }
        }

        if (existing is not null && existing.Type != typeof(T))
        {
            throw new ArgumentException(
                $"Solver configuration key '{entry.Key}' is already registered to {existing.Type}, not {typeof(T)}.");
        }

        _entries[entry.Key] = entry;
        return this;
    }

    /// <summary>
    ///     Gets whether a type is registered under <paramref name="key" />.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <returns><see langword="true" /> when a load can read configurations saved under <paramref name="key" />.</returns>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_entries.ContainsKey(key))
            return true;

        lock (DefaultsLock)
            return Defaults.TryGetValue(key, out Entry? entry) && entry.Conflict is null;
    }

    internal bool TryRead(string key, ref StateReader reader, IGasRegistry gases, out IAtmosSolverConfiguration? configuration)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            lock (DefaultsLock)
                Defaults.TryGetValue(key, out entry);
        }

        if (entry is null)
        {
            configuration = null;
            return false;
        }

        if (entry.Conflict is { } conflict)
        {
            throw new InvalidOperationException(
                $"Solver configuration key '{key}' is claimed by both {entry.Type} and {conflict}. Register the one " +
                "to load with NumosReplayReadOptions.SolverConfigurations.Register<T>().");
        }

        configuration = entry.Read(ref reader, gases);
        return true;
    }

    private static Entry CreateEntry<T>() where T : IAtmosSolverConfiguration, INumosState<T>
    {
        string key = T.StateKey ?? throw new InvalidOperationException($"{typeof(T)} has no constant Key.");
        return new Entry(key, typeof(T), static (ref StateReader reader, IGasRegistry gases) => T.ReadState(ref reader, gases));
    }

    private delegate IAtmosSolverConfiguration ReadConfiguration(ref StateReader reader, IGasRegistry gases);

    private sealed record Entry(string Key, Type Type, ReadConfiguration Read, Type? Conflict = null);
}
