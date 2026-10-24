using System.Collections.Frozen;
using System.Collections.Immutable;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API.Tests;

/// <summary>
///     Covers <see cref="StateBinarySink" /> and <see cref="StateReader" /> through generated readers, the way a mod's
///     own <see cref="NumosSerializeAttribute" /> types use them.
/// </summary>
[TestFixture]
public sealed partial class StateWireFormatTests
{
    [Test]
    public void CopiedCollectionParameters_RoundTrip()
    {
        var value = new Shapes(
            [1f, 2f, 3f],
            new HashSet<string> { "b", "a" },
            new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 },
            [4, 5],
            [Mode.On, Mode.Off]);

        Shapes read = RoundTrip(value);

        Assert.Multiple(() =>
        {
            Assert.That(NumosState.StateEquals(read, value), Is.True);
            Assert.That(NumosState.ComputeHash(read), Is.EqualTo(NumosState.ComputeHash(value)));
            Assert.That(read.Floats, Is.EqualTo(new[] { 1f, 2f, 3f }));
            Assert.That(read.Modes, Is.EqualTo(new[] { Mode.On, Mode.Off }));
        });
    }

    [Test]
    public void InitOnlyFieldsAndNestedValues_RoundTrip()
    {
        var value = new Settings { Rate = 0.5f, Label = "fire", Inner = new Inner(3, "x"), Maybe = null, Count = 9 };
        Settings read = RoundTrip(value);

        Assert.That(NumosState.StateEquals(read, value), Is.True);
        Assert.That(read.Inner.Note, Is.EqualTo("x"));
    }

    [Test]
    public void PrimitiveBlocks_MatchPerElementWrites()
    {
        float[] floats = [1.5f, -0f, float.NaN, 3e7f];
        ushort[] shorts = [1, 2, 65535];

        var block = new StateBinarySink();
        var blockHash = new StateHashSink();
        WriteBlocks(ref block, floats, shorts);
        WriteBlocks(ref blockHash, floats, shorts);

        var single = new StateBinarySink();
        var singleHash = new StateHashSink();
        WriteOneByOne(ref single, floats, shorts);
        WriteOneByOne(ref singleHash, floats, shorts);

        Assert.Multiple(() =>
        {
            Assert.That(block.WrittenSpan.ToArray(), Is.EqualTo(single.WrittenSpan.ToArray()));
            Assert.That(blockHash.Value, Is.EqualTo(singleHash.Value));
        });

        var reader = new StateReader(block.WrittenSpan);
        float[] readFloats = reader.ReadArray<float>(floats.Length);
        ushort[] readShorts = reader.ReadArray<ushort>(shorts.Length);
        bool atEnd = reader.IsAtEnd;
        Assert.Multiple(() =>
        {
            Assert.That(readFloats.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(floats.Select(BitConverter.SingleToInt32Bits)));
            Assert.That(readShorts, Is.EqualTo(shorts));
            Assert.That(atEnd, Is.True);
        });
    }

    [Test]
    public void PrimitiveBlock_LongerThanTheData_IsCorruptData()
    {
        byte[] data = new byte[6];
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new StateReader(data);
            reader.ReadArray<float>(2);
        });
    }

    private static void WriteBlocks<TSink>(ref TSink sink, float[] floats, ushort[] shorts) where TSink : struct, IStateSink
    {
        sink.WriteElements(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()), sizeof(float));
        sink.WriteElements(System.Runtime.InteropServices.MemoryMarshal.AsBytes(shorts.AsSpan()), sizeof(ushort));
    }

    private static void WriteOneByOne<TSink>(ref TSink sink, float[] floats, ushort[] shorts) where TSink : struct, IStateSink
    {
        foreach (float value in floats)
            sink.WriteSingle(value);

        foreach (ushort value in shorts)
            sink.WriteUInt16(value);
    }

    [Test]
    public void CodecOverride_RoundTripsNull()
    {
        var value = new Tagged { Name = null!, Limit = 8 };
        Tagged read = RoundTrip(value);

        Assert.Multiple(() =>
        {
            Assert.That(read.Name, Is.Null);
            Assert.That(read.Limit, Is.EqualTo(8));
            Assert.That(NumosState.StateEquals(read, value), Is.True);
            Assert.That(NumosState.ComputeHash(read), Is.Not.EqualTo(NumosState.ComputeHash(new Tagged { Name = "", Limit = 8 })));
        });
    }

    [Test]
    public void MissingSettableFields_KeepTheirInitializers()
    {
        var sink = new StateBinarySink();
        sink.BeginObject();
        sink.EndObject();

        Tagged read = Read<Tagged>(sink.WrittenSpan);
        Assert.Multiple(() =>
        {
            Assert.That(read.Name, Is.EqualTo("unnamed"));
            Assert.That(read.Limit, Is.EqualTo(3));
        });
    }

    [Test]
    public void FieldIds_AreFnv1aOfTheWireName()
    {
        Assert.Multiple(() =>
        {
            // 0xe40c292c is the published FNV-1a test vector for "a". The sign bit is cleared.
            Assert.That(NumosState.FieldId("a"), Is.EqualTo(0x640c292c));
            Assert.That(NumosState.FieldId("Rate"), Is.EqualTo(783280263));
            Assert.That(NumosState.FieldId("Count"), Is.EqualTo(1642576020));
            Assert.That(NumosState.FieldId("GlobalTemperature"), Is.EqualTo(1407824132));
        });
    }

    [Test]
    public void FieldsAreWrittenInIdOrder()
    {
        Assert.That(Write(new Pair(1.5f, 7)), Is.EqualTo(Sorted((Id("Count"), Int32(7)), (Id("Rate"), Single(1.5f)))));
    }

    [Test]
    public void RenamedMember_KeepsReadingItsOldName()
    {
        Renamed read = Read<Renamed>(Write(new Pair(1.5f, 7)));
        Assert.That(read, Is.EqualTo(new Renamed(1.5f, 7)));
    }

    [Test]
    public void UnknownFieldIds_AreSkipped()
    {
        // A newer Pair added a field this one doesn't know about.
        byte[] payload = Sorted(
            (Id("Rate"), Single(1.5f)),
            (Id("Spread"), [.. Int32(3), .. "new"u8]),
            (Id("Count"), Int32(7)));

        Pair read = Read<Pair>(payload);
        Assert.That(read, Is.EqualTo(new Pair(1.5f, 7)));
    }

    [Test]
    public void MissingFields_TakeTheParameterDefaultOrAnEmptyValue()
    {
        var sink = new StateBinarySink();
        sink.BeginObject();
        sink.BeginField(Id("Rate"));
        sink.WriteSingle(2f);
        sink.EndField();
        sink.EndObject();

        Defaults read = Read<Defaults>(sink.WrittenSpan);
        Assert.Multiple(() =>
        {
            Assert.That(read.Rate, Is.EqualTo(2f));
            Assert.That(read.Spread, Is.EqualTo(0.25f), "parameter default");
            Assert.That(read.Mode, Is.EqualTo(Mode.On), "enum parameter default");
            Assert.That(read.Names, Is.Empty);
            Assert.That(read.Label, Is.EqualTo(""));
            Assert.That(read.Limit, Is.Zero);
        });
    }

    [Test]
    public void EmptyPayload_ReadsAsAllDefaults()
    {
        Pair read = Read<Pair>([]);
        Assert.That(read, Is.EqualTo(new Pair(0f, 0)));
    }

    [Test]
    public void TruncatedPayload_IsCorruptData()
    {
        byte[] bytes = Write(new Settings { Rate = 1f, Label = "abc", Inner = new Inner(1, "n"), Count = 2 });
        Assert.That(() => Read<Settings>(bytes[..^1]), Throws.TypeOf<InvalidDataException>());

        // A cut on a field boundary is just an older payload with fewer fields. A cut anywhere else must be corrupt
        // data, never an index or overflow exception.
        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] truncated = bytes[..length];
            try
            {
                var reader = new StateReader(truncated);
                NumosState.Read<Settings>(ref reader);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Test]
    public void MalformedPayloads_AreCorruptData()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => Read<Pair>(Fields((Id("Count"), Int32(1)), (Id("Rate"), Single(1f)))),
                Throws.TypeOf<InvalidDataException>(),
                "ids out of order");

            Assert.That(() => Read<Pair>(Fields((Id("Rate"), Single(1f)), (Id("Rate"), Single(2f)))), Throws.TypeOf<InvalidDataException>(), "repeated id");
            Assert.That(() => Read<Pair>(Fields((Id("Rate"), Int64(1)))), Throws.TypeOf<InvalidDataException>(), "field longer than its type");
            Assert.That(() => Read<Settings>(Fields((Id("Flag"), [2]))), Throws.TypeOf<InvalidDataException>(), "boolean other than 0 or 1");
            Assert.That(() => Read<Pair>([1, 0, 0, 0, 100, 0, 0, 0, 1, 2]), Throws.TypeOf<InvalidDataException>(), "length past the end");
            Assert.That(() => Read<Pair>([1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff]), Throws.TypeOf<InvalidDataException>(), "negative length");
            Assert.That(() => Read<Pair>(Fields((0, Int32(1)))), Throws.TypeOf<InvalidDataException>(), "id zero");
            Assert.That(
                () => Read<Shapes>(Fields((Id("Floats"), Int32(1_000_000)))),
                Throws.TypeOf<InvalidDataException>(),
                "count larger than the bytes left");
        });
    }

    [Test]
    public void Limits_AreEnforced()
    {
        byte[] label = Write(new Settings { Label = new string('x', 100), Inner = new Inner(0, "") });
        Assert.That(() => Read<Settings>(label, maxStringBytes: 99), Throws.TypeOf<InvalidDataException>());
        Assert.That(Read<Settings>(label, maxStringBytes: 100).Label, Has.Length.EqualTo(100));

        byte[] nested = Write(new Settings { Inner = new Inner(0, "") });
        Assert.That(() => Read<Settings>(nested, maxDepth: 1), Throws.TypeOf<InvalidDataException>().With.Message.Contains("deeply"));
        Assert.That(Read<Settings>(nested, maxDepth: 2), Is.Not.Null);
    }

    [Test]
    public void InvalidUtf8_IsCorruptData()
    {
        Assert.That(() => Read<Settings>(Fields((Id("Label"), [1, 0, 0, 0, 0xff]))), Throws.TypeOf<InvalidDataException>());
    }

    private static T RoundTrip<T>(T value) where T : INumosState<T> => Read<T>(Write(value));

    private static byte[] Write<T>(T value) where T : INumosState<T>
    {
        var sink = new StateBinarySink();
        NumosState.Write(ref sink, value);
        return sink.WrittenSpan.ToArray();
    }

    private static T Read<T>(ReadOnlySpan<byte> payload, int maxStringBytes = StateReader.DefaultMaxStringBytes, int maxDepth = 64)
        where T : INumosState<T>
    {
        var reader = new StateReader(payload, maxStringBytes, maxDepth);
        T value = NumosState.Read<T>(ref reader);
        Assert.That(reader.IsAtEnd, Is.True);
        return value;
    }

    private static int Id(string name) => NumosState.FieldId(name);

    private static byte[] Sorted(params (int Id, byte[] Payload)[] fields) => Fields([.. fields.OrderBy(static field => field.Id)]);

    // Lays fields out by hand, so tests can write payloads no generated writer would.
    private static byte[] Fields(params (int Id, byte[] Payload)[] fields)
    {
        var bytes = new List<byte>();
        foreach ((int id, byte[] payload) in fields)
        {
            bytes.AddRange(BitConverter.GetBytes(id));
            bytes.AddRange(BitConverter.GetBytes(payload.Length));
            bytes.AddRange(payload);
        }

        return bytes.ToArray();
    }

    private static byte[] Int32(int value) => BitConverter.GetBytes(value);

    private static byte[] Int64(long value) => BitConverter.GetBytes(value);

    private static byte[] Single(float value) => BitConverter.GetBytes(value);

    public enum Mode : byte
    {
        Off,
        On,
    }

    public readonly partial record struct Pair([property: NumosSerialize] float Rate, [property: NumosSerialize] int Count);

    // Pair after renaming Rate to Speed, still reading files that call it Rate.
    public readonly partial record struct Renamed([property: NumosSerialize("Rate")] float Speed, [property: NumosSerialize] int Count);

    public readonly partial record struct Inner([property: NumosSerialize] int Value, [property: NumosSerialize] string Note);

    public sealed partial class Settings
    {
        [NumosSerialize] public float Rate { get; init; }
        [NumosSerialize] public Inner Inner { get; init; }
        [NumosSerialize] public string Label { get; init; } = "";
        [NumosSerialize] public Inner? Maybe { get; init; }
        [NumosSerialize] public bool Flag { get; init; }
        [NumosSerialize] public int Count { get; init; }
    }

    // Every parameter is a different collection type from the field it fills, so the reader has to copy.
    public sealed partial class Shapes(
        IEnumerable<float> floats,
        ISet<string> names,
        IDictionary<string, int> counts,
        List<int> order,
        IList<Mode> modes)
    {
        [NumosSerialize] public ImmutableArray<float> Floats { get; } = [.. floats];
        [NumosSerialize] public FrozenSet<string> Names { get; } = names.ToFrozenSet();
        [NumosSerialize] public FrozenDictionary<string, int> Counts { get; } = counts.ToFrozenDictionary();
        [NumosSerialize] public int[] Order { get; } = [.. order];
        [NumosSerialize] public IReadOnlyList<Mode> Modes { get; } = [.. modes];
    }

    public sealed partial class Defaults(float rate, float spread = 0.25f, Mode mode = Mode.On, IReadOnlyList<string>? names = null)
    {
        [NumosSerialize] public float Rate { get; } = rate;
        [NumosSerialize] public float Spread { get; } = spread;
        [NumosSerialize] public Mode Mode { get; } = mode;
        [NumosSerialize] public IReadOnlyList<string> Names { get; } = names ?? [];
        [NumosSerialize] public string Label { get; init; } = "unset";
        [NumosSerialize] public int Limit { get; init; } = 3;
    }

    // A codec override, and set properties that keep their initializer when the data doesn't have them.
    public sealed partial class Tagged
    {
        [NumosSerialize(Codec = typeof(StateCodecs.Nullable<string, StateCodecs.String>))]
        public string Name { get; set; } = "unnamed";

        [NumosSerialize] public int Limit { get; set; } = 3;
    }
}
