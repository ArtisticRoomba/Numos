using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     One simulation component a simulation holds, with the baseline the recording boundary compares against.
/// </summary>
/// <param name="key">The component key.</param>
/// <param name="instance">The component.</param>
internal sealed class SimulationComponentSlot(string key, INumosStateTarget instance)
{
    /// <summary>
    ///     The Debug-only baseline of the members marked <c>[NumosSerialize(HostEditable = false)]</c>.
    /// </summary>
    public ulong[]? BulkHashes;

    /// <summary>
    ///     Whether <see cref="StateHash" /> holds a baseline. A component the host created since the last boundary has
    ///     none, so the next boundary records its whole state.
    /// </summary>
    public bool HasBaseline;

    /// <summary>
    ///     The hash of the host-editable members when Numos last saw them.
    /// </summary>
    public ulong StateHash;

    public string Key { get; } = key;

    public INumosStateTarget Instance { get; } = instance;
}

public sealed partial class AtmosSimulation
{
    // Ordinal key order is the checkpoint and hash order. The list has its own lock, because a stage's workers reach it
    // while the tick thread holds the world gate.
    [NumosIgnore]
    private readonly SortedList<string, SimulationComponentSlot> _components = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets whether the simulation holds at least one simulation component.
    /// </summary>
    internal bool HasComponents
    {
        get
        {
            lock (_components)
                return _components.Count > 0;
        }
    }

    /// <summary>
    ///     Gets this simulation's <typeparamref name="T" /> component, and creates it on first use.
    /// </summary>
    /// <typeparam name="T">A <see cref="SimulationComponentAttribute" /> class.</typeparam>
    /// <returns>The one instance of <typeparamref name="T" /> this simulation holds.</returns>
    /// <remarks>
    ///     <para>
    ///         Call it from host code between ticks. Numos records host edits to the component at the next recording
    ///         boundary, as it does for a class solver. A component the host creates is recorded with its whole state
    ///         there. In a stage, use <see cref="AtmosWorldSolverContext.Get{T}" /> instead. Work inside a tick is
    ///         derived and is not recorded.
    ///     </para>
    ///     <para>
    ///         The call waits for a tick that runs on another thread. Do not call it from a stage's worker threads,
    ///         because the tick thread holds the lock it takes.
    ///     </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Another class holds the key of <typeparamref name="T" />, in this simulation or in
    ///     <see cref="SimulationComponentCatalog" />.
    /// </exception>
    /// <example>
    ///     <code>
    ///     var pipes = simulation.GetComponent&lt;PipeNetwork&gt;();
    ///     pipes.NextId = 100; // recorded at the next tick
    ///     </code>
    /// </example>
    public T GetComponent<T>() where T : class, ISimulationComponentDefinition<T>
    {
        lock (World.Gate)
        {
            ThrowIfDisposed();
            return GetOrCreateComponent<T>();
        }
    }

    /// <summary>
    ///     Gets this simulation's <typeparamref name="T" /> component when it has one.
    /// </summary>
    /// <param name="component">The component, or <see langword="null" /> when the simulation has none.</param>
    /// <typeparam name="T">A <see cref="SimulationComponentAttribute" /> class.</typeparam>
    /// <returns><see langword="true" /> when the simulation holds the component.</returns>
    /// <remarks>
    ///     The call never creates a component. It waits for a tick that runs on another thread, as
    ///     <see cref="GetComponent{T}" /> does.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Another class holds the key of <typeparamref name="T" />.</exception>
    public bool TryGetComponent<T>([NotNullWhen(true)] out T? component) where T : class, ISimulationComponentDefinition<T>
    {
        lock (World.Gate)
        {
            ThrowIfDisposed();
            return TryGetComponentCore(out component);
        }
    }

    /// <summary>
    ///     Drops this simulation's <typeparamref name="T" /> component.
    /// </summary>
    /// <typeparam name="T">A <see cref="SimulationComponentAttribute" /> class.</typeparam>
    /// <returns><see langword="true" /> when the simulation held the component.</returns>
    /// <remarks>
    ///     Between ticks while the world records, the removal is a
    ///     <see cref="RemoveAtmosSimulationComponentOperation" />. Numos records nothing for a component the host
    ///     created since the last boundary, because the creation was not recorded yet either.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Another class holds the key of <typeparamref name="T" />.</exception>
    public bool RemoveComponent<T>() where T : class, ISimulationComponentDefinition<T>
    {
        lock (World.Gate)
        {
            ThrowIfDisposed();
            string key = T.ComponentKey;
            SimulationComponentSlot? removed;
            lock (_components)
            {
                if (!_components.TryGetValue(key, out removed))
                    return false;

                _ = Cast<T>(removed);
                _components.Remove(key);
            }

            World.RecordComponentRemoved(this, removed);
            return true;
        }
    }

    /// <summary>
    ///     Gets the keys of the simulation components this simulation holds, in ordinal order.
    /// </summary>
    /// <remarks>
    ///     The list is a copy. It waits for a tick that runs on another thread, as <see cref="GetComponent{T}" /> does.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public IReadOnlyList<string> ComponentKeys
    {
        get
        {
            lock (World.Gate)
            {
                ThrowIfDisposed();
                lock (_components)
                    return [.._components.Keys];
            }
        }
    }

