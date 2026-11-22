using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     One place where two world checkpoints differ, as <see cref="AtmosWorldCheckpoint.Diff" /> reports it.
/// </summary>
/// <param name="Path">
///     Where the states differ, as segments joined by <c>" / "</c>. A segment is a field's wire name, a simulation
///     index, a chunk position such as <c>(1,0,0)</c>, a solver or configuration key, or a list index. An array element
///     adds its index in brackets, as in <c>Temperatures[412]</c>. A field with no registered name prints as
///     <c>#id</c>.
/// </param>
/// <param name="Detail">
///     What differs. A scalar gives both values, left first (<c>0.125 vs 0.25</c>). An array gives the first differing
///     element's values and how many elements differ (<c>300 vs 301.5; 17 of 4096 elements differ</c>). An item that
///     only one side has says which side.
/// </param>
/// <remarks>
///     Floats print in round-trip form. Two floats that print the same or compare equal (<c>0</c> and <c>-0</c>, two
///     NaN payloads) also print their raw bits, because the hash compares bits.
/// </remarks>
public readonly record struct AtmosStateDifference(string Path, string Detail)
{
    /// <summary>
    ///     Gets the path and detail on one line.
    /// </summary>
    /// <returns><see cref="Path" />, a colon, and <see cref="Detail" />.</returns>
    public override string ToString() => Path + ": " + Detail;
}

/// <summary>
///     Compares two world checkpoints through the traces of their generated state walks.
/// </summary>
/// <remarks>
///     <para>
///         The trace holds exactly what the state hash sees, so any hash difference shows up in it. Most of it is
///         compared position by position. A few levels hold items that one side can gain or lose: simulations, chunks,
///         solvers, solver states, simulation components, solver configurations, and rollback chunk fields. Those match by identity, so one
///         extra chunk is one difference and not a shift of every chunk after it.
///     </para>
///     <para>
///         Matching works on the trace too, by field id, so every field the walker writes is compared even if this code
///         never names it. When a matched level finds nothing although its values differ, it still reports the level,
///         so a difference in the trace always gives at least one entry.
///     </para>
/// </remarks>
internal sealed class AtmosStateDiffer
{
    private const string Separator = " / ";
    private const string OnlyLeft = "present only on the left";
    private const string OnlyRight = "present only on the right";

    private static readonly int SimulationsId = NumosState.FieldId("Simulations");
    private static readonly int SolversId = NumosState.FieldId("Solvers");
    private static readonly int SolverStatesId = NumosState.FieldId("SolverStates");
    private static readonly int SimulationComponentsId = NumosState.FieldId("SimulationComponents");
    private static readonly int ConfigId = NumosState.FieldId("Config");
    private static readonly int GasMixturesId = NumosState.FieldId("GasMixtures");
    private static readonly int CheckpointId = NumosState.FieldId("Checkpoint");
    private static readonly int ChunksId = NumosState.FieldId("Chunks");
    private static readonly int PagesId = NumosState.FieldId("Pages");
    private static readonly int SolverArraysId = NumosState.FieldId("SolverArrays");
    private static readonly int StateId = NumosState.FieldId("State");
    private static readonly int SimulationId = NumosState.FieldId("Simulation");
    private static readonly int IndexId = NumosState.FieldId("Index");
    private static readonly int NameId = NumosState.FieldId("Name");
    private static readonly int KeyId = NumosState.FieldId("Key");
    private static readonly int PositionId = NumosState.FieldId("Position");

    // The world's fields in reading order: where in time, then settings, then solvers, then grids. Fields not listed
    // follow in id order.
    private static readonly int[] WorldOrder =
    [
        PositionId,
        NumosState.FieldId("TickRate"),
        ConfigId,
        SolversId,
        SolverStatesId,
        SimulationComponentsId,
        SimulationsId,
        NumosState.FieldId("SimulationGenerations"),
        NumosState.FieldId("TopologyVersion"),
        NumosState.FieldId("LinkSlots"),
        GasMixturesId
    ];

    private readonly List<AtmosStateDifference> _differences = [];
    private readonly int _maxDifferences;

