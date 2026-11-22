using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Receives one event that a stage emitted with <see cref="AtmosWorldSolverContext.Emit{T}" />.
/// </summary>
/// <typeparam name="T">The event type.</typeparam>
/// <param name="value">The event. It is valid only for the duration of the call.</param>
public delegate void AtmosEventHandler<T>(in T value) where T : struct;

/// <summary>
///     Receives one event that a stage emitted with <see cref="AtmosWorldSolverContext.Emit{T}" />, together with the
///     tick that emitted it.
/// </summary>
/// <typeparam name="T">The event type.</typeparam>
/// <param name="value">The event. It is valid only for the duration of the call.</param>
/// <param name="info">The tick that emitted the event and whether that tick was replayed.</param>
public delegate void AtmosEventInfoHandler<T>(in T value, AtmosEventInfo info) where T : struct;

/// <summary>
///     Selects which ticks deliver events to a subscription.
/// </summary>
public enum AtmosEventDelivery : byte
{
    /// <summary>
    ///     Delivers events of live ticks only. Ticks that a replay or a timeline seek runs deliver nothing.
    /// </summary>
    LiveOnly,

    /// <summary>
    ///     Delivers events of live ticks and of replayed ticks. <see cref="AtmosEventInfo.IsReplay" /> tells them apart.
    /// </summary>
    Always
}

/// <summary>
///     Describes the tick that emitted an event.
/// </summary>
/// <param name="Tick">The one-based tick that emitted the event. It equals <see cref="AtmosWorld.TickCount" /> during delivery.</param>
/// <param name="IsReplay">
///     <see langword="true" /> when the tick ran inside a replay, such as <c>ReplayTo</c> or a timeline seek.
/// </param>
public readonly record struct AtmosEventInfo(long Tick, bool IsReplay);

/// <summary>
///     Carries events from stages to host code, such as a fire that started or a tile that should break.
/// </summary>
/// <remarks>
///     <para>
///         A stage calls <see cref="AtmosWorldSolverContext.Emit{T}" /> on the tick thread. Numos buffers the event and
///         delivers it after the tick completes and its simulation locks are released. Delivery runs on the tick thread,
///         in emit order across all event types, before the next tick starts. <see cref="AtmosWorld.Update" /> delivers
///         the events of each of its ticks before it runs the next one.
///     </para>
///     <para>
///         A handler can call world and simulation mutators. A live recording records them as ordinary operations,
///         after the tick that emitted the event. A handler cannot tick, update, replay, or dispose the world. Those
///         calls throw <see cref="InvalidOperationException" />.
///     </para>
///     <para>
///         During a replay, events go only to <see cref="AtmosEventDelivery.Always" /> subscriptions, with
///         <see cref="AtmosEventInfo.IsReplay" /> set. Such a handler must not change the world while
///         <see cref="AtmosEventInfo.IsReplay" /> is set. Numos does not record that change, so the replay diverges.
///     </para>
///     <para>
///         Events are not state. Numos does not checkpoint, hash, or record them. A checkpoint restore discards the
///         undelivered events of the current tick. That includes a restore that a handler starts.
///     </para>
///     <para>
///         If a handler throws, the exception leaves <see cref="AtmosWorld.Tick" /> or <see cref="AtmosWorld.Update" />.
///         The tick that emitted the event is already complete, and Numos drops the remaining events of that tick. If a
///         stage throws, Numos drops every event that the tick emitted. A handler that throws during a replay fails the
///         replay, and Numos restores the state from before the replay call.
///     </para>
///     <para>
///         <see cref="Subscribe{T}(AtmosEventHandler{T}, AtmosEventDelivery)" /> and the returned subscription's
///         <see cref="IDisposable.Dispose" /> are safe from any thread at any time, also while recording or replaying
///         and from inside a handler. They change no simulation state. A change made during delivery applies from the
///         next event Numos delivers.
///     </para>
///     <para>
///         An emit of a type that no subscription would receive costs one dictionary lookup and allocates nothing.
///         Numos drops it at the emit. A subscription therefore only receives events that a stage emitted after the
///         subscription existed.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     public readonly record struct FireSpread(AtmosCellRef Cell, float Temperature);
///
///     // In a stage, on the tick thread:
///     context.Emit(new FireSpread(cell, temperature));
///
///     // Host:
///     IDisposable sound = world.Events.Subscribe&lt;FireSpread&gt;(static (in FireSpread e) => PlaySound(e.Cell));
///     world.Events.Subscribe&lt;FireSpread&gt;(
///         (in FireSpread e, AtmosEventInfo info) => log.Write(info.Tick, e.Cell),
///         AtmosEventDelivery.Always);
///     </code>
/// </example>
public sealed class AtmosWorldEvents
{
    private readonly AtmosWorld _world;

