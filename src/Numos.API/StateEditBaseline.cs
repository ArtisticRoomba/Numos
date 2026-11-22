using System.Diagnostics;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     Hashes state restored in place (class solvers and simulation components) for the recording boundary, and
///     captures it as a payload.
/// </summary>
/// <remarks>
///     The boundary hash covers the host-editable members only. Members marked
///     <c>[NumosSerialize(HostEditable = false)]</c> stay in the payload, the checkpoint, and the world state hash. A
///     Debug build of Numos hashes them separately and throws when one changed between ticks.
/// </remarks>
internal static class StateEditBaseline
{
    /// <summary>
    ///     Hashes the members the boundary compares, without buffering them.
    /// </summary>
    public static ulong HashHostEditable(INumosStateTarget state)
    {
        var sink = new StateHashSink();
        if (state is INumosBulkStateTarget bulk)
            bulk.WriteHostEditableState(ref sink);
        else
            state.WriteState(ref sink);

        return sink.Value;
    }

    /// <summary>
    ///     Writes the full state into a fresh array.
    /// </summary>
    public static byte[] Capture(INumosStateTarget state, ref StateBinarySink sink)
    {
        sink.Reset();
        state.WriteState(ref sink);
        return sink.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Reads a payload from <see cref="Capture" /> into <paramref name="state" />.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload is malformed or has trailing data.</exception>
    public static void Restore(INumosStateTarget state, ReadOnlySpan<byte> payload, Numos.CoreSim.IGasRegistry gases, string owner)
    {
        var reader = new StateReader(payload);
        state.ReadStateInto(ref reader, gases);
        if (!reader.IsAtEnd)
            throw new InvalidDataException($"The state for {owner} has trailing data.");
    }

    /// <summary>
    ///     Takes the hashes of the members that are not host-editable as the baseline <see cref="CheckBulk" /> compares
    ///     against. Only Debug builds of Numos call it.
    /// </summary>
    [Conditional("DEBUG")]
    public static void RefreshBulk(INumosStateTarget state, ref ulong[]? hashes)
    {
        if (state is not INumosBulkStateTarget bulk)
            return;

        hashes ??= new ulong[bulk.BulkMemberNames.Count];
        bulk.HashBulkMembers(hashes);
    }

    /// <summary>
    ///     Throws when a member that is not host-editable changed since <see cref="RefreshBulk" />. Only Debug builds of
    ///     Numos call it.
    /// </summary>
    /// <param name="state">The state to check.</param>
    /// <param name="hashes">The baseline, or <see langword="null" /> when there is none yet.</param>
    /// <param name="owner">Names the solver or component in the message.</param>
    /// <exception cref="InvalidOperationException">A member changed. The message names it.</exception>
    [Conditional("DEBUG")]
    public static void CheckBulk(INumosStateTarget state, ulong[]? hashes, string owner)
    {
        if (hashes is null || state is not INumosBulkStateTarget bulk)
            return;

        Span<ulong> current = hashes.Length <= 16 ? stackalloc ulong[hashes.Length] : new ulong[hashes.Length];
        bulk.HashBulkMembers(current);
        for (int index = 0; index < hashes.Length; index++)
        {
            if (current[index] == hashes[index])
                continue;

            throw new InvalidOperationException(
                $"'{state.GetType().Name}.{bulk.BulkMemberNames[index]}' ({owner}) is marked " +
                "[NumosSerialize(HostEditable = false)], but it changed between ticks. Numos does not record such an " +
                "edit, so replay would diverge. Change the member only through a solver command.");
        }
    }
}