    private AtmosStateDiffer(int maxDifferences)
    {
        _maxDifferences = maxDifferences;
    }

    /// <summary>
    ///     Which object a field belongs to, for the levels that match items by identity.
    /// </summary>
    private enum Scope
    {
        Other,
        World,
        Simulation,
        Grid,
        Chunk,
        MixturePool,
        SolverState
    }

    private bool Full => _differences.Count >= _maxDifferences;

    /// <summary>
    ///     Lists where two checkpoints differ.
    /// </summary>
    /// <param name="left">The first checkpoint.</param>
    /// <param name="right">The second checkpoint.</param>
    /// <param name="maxDifferences">The most entries to return.</param>
    /// <returns>At least one entry. Call this only for checkpoints whose state hashes differ.</returns>
    public static IReadOnlyList<AtmosStateDifference> Compare(
        AtmosWorldCheckpoint left, AtmosWorldCheckpoint right, int maxDifferences)
    {
        var differ = new AtmosStateDiffer(maxDifferences);
        differ.CompareValues("", StateTraceSink.Trace(left), StateTraceSink.Trace(right), Scope.World);
        if (differ._differences.Count == 0)
        {
            // Only a hash collision in reverse could get here: equal traces with different digests.
            differ.Add(
                "StateHash",
                Hex(left.ComputeStateHash().Digest) + " vs " + Hex(right.ComputeStateHash().Digest) +
                "; no field difference found");
        }

        return differ._differences.AsReadOnly();
    }

    private void Add(string path, string detail)
    {
        if (!Full)
            _differences.Add(new AtmosStateDifference(path, detail));
    }

    private static string Join(string path, string segment) => path.Length == 0 ? segment : path + Separator + segment;

    private void CompareObject(string path, StateTraceObject left, StateTraceObject right, Scope scope)
    {
        var ids = new List<int>(left.Fields.Count);
        foreach (var field in left.Fields)
            ids.Add(field.Id);

        foreach (var field in right.Fields)
        {
            if (left.Find(field.Id) is null)
                ids.Add(field.Id);
        }

        if (scope == Scope.World)
        {
            ids.Sort(static (first, second) =>
            {
                int a = Array.IndexOf(WorldOrder, first);
                int b = Array.IndexOf(WorldOrder, second);
                a = a < 0 ? int.MaxValue : a;
                b = b < 0 ? int.MaxValue : b;
                return a != b ? a.CompareTo(b) : first.CompareTo(second);
            });
        }
        else
        {
            ids.Sort();
        }

        foreach (int id in ids)
        {
            if (Full)
                return;

            string fieldPath = Join(path, NumosState.GetFieldName(id));
            var first = left.Find(id);
            var second = right.Find(id);
            if (first is null)
            {
                Add(fieldPath, OnlyRight);
                continue;
            }

            if (second is null)
            {
                Add(fieldPath, OnlyLeft);
                continue;
            }

            if (StateTraceValue.AreEqual(first.Values, second.Values))
                continue;

            int before = _differences.Count;
            CompareField(path, fieldPath, id, first.Values, second.Values, scope);
            if (_differences.Count == before)
                Add(fieldPath, "values differ");
        }
    }

    private void CompareField(
        string parentPath, string path, int id, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right,
        Scope scope)
    {
        switch (scope)
        {
            case Scope.World when id == SimulationsId:
                CompareKeyed(path, left, right, SimulationKey, Scope.Simulation);
                return;
            case Scope.World when id == SolversId:
                CompareKeyed(path, left, right, NameKey, Scope.Other);
                return;
            case Scope.World when id == SolverStatesId:
                CompareKeyed(path, left, right, NameKey, Scope.SolverState);
                return;
            case Scope.World when id == SimulationComponentsId:
                // A component payload has the same shape as a solver state payload.
                CompareKeyed(path, left, right, ComponentKey, Scope.SolverState);
                return;
            case Scope.World when id == ConfigId:
                CompareConfig(path, left, right);
                return;
            case Scope.World when id == GasMixturesId:
                CompareValues(path, left, right, Scope.MixturePool);
                return;
            case Scope.Simulation when id == CheckpointId:
                // The grid's fields read as the simulation's own.
                CompareValues(parentPath, left, right, Scope.Grid);
                return;
            case Scope.Grid when id == ChunksId:
                CompareKeyed(path, left, right, PositionKey, Scope.Chunk);
                return;
            case Scope.MixturePool when id == PagesId:
                CompareValues(path, left, right, Scope.Chunk);
                return;
            case Scope.Chunk when id == SolverArraysId:
                CompareSolverArrays(path, left, right);
                return;
            case Scope.SolverState when id == StateId:
                // The payload's fields read as the solver's own.
                CompareSolverState(parentPath, left, right);
                return;
            default:
                CompareValues(path, left, right, Scope.Other);
                return;
        }
    }

