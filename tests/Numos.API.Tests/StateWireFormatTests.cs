using System.Collections.Frozen;
using System.Collections.Immutable;
using Numos.CoreSim;
using Numos.CoreSim.State;

namespace Numos.API.Tests;

/// <summary>
///     Covers <see cref="StateBinarySink" /> and <see cref="StateReader" /> through generated readers, the way a mod's
///     own <see cref="NumosStateAttribute" /> types use them.
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
    public void UnknownFieldIds_AreSkipped()
    {
        // Version 2 of Pair added field 3 between the two fields version 1 knows.
        var sink = new StateBinarySink();
        sink.BeginObject();
        sink.BeginField(1);
        sink.WriteSingle(1.5f);
        sink.EndField();
        sink.BeginField(3);
        sink.WriteString("from the future");
        sink.WriteCount(2);
        sink.EndField();
        sink.BeginField(5);
        sink.WriteInt32(7);
        sink.EndField();
        sink.EndObject();

        Pair read = Read<Pair>(sink.WrittenSpan);
        Assert.That(read, Is.EqualTo(new Pair(1.5f, 7)));
    }

    [Test]
    public void MissingFields_TakeTheParameterDefaultOrAnEmptyValue()
    {
        var sink = new StateBinarySink();
        sink.BeginObject();
        sink.BeginField(1);
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

        // A cut on a field boundary is just an older payload with fewer fields; anywhere else must be corrupt data,
        // never an index or overflow exception.
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
            Assert.That(() => Read<Pair>(Fields((5, Int32(1)), (1, Single(1f)))), Throws.TypeOf<InvalidDataException>(), "ids out of order");
            Assert.That(() => Read<Pair>(Fields((1, Single(1f)), (1, Single(2f)))), Throws.TypeOf<InvalidDataException>(), "repeated id");
            Assert.That(() => Read<Pair>(Fields((1, Int64(1)))), Throws.TypeOf<InvalidDataException>(), "field longer than its type");
            Assert.That(() => Read<Settings>(Fields((5, [2]))), Throws.TypeOf<InvalidDataException>(), "boolean other than 0 or 1");
            Assert.That(() => Read<Pair>([1, 0, 0, 0, 100, 0, 0, 0, 1, 2]), Throws.TypeOf<InvalidDataException>(), "length past the end");
            Assert.That(() => Read<Pair>([1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff]), Throws.TypeOf<InvalidDataException>(), "negative length");
            Assert.That(() => Read<Pair>(Fields((0, Int32(1)))), Throws.TypeOf<InvalidDataException>(), "id zero");
            Assert.That(
                () => Read<Shapes>(Fields((1, Int32(1_000_000)))),
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
        Assert.That(() => Read<Settings>(Fields((3, [1, 0, 0, 0, 0xff]))), Throws.TypeOf<InvalidDataException>());
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

    [NumosState]
    public readonly partial record struct Pair([property: StateField(1)] float Rate, [property: StateField(5)] int Count);

    [NumosState]
    public readonly partial record struct Inner([property: StateField(1)] int Value, [property: StateField(2)] string Note);

    [NumosState]
    public sealed partial class Settings
    {
        [StateField(1)] public float Rate { get; init; }
        [StateField(2)] public Inner Inner { get; init; }
        [StateField(3)] public string Label { get; init; } = "";
        [StateField(4)] public Inner? Maybe { get; init; }
        [StateField(5)] public bool Flag { get; init; }
        [StateField(6)] public int Count { get; init; }
    }

    // Every parameter is a different collection type from the field it fills, so the reader has to copy.
    [NumosState]
    public sealed partial class Shapes(
        IEnumerable<float> floats,
        ISet<string> names,
        IDictionary<string, int> counts,
        List<int> order,
        IList<Mode> modes)
    {
        [StateField(1)] public ImmutableArray<float> Floats { get; } = [.. floats];
        [StateField(2)] public FrozenSet<string> Names { get; } = names.ToFrozenSet();
        [StateField(3)] public FrozenDictionary<string, int> Counts { get; } = counts.ToFrozenDictionary();
        [StateField(4)] public int[] Order { get; } = [.. order];
        [StateField(5)] public IReadOnlyList<Mode> Modes { get; } = [.. modes];
    }

    [NumosState]
    public sealed partial class Defaults(float rate, float spread = 0.25f, Mode mode = Mode.On, IReadOnlyList<string>? names = null)
    {
        [StateField(1)] public float Rate { get; } = rate;
        [StateField(2)] public float Spread { get; } = spread;
        [StateField(3)] public Mode Mode { get; } = mode;
        [StateField(4)] public IReadOnlyList<string> Names { get; } = names ?? [];
        [StateField(5)] public string Label { get; init; } = "unset";
        [StateField(6)] public int Limit { get; init; } = 3;
    }
}
