using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.Replay;

/// <summary>
///     The <see cref="NumosSerializeAttribute.Codec" /> for a world's <see cref="AtmosConfigSnapshot" />: its schema
///     fields (scalars and the gas registry), then its solver configurations as a count and, for each one in the
///     snapshot's ordinal key order, its key and payload.
/// </summary>
/// <remarks>
///     <para>
///         The solver configurations are a keyed section rather than a typed field because their types are only known
///         at runtime. Reading looks each key up in <see cref="StateReader.SolverConfigurations" />, or in the built-in
///         registry when the reader has none.
///     </para>
///     <para>
///         A hand-written configuration (one without <see cref="NumosSerializeAttribute" /> members) has no wire format,
///         so writing one to anything but a <see cref="StateHashSink" /> throws. The hash takes its own
///         <see cref="IAtmosSolverConfiguration.ComputeStateHash" /> instead, which keeps a world with one hashable.
///     </para>
/// </remarks>
internal readonly struct AtmosConfigSnapshotCodec : IStateCodec<AtmosConfigSnapshot>
{
    /// <exception cref="NotSupportedException">A solver configuration has no wire format and the sink isn't a hash.</exception>
    public static void Write<TSink>(ref TSink sink, AtmosConfigSnapshot value) where TSink : struct, IStateSink
    {
        NumosState.Write(ref sink, value.Fields);
        sink.WriteCount(value.SolverConfigurations.Count);
        foreach (var configuration in value.SolverConfigurations)
        {
            sink.WriteString(configuration.Key);
            if (configuration is INumosState state)
            {
                state.WriteState(ref sink);
            }
            else if (typeof(TSink) == typeof(StateHashSink))
            {
                sink.WriteUInt64(configuration.ComputeStateHash());
            }
            else
            {
                throw new NotSupportedException(
                    $"Solver configuration '{configuration.Key}' ({configuration.GetType()}) has no [NumosSerialize] members, so it can't be saved.");
            }
        }
    }

    public static bool StateEquals(AtmosConfigSnapshot left, AtmosConfigSnapshot right) =>
        ReferenceEquals(left, right) || left.SemanticallyEquals(right);

    /// <remarks>
    ///     Orders by state hash. That's only canonical while hashes don't collide, which is fine because nothing sorts
    ///     configurations; this exists to complete the codec.
    /// </remarks>
    public static int Compare(AtmosConfigSnapshot left, AtmosConfigSnapshot right)
    {
        if (StateEquals(left, right))
            return 0;

        var first = new StateHashSink();
        Write(ref first, left);
        var second = new StateHashSink();
        Write(ref second, right);
        int result = first.Value.CompareTo(second.Value);
        return result != 0 ? result : 1;
    }

    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    /// <exception cref="NotSupportedException">A solver configuration key has no registered type.</exception>
    public static AtmosConfigSnapshot Read(ref StateReader reader, IGasRegistry? gases)
    {
        // Read into a mutable AtmosConfig so the snapshot constructor normalizes it, exactly as for a live config.
        var config = NumosState.Read<AtmosConfig>(ref reader);
        var types = reader.SolverConfigurations ?? SolverConfigurationTypes.BuiltIn;
        int count = reader.ReadCount();
        string? previousKey = null;
        for (int index = 0; index < count; index++)
        {
            string key = reader.ReadString();
            if (previousKey != null && string.CompareOrdinal(previousKey, key) >= 0)
                throw new InvalidDataException("Solver configurations are not in ascending ordinal key order.");

            previousKey = key;
            if (!types.TryRead(key, ref reader, config.GasRegistry, out var configuration))
            {
                throw new NotSupportedException(
                    $"The file contains solver configuration '{key}', but no type is registered for that key. " +
                    "Register it in NumosReplayReadOptions.SolverConfigurations before loading.");
            }

            if (configuration!.Key != key)
            {
                throw new InvalidDataException(
                    $"Solver configuration saved as '{key}' loaded as {configuration.GetType()} with key '{configuration.Key}'.");
            }

            config.SolverConfigurations.Add(configuration);
        }

        return config.CreateSnapshot();
    }

    public static AtmosConfigSnapshot Default => new AtmosConfig().CreateSnapshot();
}