    /// <summary>
    ///     Compares one field's values by the shapes the generic codecs write: a single value or object, a list (a count,
    ///     then elements or objects), a nullable (a presence flag, then the value), or an <c>Int3</c>. The scope applies
    ///     to an object in the values, alone or as a list element.
    /// </summary>
    private void CompareValues(
        string path, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right, Scope scope)
    {
        if (StateTraceValue.AreEqual(left, right))
            return;

        if (left.Count == 1 && right.Count == 1)
        {
            if (left[0].Kind == StateTraceKind.Object && right[0].Kind == StateTraceKind.Object)
                CompareObject(path, left[0].Object!, right[0].Object!, scope);
            else
                Add(path, Pair(left[0], right[0]));

            return;
        }

        if (left.Count > 0 && right.Count > 0 && left[0].Kind == StateTraceKind.Count && right[0].Kind == StateTraceKind.Count)
        {
            int count = (int)left[0].Bits;
            if (count != (int)right[0].Bits)
            {
                Add(path, Number(count) + " vs " + Number((int)right[0].Bits) + " elements");
                return;
            }

            if (left.Count == 2 && right.Count == 2 &&
                left[1].Kind == StateTraceKind.Elements && right[1].Kind == StateTraceKind.Elements)
            {
                CompareElements(path, left[1], right[1], left[1].ElementType);
                return;
            }

            if (left.Count == count + 1 && right.Count == count + 1)
            {
                if (AllObjects(left, 1) && AllObjects(right, 1))
                {
                    for (int index = 0; index < count && !Full; index++)
                        CompareObject(Join(path, Number(index)), left[index + 1].Object!, right[index + 1].Object!, scope);

                    return;
                }

                if (AllScalars(left, right, 1))
                {
                    CompareScalarRun(path, left, right, 1);
                    return;
                }
            }
        }

        if (left.Count > 0 && right.Count > 0 && left[0].Kind == StateTraceKind.Boolean && right[0].Kind == StateTraceKind.Boolean)
        {
            bool leftSet = left[0].Bits != 0;
            bool rightSet = right[0].Bits != 0;
            if (leftSet != rightSet && (leftSet ? right.Count == 1 && left.Count > 1 : left.Count == 1 && right.Count > 1))
            {
                Add(path, leftSet ? "set vs null" : "null vs set");
                return;
            }

            if (leftSet && rightSet && left.Count > 1 && right.Count > 1)
            {
                CompareValues(path, Skip(left, 1), Skip(right, 1), scope);
                return;
            }
        }

        if (IsInt3(left) && IsInt3(right))
        {
            Add(path, Int3Text(left) + " vs " + Int3Text(right));
            return;
        }

        int shared = Math.Min(left.Count, right.Count);
        for (int index = 0; index < shared; index++)
        {
            if (!StateTraceValue.AreEqual(left[index], right[index]))
            {
                Add(path, "value " + Number(index) + ": " + Pair(left[index], right[index]));
                return;
            }
        }

        Add(path, Number(left.Count) + " vs " + Number(right.Count) + " values");
    }