    // Guards subscription changes. Emit and delivery read the published tables without it.
    private readonly object _subscriptionGate = new();

    // Copy-on-write, like the services table, because the tick thread reads it while another thread subscribes.
    private Dictionary<Type, Channel> _channels = [];

    // One entry per buffered event, in emit order. Numos touches the buffer only under AtmosWorld.Gate.
    private readonly List<Channel> _pending = [];

    internal AtmosWorldEvents(AtmosWorld world)
    {
        _world = world;
    }

    /// <summary>
    ///     Gets whether a handler of this world is running. Numos reads it under <see cref="AtmosWorld.Gate" />.
    /// </summary>
    internal bool IsDelivering { get; private set; }

    /// <summary>
    ///     Gets whether a tick emitted events that are not delivered yet.
    /// </summary>
    internal bool HasPending => _pending.Count > 0;

    /// <summary>
    ///     Subscribes a handler to every event of type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The event type. Numos matches the exact type.</typeparam>
    /// <param name="handler">The handler that Numos calls on the tick thread after each tick.</param>
    /// <param name="delivery">Whether replayed ticks also deliver to <paramref name="handler" />.</param>
    /// <returns>A subscription. Dispose it to stop delivery. A second dispose does nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delivery" /> is not a defined value.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IDisposable Subscribe<T>(AtmosEventHandler<T> handler, AtmosEventDelivery delivery = AtmosEventDelivery.LiveOnly)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateDelivery(delivery);
        return SubscribeCore(new Subscription<T>(handler, null, delivery));
    }

    /// <summary>
    ///     Subscribes a handler that also receives the emitting tick to every event of type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The event type. Numos matches the exact type.</typeparam>
    /// <param name="handler">The handler that Numos calls on the tick thread after each tick.</param>
    /// <param name="delivery">Whether replayed ticks also deliver to <paramref name="handler" />.</param>
    /// <returns>A subscription. Dispose it to stop delivery. A second dispose does nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delivery" /> is not a defined value.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IDisposable Subscribe<T>(AtmosEventInfoHandler<T> handler, AtmosEventDelivery delivery = AtmosEventDelivery.LiveOnly)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateDelivery(delivery);
        return SubscribeCore(new Subscription<T>(null, handler, delivery));
    }

    /// <summary>
    ///     Buffers one event for delivery after the tick, or drops it when no subscription would receive it.
    /// </summary>
    /// <remarks>The caller is a stage on the tick thread, which holds <see cref="AtmosWorld.Gate" />.</remarks>
    internal void Emit<T>(in T value) where T : struct
    {
        if (!Volatile.Read(ref _channels).TryGetValue(typeof(T), out Channel? channel))
            return;

        var typed = (Channel<T>)channel;
        if (!typed.Accepts(_world.IsReplayingWorld))
            return;

        typed.Add(in value);
        _pending.Add(typed);
    }

    /// <summary>
    ///     Delivers the buffered events of the tick that just completed, then empties the buffer.
    /// </summary>
    /// <remarks>
    ///     The caller holds <see cref="AtmosWorld.Gate" /> outside a tick. A handler that restores a checkpoint empties
    ///     the buffer, which ends the loop. The buffer is empty when this returns, also when a handler throws.
    /// </remarks>
    internal void Deliver(long tick, bool isReplay)
    {
        if (_pending.Count == 0)
            return;

        var info = new AtmosEventInfo(tick, isReplay);
        IsDelivering = true;
        try
        {
            for (int index = 0; index < _pending.Count; index++)
                _pending[index].DeliverNext(info);
        }
        finally
        {
            IsDelivering = false;
            ClearPending();
        }
    }

    /// <summary>
    ///     Drops every buffered event. The caller holds <see cref="AtmosWorld.Gate" />.
    /// </summary>
    internal void ClearPending()
    {
        foreach (var channel in _pending)
            channel.Clear();

        _pending.Clear();
    }

    private IDisposable SubscribeCore<T>(Subscription<T> subscription) where T : struct
    {
        ObjectDisposedException.ThrowIf(_world.IsDisposed, _world);
        lock (_subscriptionGate)
        {
            Channel<T> channel;
            if (_channels.TryGetValue(typeof(T), out Channel? existing))
            {
                channel = (Channel<T>)existing;
            }
            else
            {
                channel = new Channel<T>();
                Volatile.Write(ref _channels, new Dictionary<Type, Channel>(_channels) { [typeof(T)] = channel });
            }

            subscription.Attach(this, channel);
            channel.Publish([.. channel.Subscriptions, subscription]);
        }

        return subscription;
    }

    private static void ValidateDelivery(AtmosEventDelivery delivery)
    {
        if (!Enum.IsDefined(delivery))
            throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "Unknown event delivery.");
    }

    private void Unsubscribe<T>(Channel<T> channel, Subscription<T> subscription) where T : struct
    {
        lock (_subscriptionGate)
        {
            Subscription<T>[] current = channel.Subscriptions;
            int index = Array.IndexOf(current, subscription);
            if (index < 0)
                return;

            var next = new Subscription<T>[current.Length - 1];
            current.AsSpan(0, index).CopyTo(next);
            current.AsSpan(index + 1).CopyTo(next.AsSpan(index));
            channel.Publish(next);
        }
    }

    /// <summary>
    ///     The subscriptions and the buffered events of one event type.
    /// </summary>
    private abstract class Channel
    {
        /// <summary>
        ///     Delivers the oldest event not yet delivered in this tick.
        /// </summary>
        internal abstract void DeliverNext(AtmosEventInfo info);

        internal abstract void Clear();
    }

    private sealed class Channel<T> : Channel where T : struct
    {
        private T[] _items = [];
        private int _count;
        private int _next;
        private volatile Subscription<T>[] _subscriptions = [];
        private volatile bool _hasAlways;

        internal Subscription<T>[] Subscriptions => _subscriptions;

        /// <summary>
        ///     Replaces the subscription list. The caller holds the subscription gate.
        /// </summary>
        internal void Publish(Subscription<T>[] subscriptions)
        {
            bool hasAlways = false;
            foreach (var subscription in subscriptions)
                hasAlways |= subscription.Delivery == AtmosEventDelivery.Always;

            // An emit that reads the new list with the old flag only buffers or drops one event it should not have.
            // Delivery checks each subscription again, so neither case reaches a handler that should not see it.
            _hasAlways = hasAlways;
            _subscriptions = subscriptions;
        }

        internal bool Accepts(bool isReplay)
        {
            return isReplay ? _hasAlways : _subscriptions.Length > 0;
        }

        internal void Add(in T value)
        {
            if (_count == _items.Length)
                Array.Resize(ref _items, Math.Max(4, _items.Length * 2));

            _items[_count++] = value;
        }

        internal override void DeliverNext(AtmosEventInfo info)
        {
            // A copy, because a handler that restores a checkpoint clears the buffer under the remaining handlers.
            T value = _items[_next++];
            foreach (var subscription in _subscriptions)
                subscription.Invoke(in value, info);
        }

        internal override void Clear()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_items, 0, _count);

            _count = 0;
            _next = 0;
        }
    }

    private sealed class Subscription<T>(
        AtmosEventHandler<T>? handler,
        AtmosEventInfoHandler<T>? infoHandler,
        AtmosEventDelivery delivery) : IDisposable where T : struct
    {
        private AtmosWorldEvents? _owner;
        private Channel<T>? _channel;
        private volatile bool _disposed;

        internal AtmosEventDelivery Delivery { get; } = delivery;

        internal void Attach(AtmosWorldEvents owner, Channel<T> channel)
        {
            _owner = owner;
            _channel = channel;
        }

        internal void Invoke(in T value, AtmosEventInfo info)
        {
            // The disposed check makes a dispose from an earlier handler of the same event take effect at once.
            if (_disposed || (info.IsReplay && Delivery == AtmosEventDelivery.LiveOnly))
                return;

            if (handler != null)
                handler(in value);
            else
                infoHandler!(in value, info);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner!.Unsubscribe(_channel!, this);
        }
    }
}
