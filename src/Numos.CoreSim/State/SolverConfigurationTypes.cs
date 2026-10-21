using Numos.CoreSim.GasReactions;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.State;

/// <summary>
///     Maps solver configuration keys to the <see cref="NumosStateAttribute" /> types that read them, so loading a
///     world archive can turn each saved configuration back into an object.
/// </summary>
/// <remarks>
///     <para>
///         Saving needs no registration: any solver configuration marked <see cref="NumosStateAttribute" /> is written
///         under its <see cref="IAtmosSolverConfiguration.Key" />. Loading does, because a key on disk is all there is
///         to go on. Register each custom configuration type before loading files that might contain it.
///     </para>
///     <para>
///         A key with no registered type fails the load rather than being skipped. Dropping a configuration would
///         change what the solvers do, and replay would quietly diverge from the recording.
///     </para>
///     <para>
///         A new instance already knows Numos's built-in configurations, such as <see cref="GasReactionConfig" />.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     var options = new NumosReplayReadOptions();
///     options.SolverConfigurations.Register&lt;FireSettings&gt;();
///     NumosWorldReplayDocument document = NumosReplayFile.Load(path, options);
///     </code>
/// </example>
public sealed class SolverConfigurationTypes
{
    // Only read from, never handed out, so it can stand in for "no host registrations" without anyone mutating it.
    internal static readonly SolverConfigurationTypes BuiltIn = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    ///     Creates a registry holding the built-in solver configurations.
    /// </summary>
    public SolverConfigurationTypes()
    {
        Register<GasReactionConfig>();
    }

    /// <summary>
    ///     Gets the registered keys.
    /// </summary>
    public IReadOnlyCollection<string> Keys => _entries.Keys;

    /// <summary>
    ///     Registers <typeparamref name="T" /> under its <see cref="NumosStateAttribute.Key" />.
    /// </summary>
    /// <typeparam name="T">A <see cref="NumosStateAttribute" /> solver configuration with a declared key.</typeparam>
    /// <returns>This registry, for chaining.</returns>
    /// <exception cref="ArgumentException">
    ///     <typeparamref name="T" /> declares no key, or the key is already registered to a different type.
    /// </exception>
    public SolverConfigurationTypes Register<T>() where T : class, IAtmosSolverConfiguration, INumosState<T>
    {
        string? key = T.StateKey;
        if (key is null)
        {
            throw new ArgumentException(
                $"{typeof(T)} declares no [NumosState(Key = ...)]. Register it under each key it uses with Register<T>(key).");
        }

        return Register<T>(key);
    }

    /// <summary>
    ///     Registers <typeparamref name="T" /> under <paramref name="key" />, for a configuration type that's used under
    ///     more than one key.
    /// </summary>
    /// <param name="key">The <see cref="IAtmosSolverConfiguration.Key" /> instances saved under it carry.</param>
    /// <typeparam name="T">A <see cref="NumosStateAttribute" /> solver configuration.</typeparam>
    /// <returns>This registry, for chaining.</returns>
    /// <remarks>
    ///     Registering the same type under the same key again does nothing. A loaded configuration whose own key
    ///     disagrees with the key it was saved under fails the load.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="key" /> is blank, or already registered to a different type.
    /// </exception>
    public SolverConfigurationTypes Register<T>(string key) where T : class, IAtmosSolverConfiguration, INumosState<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_entries.TryGetValue(key, out Entry? existing))
        {
            if (existing.Type == typeof(T))
                return this;

            throw new ArgumentException(
                $"Solver configuration key '{key}' is already registered to {existing.Type}, not {typeof(T)}.",
                nameof(key));
        }

        _entries.Add(key, new Entry(typeof(T), static (ref StateReader reader, IGasRegistry gases) => T.ReadState(ref reader, gases)));
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
        return _entries.ContainsKey(key);
    }

    internal bool TryRead(string key, ref StateReader reader, IGasRegistry gases, out IAtmosSolverConfiguration? configuration)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            configuration = null;
            return false;
        }

        configuration = entry.Read(ref reader, gases);
        return true;
    }

    private delegate IAtmosSolverConfiguration ReadConfiguration(ref StateReader reader, IGasRegistry gases);

    private sealed record Entry(Type Type, ReadConfiguration Read);
}