    /// <summary>
    ///     Matches a list of objects by a key each object carries. Lists that aren't keyed objects, or whose keys repeat,
    ///     fall back to position.
    /// </summary>
    private void CompareKeyed(
        string path, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right,
        Func<StateTraceObject, string?> key, Scope scope)
    {
        if (!TryKeyObjects(left, key, out var first) || !TryKeyObjects(right, key, out var second))
        {
            CompareValues(path, left, right, scope);
            return;
        }

        int before = _differences.Count;
        foreach ((string name, StateTraceObject value) in first)
        {
            if (Full)
                return;

            if (Lookup(second, name) is { } other)
                CompareObject(Join(path, name), value, other, scope);
            else
                Add(Join(path, name), OnlyLeft);
        }

        foreach ((string name, _) in second)
        {
            if (Lookup(first, name) is null)
                Add(Join(path, name), OnlyRight);
        }

        if (_differences.Count == before)
            Add(path, "same entries in a different order");
    }

    /// <summary>
    ///     Compares a configuration: its scalar fields, then its solver configurations matched by key.
    /// </summary>
    /// <remarks>
    ///     The codec writes the fields as one object, a count, and then a key and a payload per configuration. The
    ///     payload is an object, or the configuration's own hash when it has no wire format.
    /// </remarks>
    private void CompareConfig(string path, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right)
    {
        if (!TryReadConfig(left, out var leftFields, out var leftEntries) ||
            !TryReadConfig(right, out var rightFields, out var rightEntries))
        {
            CompareValues(path, left, right, Scope.Other);
            return;
        }

        int before = _differences.Count;
        if (!StateTraceObject.AreEqual(leftFields, rightFields))
            CompareObject(path, leftFields, rightFields, Scope.Other);

        string entries = Join(path, "SolverConfigurations");
        foreach ((string key, StateTraceValue value) in leftEntries)
        {
            if (Full)
                return;

            string entryPath = Join(entries, key);
            if (!TryLookup(rightEntries, key, out StateTraceValue other))
            {
                Add(entryPath, OnlyLeft);
                continue;
            }

            if (StateTraceValue.AreEqual(value, other))
                continue;

            if (value.Kind == StateTraceKind.Object && other.Kind == StateTraceKind.Object)
                CompareObject(entryPath, value.Object!, other.Object!, Scope.Other);
            else if (value.Kind == StateTraceKind.UInt64 && other.Kind == StateTraceKind.UInt64)
                Add(entryPath, "configuration hash " + Hex(value.Bits) + " vs " + Hex(other.Bits));
            else
                Add(entryPath, Pair(value, other));
        }

        foreach ((string key, _) in rightEntries)
        {
            if (!TryLookup(leftEntries, key, out _))
                Add(Join(entries, key), OnlyRight);
        }

        if (_differences.Count == before)
            Add(entries, "same entries in a different order");
    }

    /// <summary>
    ///     Compares rollback chunk fields matched by key, down to the first differing element.
    /// </summary>
    /// <remarks>
    ///     The codec writes a count, then per field its key, element type name, element size, element count, and one
    ///     element block.
    /// </remarks>
    private void CompareSolverArrays(string path, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right)
    {
        if (!TryReadSolverArrays(left, out var first) || !TryReadSolverArrays(right, out var second))
        {
            CompareValues(path, left, right, Scope.Other);
            return;
        }

        int before = _differences.Count;
        foreach (SolverArray array in first)
        {
            if (Full)
                return;

            string arrayPath = Join(path, array.Key);
            if (second.Find(other => other.Key == array.Key) is not { Key: not null } other)
            {
                Add(arrayPath, OnlyLeft);
                continue;
            }

            if (array.TypeName != other.TypeName)
                Add(arrayPath, "element type " + array.TypeName + " vs " + other.TypeName);
            else if (array.Length != other.Length)
                Add(arrayPath, Number(array.Length) + " vs " + Number(other.Length) + " elements");
            else if (!StateTraceValue.AreEqual(array.Values, other.Values))
                CompareElements(arrayPath, array.Values, other.Values, StateTraceSink.ElementTypeOf(array.TypeName));
        }

        foreach (SolverArray array in second)
        {
            if (!first.Exists(other => other.Key == array.Key))
                Add(Join(path, array.Key), OnlyRight);
        }

        if (_differences.Count == before)
            Add(path, "same entries in a different order");
    }