    /// <summary>
    ///     Finds the component under <paramref name="key" />, with descriptors of its <c>[NumosSerialize]</c> members.
    /// </summary>
    /// <param name="key">The component key.</param>
    /// <param name="component">The component, or <see langword="null" /> when the simulation has none.</param>
    /// <param name="members">
    ///     One descriptor per marked member, in declaration order. The list is empty for a component that marks nothing,
    ///     and when the method returns false.
    /// </param>
    /// <returns><see langword="true" /> when the simulation holds a component under <paramref name="key" />.</returns>
    /// <remarks>
    ///     The call never creates a component. Write members from host code between ticks. Numos records a write at the
    ///     next recording boundary, as it does for any other host edit to the component.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public bool TryGetComponentState(
        string key, [NotNullWhen(true)] out object? component, out IReadOnlyList<AtmosStateMember> members)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (World.Gate)
        {
            ThrowIfDisposed();
            if (FindComponentSlot(key) is not { } slot)
            {
                component = null;
                members = [];
                return false;
            }

            component = slot.Instance;
            members = (slot.Instance as INumosStateDescribed)?.StateMembers ?? [];
            return true;
        }
    }

    /// <summary>
    ///     Gets or creates the component without the world gate, for host code that holds it and for stages.
    /// </summary>
    internal T GetOrCreateComponent<T>() where T : class, ISimulationComponentDefinition<T>
    {
        string key = T.ComponentKey;
        lock (_components)
        {
            if (_components.TryGetValue(key, out SimulationComponentSlot? slot))
                return Cast<T>(slot);

            // A restore or a replayed edit creates the component by key, so the catalog must know the class.
            SimulationComponentCatalog.Register<T>();
            T created = T.CreateComponent();
            _components.Add(key, new SimulationComponentSlot(key, created));
            return created;
        }
    }

    /// <summary>
    ///     Finds the component without the world gate.
    /// </summary>
    internal bool TryGetComponentCore<T>([NotNullWhen(true)] out T? component) where T : class, ISimulationComponentDefinition<T>
    {
        lock (_components)
        {
            if (_components.TryGetValue(T.ComponentKey, out SimulationComponentSlot? slot))
            {
                component = Cast<T>(slot);
                return true;
            }
        }

        component = null;
        return false;
    }

    /// <summary>
    ///     Copies the slots in ordinal key order.
    /// </summary>
    internal SimulationComponentSlot[] CopyComponentSlots()
    {
        lock (_components)
            return [.._components.Values];
    }

    /// <summary>
    ///     Finds the slot that holds <paramref name="key" />, or <see langword="null" />.
    /// </summary>
    internal SimulationComponentSlot? FindComponentSlot(string key)
    {
        lock (_components)
            return _components.GetValueOrDefault(key);
    }

    /// <summary>
    ///     Replaces every component with <paramref name="slots" />. A restore calls this once its fallible phase has
    ///     succeeded.
    /// </summary>
    internal void InstallComponents(SortedList<string, SimulationComponentSlot> slots)
    {
        lock (_components)
        {
            _components.Clear();
            foreach (var slot in slots.Values)
                _components.Add(slot.Key, slot);
        }
    }

    /// <summary>
    ///     Writes a recorded state payload into the component under <paramref name="key" />, and creates it from
    ///     <see cref="SimulationComponentCatalog" /> when the simulation lacks it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No class is registered under the key.</exception>
    /// <exception cref="InvalidDataException">The payload is malformed.</exception>
    internal void SetComponentState(string key, ReadOnlySpan<byte> payload, IGasRegistry gases)
    {
        string owner = $"simulation component '{key}'";
        SimulationComponentSlot? slot = FindComponentSlot(key);
        if (slot is not null)
        {
            StateEditBaseline.Restore(slot.Instance, payload, gases, owner);
            return;
        }

        if (!SimulationComponentCatalog.TryCreate(key, out INumosStateTarget? created))
            throw new InvalidOperationException(SimulationComponentCatalog.DescribeUnknown(key));

        // Added only once the payload reads, so a failure leaves the simulation without the component.
        StateEditBaseline.Restore(created, payload, gases, owner);
        lock (_components)
            _components.Add(key, new SimulationComponentSlot(key, created));
    }

    /// <summary>
    ///     Drops the component under <paramref name="key" /> without recording anything.
    /// </summary>
    internal bool RemoveComponentSlot(string key)
    {
        lock (_components)
            return _components.Remove(key);
    }

    private void ClearComponents()
    {
        lock (_components)
            _components.Clear();
    }

    private static T Cast<T>(SimulationComponentSlot slot) where T : class, ISimulationComponentDefinition<T>
    {
        return slot.Instance as T ?? throw new InvalidOperationException(
            $"Simulation component '{slot.Key}' is held by {slot.Instance.GetType().FullName}, not by " +
            $"{typeof(T).FullName}.");
    }
}
