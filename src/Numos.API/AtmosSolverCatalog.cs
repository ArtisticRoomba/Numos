using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Holds the <see cref="AtmosSolverDescriptor" /> of every class solver known to the process, by stage name.
/// </summary>
/// <remarks>
///     <para>
///         The catalog is process-wide. Every world and assembly in the process shares it. An assembly with
///         <see cref="AtmosSolverAttribute" /> classes registers their descriptors from its module initializer, which
///         runs the first time code in the assembly runs. A host that reads the catalog before that can call the
///         assembly's generated <c>NumosModule.EnsureRegistered()</c>.
///     </para>
///     <para>
///         Every member is thread-safe. Entries are never removed.
///     </para>
/// </remarks>
[PublicAPI]
public static class AtmosSolverCatalog
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, AtmosSolverDescriptor> ByName = new(StringComparer.Ordinal);
    private static ReadOnlyCollection<string> _names = ReadOnlyCollection<string>.Empty;

    /// <summary>
    ///     Gets a snapshot of every registered stage name, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> Names => Volatile.Read(ref _names);

    /// <summary>
    ///     Adds a descriptor under its <see cref="AtmosSolverDescriptor.Name" />.
    /// </summary>
    /// <param name="descriptor">The descriptor to add.</param>
    /// <remarks>
    ///     Registering a name again with the same <see cref="AtmosSolverDescriptor.SolverType" /> does nothing, and the
    ///     first descriptor stays.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The name is already registered with a different solver class. The message names both classes.
    /// </exception>
    public static void Register(AtmosSolverDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (Gate)
        {
            if (ByName.TryGetValue(descriptor.Name, out AtmosSolverDescriptor? existing))
            {
                if (existing.SolverType == descriptor.SolverType)
                    return;

                throw new InvalidOperationException(
                    $"Solver '{descriptor.Name}' is already registered by {existing.SolverType.FullName} and cannot " +
                    $"be registered again by {descriptor.SolverType.FullName}.");
            }

            ByName.Add(descriptor.Name, descriptor);
            string[] names = [..ByName.Keys];
            Array.Sort(names, StringComparer.Ordinal);
            Volatile.Write(ref _names, Array.AsReadOnly(names));
        }
    }

    /// <summary>
    ///     Finds the descriptor registered under <paramref name="name" />.
    /// </summary>
    /// <param name="name">The ordinal stage name.</param>
    /// <param name="descriptor">The descriptor, or <see langword="null" /> when none is registered.</param>
    /// <returns><see langword="true" /> when a descriptor is registered under <paramref name="name" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    public static bool TryGet(string name, [NotNullWhen(true)] out AtmosSolverDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (Gate)
            return ByName.TryGetValue(name, out descriptor);
    }
}