    /// <summary>
    ///     Compares two solver state payloads field by field.
    /// </summary>
    /// <remarks>
    ///     A payload is the solver's outermost state object in the wire format: a run of <c>int32</c> id, <c>int32</c>
    ///     length, and payload bytes. The checkpoint has no solver type, so the field bytes stay opaque. A payload that
    ///     does not parse is compared as bytes.
    /// </remarks>
    private void CompareSolverState(string path, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right)
    {
        if (!TryReadBytes(left, out byte[] first) || !TryReadBytes(right, out byte[] second))
        {
            CompareValues(Join(path, "State"), left, right, Scope.Other);
            return;
        }

        if (!TryReadPayload(first, out var leftFields) || !TryReadPayload(second, out var rightFields))
        {
            Add(Join(path, "State"), Bytes(first, second));
            return;
        }

        int before = _differences.Count;
        foreach ((int id, ArraySegment<byte> value) in leftFields)
        {
            if (Full)
                return;

            string fieldPath = Join(path, NumosState.GetFieldName(id));
            int match = rightFields.FindIndex(field => field.Id == id);
            if (match < 0)
                Add(fieldPath, OnlyLeft);
            else if (!value.AsSpan().SequenceEqual(rightFields[match].Value))
                Add(fieldPath, Bytes(value, rightFields[match].Value));
        }

        foreach ((int id, _) in rightFields)
        {
            if (!leftFields.Exists(field => field.Id == id))
                Add(Join(path, NumosState.GetFieldName(id)), OnlyRight);
        }

        if (_differences.Count == before)
            Add(Join(path, "State"), "same fields in a different order");
    }

    private void CompareElements(string path, StateTraceValue left, StateTraceValue right, StateTraceElementType type)
    {
        byte[] first = left.Elements!;
        byte[] second = right.Elements!;
        int size = left.ElementSize;
        if (size != right.ElementSize || size <= 0)
        {
            Add(path, "element size " + Number(size) + " vs " + Number(right.ElementSize));
            return;
        }

        if (first.Length != second.Length)
        {
            Add(path, Number(first.Length / size) + " vs " + Number(second.Length / size) + " elements");
            return;
        }

        if (type == StateTraceElementType.Unknown)
            type = right.ElementType;

        int count = first.Length / size;
        int firstIndex = -1;
        int differing = 0;
        for (int index = 0; index < count; index++)
        {
            if (first.AsSpan(index * size, size).SequenceEqual(second.AsSpan(index * size, size)))
                continue;

            if (firstIndex < 0)
                firstIndex = index;

            differing++;
        }

        if (firstIndex < 0)
            return;

        string detail = Element(type, first.AsSpan(firstIndex * size, size), second.AsSpan(firstIndex * size, size));
        Add(path + "[" + Number(firstIndex) + "]", detail + "; " + Number(differing) + " of " + Number(count) + " elements differ");
    }

    private void CompareScalarRun(string path, IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right, int start)
    {
        int firstIndex = -1;
        int differing = 0;
        for (int index = start; index < left.Count; index++)
        {
            if (StateTraceValue.AreEqual(left[index], right[index]))
                continue;

            if (firstIndex < 0)
                firstIndex = index;

            differing++;
        }

        if (firstIndex < 0)
            return;

        Add(
            path + "[" + Number(firstIndex - start) + "]",
            Pair(left[firstIndex], right[firstIndex]) + "; " + Number(differing) + " of " + Number(left.Count - start) +
            " elements differ");
    }

    private static string? SimulationKey(StateTraceObject simulation)
    {
        if (simulation.Find(SimulationId) is not { Values: [{ Kind: StateTraceKind.Object, Object: { } id }] })
            return null;

        return id.Find(IndexId) is { Values: [{ Kind: StateTraceKind.Int32 } index] } ? Number((int)index.Bits) : null;
    }

    private static string? ComponentKey(StateTraceObject component)
    {
        return SimulationKey(component) is { } simulation &&
               component.Find(KeyId) is { Values: [{ Kind: StateTraceKind.String } key] }
            ? simulation + Separator + key.Text
            : null;
    }

