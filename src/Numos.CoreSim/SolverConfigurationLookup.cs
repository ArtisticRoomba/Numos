using System.Diagnostics.CodeAnalysis;

namespace Numos.CoreSim;

/// <summary>
///     Typed lookup over captured <see cref="IAtmosSolverConfiguration" /> snapshots.
/// </summary>
internal static class SolverConfigurationLookup
{
    internal static bool TryGet<T>(
        IReadOnlyList<IAtmosSolverConfiguration> configurations, [NotNullWhen(true)] out T? configuration)
        where T : class, IAtmosSolverConfiguration
    {
        configuration = null;
        for (int i = 0; i < configurations.Count; i++)
        {
            if (configurations[i] is not T match)
                continue;

            if (configuration != null)
            {
                throw new InvalidOperationException(
                    $"More than one solver configuration is a {typeof(T)} ('{configuration.Key}' and " +
                    $"'{match.Key}'). Look it up by key instead.");
            }

            configuration = match;
        }

        return configuration != null;
    }

    internal static bool TryGet<T>(
        IReadOnlyList<IAtmosSolverConfiguration> configurations, string key, [NotNullWhen(true)] out T? configuration)
        where T : class, IAtmosSolverConfiguration
    {
        ArgumentNullException.ThrowIfNull(key);
        for (int i = 0; i < configurations.Count; i++)
        {
            var candidate = configurations[i];
            if (!StringComparer.Ordinal.Equals(candidate.Key, key))
                continue;

            configuration = candidate as T ?? throw new InvalidOperationException(
                $"Solver configuration '{key}' is a {candidate.GetType()}, not a {typeof(T)}.");

            return true;
        }

        configuration = null;
        return false;
    }
}
