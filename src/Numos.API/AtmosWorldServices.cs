using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Holds host-owned objects that solvers look up by type, such as a game clock, an event bus, or a shared lookup
///     table.
/// </summary>
/// <remarks>
///     <para>
///         Services are keyed by the exact type argument given to <see cref="Register{T}" />. Registering a
///         <c>FireBus</c> as <c>IFireEvents</c> resolves through <c>Get&lt;IFireEvents&gt;()</c> only, not through
///         <c>Get&lt;FireBus&gt;()</c>. There is no assignability search, so lookups are a single dictionary hit and
///         need no reflection.
///     </para>
///     <para>
///         The world never checkpoints, clears, or disposes a service. Restoring a checkpoint leaves every
///         registration in place, and the host disposes its own objects after it disposes the world. Services are
///         outside replay, so a solver must not let a service change deterministic state unless the service itself
///         is deterministic.
///     </para>
///     <para>
///         Lookups never lock: each change publishes a new immutable table, and <see cref="Get{T}" /> and
///         <see cref="TryGet{T}" /> read whichever table is current. That makes them safe from any thread, including
///         <c>Parallel.For</c> workers inside a solver callback. Changes take <see cref="AtmosWorld" />'s lock and are
///         rejected during a tick and while the world is recording or replaying, because a solver that resolves a
///         different service halfway through a recording would not replay the same way.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     world.Services.Register&lt;IFireEvents&gt;(fireBus);
///
///     world.Solvers.Register("my-mod/fire", context =>
///     {
///         var events = context.World.Services.Get&lt;IFireEvents&gt;();
///         events.Publish(context.TickCount);
///     });
///     </code>
/// </example>
public sealed class AtmosWorldServices
{
    private readonly AtmosWorld _world;
    private Dictionary<Type, object> _services = [];

    internal AtmosWorldServices(AtmosWorld world)
    {
        _world = world;
    }

    /// <summary>
    ///     Registers a service under the exact type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">
    ///     The lookup key. Name it explicitly when registering through an interface or base class, since an inferred
    ///     argument keys the instance by its concrete type.
    /// </typeparam>
    /// <param name="instance">The caller-owned object to return from lookups.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A service is already registered under <typeparamref name="T" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     A tick is running, or the world is recording or replaying.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     world.Services.Register&lt;IFireEvents&gt;(fireBus); // resolves as IFireEvents only
    ///     world.Services.Register(fireBus);                 // resolves as FireBus only
    ///     </code>
    /// </example>
    [PublicAPI]
    public void Register<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        ThrowIfTickExecuting();
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            ThrowIfTickExecuting();
            _world.EnsureCanChangeWorldServices();
            if (_services.ContainsKey(typeof(T)))
            {
                throw new ArgumentException(
                    $"A world service of type '{typeof(T)}' is already registered. Unregister it first to replace it.",
                    nameof(instance));
            }

            Publish(new Dictionary<Type, object>(_services) { [typeof(T)] = instance });
        }
    }

    /// <summary>
    ///     Removes the service registered under the exact type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The type the service was registered under.</typeparam>
    /// <returns><see langword="true" /> when a service was removed.</returns>
    /// <remarks>
    ///     The removed object is not disposed. A worker thread that looked it up before the removal may still be
    ///     holding it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     A tick is running, or the world is recording or replaying.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool Unregister<T>() where T : class
    {
        ThrowIfTickExecuting();
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            ThrowIfTickExecuting();
            _world.EnsureCanChangeWorldServices();
            if (!_services.ContainsKey(typeof(T)))
                return false;

            var next = new Dictionary<Type, object>(_services);
            next.Remove(typeof(T));
            Publish(next);
            return true;
        }
    }

    /// <summary>
    ///     Looks up the service registered under the exact type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The type the service was registered under.</typeparam>
    /// <param name="service">The registered service when found; otherwise <see langword="null" />.</param>
    /// <returns><see langword="true" /> when a service is registered under <typeparamref name="T" />.</returns>
    /// <remarks>
    ///     Lock-free and safe from any thread, including solver worker threads during a tick.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class
    {
        ObjectDisposedException.ThrowIf(_world.IsDisposed, _world);
        if (Volatile.Read(ref _services).TryGetValue(typeof(T), out object? value))
        {
            service = (T)value;
            return true;
        }

        service = null;
        return false;
    }

    /// <summary>
    ///     Gets the service registered under the exact type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The type the service was registered under.</typeparam>
    /// <returns>The registered service.</returns>
    /// <remarks>
    ///     Lock-free and safe from any thread, including solver worker threads during a tick.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No service is registered under <typeparamref name="T" />.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public T Get<T>() where T : class
    {
        if (TryGet(out T? service))
            return service;

        throw new InvalidOperationException(
            $"No world service of type '{typeof(T)}' is registered. Register it with " +
            $"AtmosWorld.Services.Register<{typeof(T).Name}>(...) before the first tick that needs it.");
    }

    private void Publish(Dictionary<Type, object> next)
    {
        // Readers never lock, so the table is replaced rather than mutated: a reader sees the old table or the new
        // one, never a dictionary halfway through a resize.
        Volatile.Write(ref _services, next);
        _world.InvalidateSolverDependencies();
    }

    private void ThrowIfTickExecuting()
    {
        // Checked before taking the gate as well as after: a solver's worker thread would otherwise block on a lock
        // the tick holds until those same workers finish.
        if (_world.IsTickExecuting)
            throw new InvalidOperationException("World services cannot be registered or removed while a tick is running.");
    }
}