    private static string? NameKey(StateTraceObject value)
    {
        return value.Find(NameId) is { Values: [{ Kind: StateTraceKind.String } name] } ? name.Text : null;
    }

    private static string? PositionKey(StateTraceObject chunk)
    {
        return chunk.Find(PositionId) is { } position && IsInt3(position.Values) ? Int3Text(position.Values) : null;
    }

    private static bool TryKeyObjects(
        IReadOnlyList<StateTraceValue> values, Func<StateTraceObject, string?> key,
        out List<(string Key, StateTraceObject Value)> objects)
    {
        objects = [];
        if (values.Count == 0 || values[0].Kind != StateTraceKind.Count || values.Count != (int)values[0].Bits + 1 ||
            !AllObjects(values, 1))
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < values.Count; index++)
        {
            if (key(values[index].Object!) is not { } name || !seen.Add(name))
                return false;

            objects.Add((name, values[index].Object!));
        }

        return true;
    }

    private static StateTraceObject? Lookup(List<(string Key, StateTraceObject Value)> objects, string key)
    {
        foreach ((string name, StateTraceObject value) in objects)
        {
            if (name == key)
                return value;
        }

        return null;
    }

    private static bool TryReadConfig(
        IReadOnlyList<StateTraceValue> values, out StateTraceObject fields, out List<(string Key, StateTraceValue Value)> entries)
    {
        fields = null!;
        entries = [];
        if (values.Count < 2 || values[0].Kind != StateTraceKind.Object || values[1].Kind != StateTraceKind.Count ||
            values.Count != 2 + 2 * (long)(int)values[1].Bits)
            return false;

        fields = values[0].Object!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 2; index < values.Count; index += 2)
        {
            if (values[index].Kind != StateTraceKind.String || !seen.Add(values[index].Text!))
                return false;

            entries.Add((values[index].Text!, values[index + 1]));
        }

        return true;
    }

    private static bool TryLookup(List<(string Key, StateTraceValue Value)> entries, string key, out StateTraceValue value)
    {
        foreach ((string name, StateTraceValue entry) in entries)
        {
            if (name == key)
            {
                value = entry;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadSolverArrays(IReadOnlyList<StateTraceValue> values, out List<SolverArray> arrays)
    {
        arrays = [];
        if (values.Count == 0 || values[0].Kind != StateTraceKind.Count || values.Count != 1 + 5 * (long)(int)values[0].Bits)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < values.Count; index += 5)
        {
            if (values[index] is not { Kind: StateTraceKind.String, Text: { } key } ||
                values[index + 1] is not { Kind: StateTraceKind.String, Text: { } typeName } ||
                values[index + 2].Kind != StateTraceKind.Int32 ||
                values[index + 3].Kind != StateTraceKind.Int32 ||
                values[index + 4].Kind != StateTraceKind.Elements ||
                !seen.Add(key))
                return false;

            arrays.Add(new SolverArray(key, typeName, (int)values[index + 3].Bits, values[index + 4]));
        }

        return true;
    }

    private static bool TryReadBytes(IReadOnlyList<StateTraceValue> values, out byte[] bytes)
    {
        if (values is [{ Kind: StateTraceKind.Count }, { Kind: StateTraceKind.Elements, ElementSize: 1, Elements: { } elements }])
        {
            bytes = elements;
            return true;
        }

        bytes = [];
        return false;
    }

    private static bool TryReadPayload(byte[] payload, out List<(int Id, ArraySegment<byte> Value)> fields)
    {
        fields = [];
        int offset = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < 8)
                return false;

            int id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 4));
            offset += 8;
            if (length < 0 || length > payload.Length - offset || fields.Exists(field => field.Id == id))
                return false;

            fields.Add((id, new ArraySegment<byte>(payload, offset, length)));
            offset += length;
        }

        return true;
    }

    private static bool AllObjects(IReadOnlyList<StateTraceValue> values, int start)
    {
        for (int index = start; index < values.Count; index++)
        {
            if (values[index].Kind != StateTraceKind.Object)
                return false;
        }

        return true;
    }

    private static bool AllScalars(IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right, int start)
    {
        if (left.Count != right.Count || left.Count <= start)
            return false;

        StateTraceKind kind = left[start].Kind;
        if (kind is StateTraceKind.Object or StateTraceKind.Count or StateTraceKind.Elements)
            return false;

        for (int index = start; index < left.Count; index++)
        {
            if (left[index].Kind != kind || right[index].Kind != kind)
                return false;
        }

        return true;
    }

    private static bool IsInt3(IReadOnlyList<StateTraceValue> values) =>
        values is [{ Kind: StateTraceKind.Int32 }, { Kind: StateTraceKind.Int32 }, { Kind: StateTraceKind.Int32 }];

    private static string Int3Text(IReadOnlyList<StateTraceValue> values) =>
        "(" + Number((int)values[0].Bits) + "," + Number((int)values[1].Bits) + "," + Number((int)values[2].Bits) + ")";

    private static List<StateTraceValue> Skip(IReadOnlyList<StateTraceValue> values, int count)
    {
        var rest = new List<StateTraceValue>(values.Count - count);
        for (int index = count; index < values.Count; index++)
            rest.Add(values[index]);

        return rest;
    }

    private static string Pair(in StateTraceValue left, in StateTraceValue right)
    {
        if (left.Kind != right.Kind)
            return Describe(left) + " vs " + Describe(right);

        return left.Kind switch
        {
            StateTraceKind.Single => Floats(BitConverter.UInt32BitsToSingle((uint)left.Bits), BitConverter.UInt32BitsToSingle((uint)right.Bits)),
            StateTraceKind.Double => Doubles(BitConverter.UInt64BitsToDouble(left.Bits), BitConverter.UInt64BitsToDouble(right.Bits)),
            _ => Describe(left) + " vs " + Describe(right)
        };
    }

    private static string Describe(in StateTraceValue value)
    {
        return value.Kind switch
        {
            StateTraceKind.Object => "an object",
            StateTraceKind.Count => "count " + Number((int)value.Bits),
            StateTraceKind.Boolean => value.Bits != 0 ? "true" : "false",
            StateTraceKind.Byte or StateTraceKind.UInt16 or StateTraceKind.UInt32 or StateTraceKind.UInt64 =>
                value.Bits.ToString(CultureInfo.InvariantCulture),
            StateTraceKind.SByte => ((sbyte)value.Bits).ToString(CultureInfo.InvariantCulture),
            StateTraceKind.Int16 => ((short)value.Bits).ToString(CultureInfo.InvariantCulture),
            StateTraceKind.Int32 => ((int)value.Bits).ToString(CultureInfo.InvariantCulture),
            StateTraceKind.Int64 => ((long)value.Bits).ToString(CultureInfo.InvariantCulture),
            StateTraceKind.Single => BitConverter.UInt32BitsToSingle((uint)value.Bits).ToString("R", CultureInfo.InvariantCulture),
            StateTraceKind.Double => BitConverter.UInt64BitsToDouble(value.Bits).ToString("R", CultureInfo.InvariantCulture),
            StateTraceKind.String => "\"" + value.Text + "\"",
            StateTraceKind.Elements => Number(value.Elements!.Length / Math.Max(value.ElementSize, 1)) + " elements",
            _ => value.Kind.ToString()
        };
    }

    private static string Floats(float left, float right)
    {
        string first = left.ToString("R", CultureInfo.InvariantCulture);
        string second = right.ToString("R", CultureInfo.InvariantCulture);
        string text = first + " vs " + second;
        return first == second || left.Equals(right)
            ? text + " (bits 0x" + BitConverter.SingleToUInt32Bits(left).ToString("X8", CultureInfo.InvariantCulture) + " vs 0x" +
              BitConverter.SingleToUInt32Bits(right).ToString("X8", CultureInfo.InvariantCulture) + ")"
            : text;
    }

    private static string Doubles(double left, double right)
    {
        string first = left.ToString("R", CultureInfo.InvariantCulture);
        string second = right.ToString("R", CultureInfo.InvariantCulture);
        string text = first + " vs " + second;
        return first == second || left.Equals(right)
            ? text + " (bits 0x" + BitConverter.DoubleToUInt64Bits(left).ToString("X16", CultureInfo.InvariantCulture) + " vs 0x" +
              BitConverter.DoubleToUInt64Bits(right).ToString("X16", CultureInfo.InvariantCulture) + ")"
            : text;
    }

    /// <summary>
    ///     Prints one element from each side. The bytes are in native order, as the trace keeps them.
    /// </summary>
    private static string Element(StateTraceElementType type, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return type switch
        {
            StateTraceElementType.Single when left.Length == 4 => Floats(MemoryMarshal.Read<float>(left), MemoryMarshal.Read<float>(right)),
            StateTraceElementType.Double when left.Length == 8 => Doubles(MemoryMarshal.Read<double>(left), MemoryMarshal.Read<double>(right)),
            StateTraceElementType.Int32 when left.Length == 4 => Numbers(MemoryMarshal.Read<int>(left), MemoryMarshal.Read<int>(right)),
            StateTraceElementType.UInt32 when left.Length == 4 => Numbers(MemoryMarshal.Read<uint>(left), MemoryMarshal.Read<uint>(right)),
            StateTraceElementType.Int64 when left.Length == 8 => Numbers(MemoryMarshal.Read<long>(left), MemoryMarshal.Read<long>(right)),
            StateTraceElementType.UInt64 when left.Length == 8 => Numbers(MemoryMarshal.Read<ulong>(left), MemoryMarshal.Read<ulong>(right)),
            StateTraceElementType.Int16 when left.Length == 2 => Numbers(MemoryMarshal.Read<short>(left), MemoryMarshal.Read<short>(right)),
            StateTraceElementType.UInt16 or StateTraceElementType.Char when left.Length == 2 =>
                Numbers(MemoryMarshal.Read<ushort>(left), MemoryMarshal.Read<ushort>(right)),
            StateTraceElementType.Byte when left.Length == 1 => Numbers(left[0], right[0]),
            StateTraceElementType.SByte when left.Length == 1 => Numbers((sbyte)left[0], (sbyte)right[0]),
            _ => "0x" + Convert.ToHexString(left) + " vs 0x" + Convert.ToHexString(right)
        };
    }

    private static string Numbers<T>(T left, T right) where T : IFormattable =>
        left.ToString(null, CultureInfo.InvariantCulture) + " vs " + right.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>
    ///     Prints two opaque little-endian payloads. A 1-, 2-, 4-, or 8-byte payload is most often a scalar, so it also
    ///     prints as one.
    /// </summary>
    private static string Bytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length == right.Length)
        {
            switch (left.Length)
            {
                case 1:
                    return Numbers(left[0], right[0]) + " as a byte";
                case 2:
                    return Numbers(BinaryPrimitives.ReadInt16LittleEndian(left), BinaryPrimitives.ReadInt16LittleEndian(right)) +
                           " as int16";
                case 4:
                    return Numbers(BinaryPrimitives.ReadInt32LittleEndian(left), BinaryPrimitives.ReadInt32LittleEndian(right)) +
                           " as int32, " +
                           Floats(BinaryPrimitives.ReadSingleLittleEndian(left), BinaryPrimitives.ReadSingleLittleEndian(right)) +
                           " as float32";
                case 8:
                    return Numbers(BinaryPrimitives.ReadInt64LittleEndian(left), BinaryPrimitives.ReadInt64LittleEndian(right)) +
                           " as int64, " +
                           Doubles(BinaryPrimitives.ReadDoubleLittleEndian(left), BinaryPrimitives.ReadDoubleLittleEndian(right)) +
                           " as float64";
            }
        }

        int shared = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < shared && left[index] == right[index])
            index++;

        string text = Number(left.Length) + " vs " + Number(right.Length) + " bytes";
        return index < shared
            ? text + ", first difference at byte " + Number(index) + ": 0x" + left[index].ToString("X2", CultureInfo.InvariantCulture) +
              " vs 0x" + right[index].ToString("X2", CultureInfo.InvariantCulture)
            : text;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hex(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);

    private readonly record struct SolverArray(string Key, string TypeName, int Length, StateTraceValue Values);
}
