using System.Text.Json;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.Serialization;
using Numos.Serialization.FileSystem;
using static Numos.Headless.Tests.HeadlessTestSupport;

namespace Numos.Headless.Tests;

public enum HeadlessTestMode
{
    Off,
    On,
}

/// <summary>
///     Adds <see cref="Rate" /> to <see cref="Total" /> every tick, so a recorded edit to the rate shows in every later
///     state.
/// </summary>
[AtmosSolver(Name, Interval = 1)]
public sealed partial class HeadlessStateSolver : IAtmosWorldSolver
{
    public const string Name = "numos-headless-tests/state";

    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public float Total;
    [NumosSerialize] public HeadlessTestMode Mode;
    [NumosSerialize] public string? Note;
    [NumosSerialize] public ulong Seed;
    [NumosSerialize] public readonly List<int> Log = [];

    public void Solve(AtmosWorldSolverContext context) => Total += Rate;
}

/// <summary>
///     Counts ticks in a simulation component that it creates on its first run.
/// </summary>
[AtmosSolver(Name, Interval = 1)]
public sealed partial class HeadlessCounterSolver : IAtmosWorldSolver
{
    public const string Name = "numos-headless-tests/counter";

    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (var simulation in context.Simulations)
        {
            var counter = context.Get<HeadlessCounter>(simulation);
            counter.Ticks += 1 + counter.Bonus;
        }
    }
}

[SimulationComponent(Key)]
public sealed partial class HeadlessCounter
{
    public const string Key = "numos-headless-tests/counter";

    [NumosSerialize] public int Ticks;
    [NumosSerialize] public int Bonus;
}

[TestFixture]
public sealed class HeadlessSolverStateTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-headless-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, true);
    }

    private static JsonElement Member(JsonElement members, string name)
    {
        return members.EnumerateArray().Single(member => member.GetProperty("name").GetString() == name);
    }

    private static string Set(string id, string member, string value)
    {
        return Request(
            id,
            "setSolverState",
            $"\"solver\":\"{HeadlessStateSolver.Name}\",\"member\":\"{member}\",\"value\":{value}");
    }

    [Test]
    public async Task CatalogSolverCommands_AddListAndRemove()
    {
        const string name = HeadlessStateSolver.Name;
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("catalog", "listSolverCatalog"),
            Request("add", "addSolver", $"\"name\":\"{name}\",\"before\":\"gas-reactions\""),
            Request("listed", "listSolverCatalog"),
            Request("again", "addSolver", $"\"name\":\"{name}\""),
            Request("unknown", "addSolver", "\"name\":\"numos-headless-tests/missing\""),
            Request("noName", "addSolver"),
            Request("builtIn", "removeSolver", "\"name\":\"gas-reactions\""),
            Request("remove", "removeSolver", $"\"name\":\"{name}\""),
            Request("removeAgain", "removeSolver", $"\"name\":\"{name}\""));

        static JsonElement Entry(JsonElement result) => result.GetProperty("solverCatalog").EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == name);

        var added = run.Result("add").GetProperty("solvers").EnumerateArray().Select(static step => step.GetProperty("name").GetString()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(Entry(run.Result("catalog")).GetProperty("added").GetBoolean(), Is.False);
            Assert.That(Entry(run.Result("catalog")).GetProperty("interval").GetInt32(), Is.EqualTo(1));
            Assert.That(Entry(run.Result("catalog")).TryGetProperty("runsPerSecond", out _), Is.False);
            Assert.That(Entry(run.Result("listed")).GetProperty("added").GetBoolean(), Is.True);
            Assert.That(run.Result("add").GetProperty("changed").GetBoolean(), Is.True);
            Assert.That(added[^2..], Is.EqualTo(new[] { name, "gas-reactions" }));
            Assert.That(run.ErrorCode("again"), Is.EqualTo("operationRejected"));
            Assert.That(run.ErrorCode("unknown"), Is.EqualTo("solverNotFound"));
            Assert.That(run.ErrorCode("noName"), Is.EqualTo("missingProperty"));
            Assert.That(run.ErrorCode("builtIn"), Is.EqualTo("solverNotFound"));
            Assert.That(run.Result("remove").GetProperty("changed").GetBoolean(), Is.True);
            Assert.That(run.Result("removeAgain").GetProperty("changed").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task SetSolverState_IsVisibleRecordedAndReplayed()
    {
        string path = Path.Combine(_directory, "state.numos").Replace('\\', '/');
        const string solver = $"\"solver\":\"{HeadlessStateSolver.Name}\"";
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addSolver", $"\"name\":\"{HeadlessStateSolver.Name}\""),
            Request("initial", "getSolverState", solver),
            Request("warm", "tick", "\"count\":2"),
            Set("rate", "Rate", "3"),
            Set("mode", "Mode", "\"On\""),
            Set("note", "Note", "\"lit\""),
            Set("clear", "Note", "null"),
            Set("seed", "Seed", "18446744073709551615"),
            Set("same", "Rate", "3.0"),
            Request("after", "tick", "\"count\":2"),
            Request("read", "getSolverState", solver),
            Request("info", "timelineInfo"),
            Request("save", "saveReplay", $"\"path\":\"{path}\""),
            Request("load", "loadReplay", $"\"path\":\"{path}\",\"atHead\":true"),
            Request("loaded", "getSolverState", solver),
            Request("loadedInfo", "timelineInfo"));

        var initial = run.Result("initial").GetProperty("members");
        var read = run.Result("read").GetProperty("members");
        var loaded = run.Result("loaded").GetProperty("members");
        Assert.Multiple(() =>
        {
            Assert.That(
                initial.EnumerateArray().Select(static member => member.GetProperty("name").GetString()),
                Is.EqualTo(new[] { "Rate", "Total", "Mode", "Note", "Seed", "Log" }));

            Assert.That(Member(initial, "Rate").GetProperty("type").GetString(), Is.EqualTo("float"));
            Assert.That(Member(initial, "Rate").GetProperty("value").GetSingle(), Is.EqualTo(1f));
            Assert.That(Member(initial, "Rate").GetProperty("canWrite").GetBoolean(), Is.True);
            Assert.That(Member(initial, "Rate").GetProperty("wireName").GetString(), Is.EqualTo("Rate"));
            Assert.That(Member(initial, "Rate").TryGetProperty("unit", out _), Is.False);
            Assert.That(Member(initial, "Mode").GetProperty("value").GetString(), Is.EqualTo("Off"));
            Assert.That(Member(initial, "Note").GetProperty("value").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(Member(initial, "Log").GetProperty("scalar").GetBoolean(), Is.False);
            Assert.That(Member(initial, "Log").GetProperty("canWrite").GetBoolean(), Is.False);
            Assert.That(Member(initial, "Log").GetProperty("value").GetString(), Is.EqualTo("0 items"));

            Assert.That(run.Result("rate").GetProperty("changed").GetBoolean(), Is.True);
            Assert.That(run.Result("rate").GetProperty("member").GetProperty("value").GetSingle(), Is.EqualTo(3f));
            Assert.That(run.Result("mode").GetProperty("member").GetProperty("value").GetString(), Is.EqualTo("On"));
            Assert.That(run.Result("clear").GetProperty("member").GetProperty("value").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(run.Result("seed").GetProperty("member").GetProperty("value").GetUInt64(), Is.EqualTo(ulong.MaxValue));
            Assert.That(run.Result("same").GetProperty("changed").GetBoolean(), Is.False);

            Assert.That(Member(read, "Rate").GetProperty("value").GetSingle(), Is.EqualTo(3f));
            Assert.That(Member(read, "Total").GetProperty("value").GetSingle(), Is.EqualTo(8f));
            Assert.That(Member(loaded, "Rate").GetProperty("value").GetSingle(), Is.EqualTo(3f));
            Assert.That(Member(loaded, "Total").GetProperty("value").GetSingle(), Is.EqualTo(8f));
            Assert.That(Member(loaded, "Mode").GetProperty("value").GetString(), Is.EqualTo("On"));
            Assert.That(
                run.Result("loadedInfo").GetProperty("timeline").GetProperty("stateHash").GetString(),
                Is.EqualTo(run.Result("info").GetProperty("timeline").GetProperty("stateHash").GetString()));
        });
    }

    [Test]
    public async Task SetSolverState_RejectsBadRequests()
    {
        const string solver = $"\"solver\":\"{HeadlessStateSolver.Name}\"";
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addSolver", $"\"name\":\"{HeadlessStateSolver.Name}\""),
            Set("text", "Rate", "\"fast\""),
            Set("null", "Rate", "null"),
            Set("caseMode", "Mode", "\"on\""),
            Set("object", "Rate", "{}"),
            Set("readOnly", "Log", "4"),
            Set("member", "Nope", "4"),
            Request("noValue", "setSolverState", solver + ",\"member\":\"Rate\""),
            Request("noMember", "setSolverState", solver + ",\"value\":1"),
            Request("builtIn", "getSolverState", "\"solver\":\"gas-reactions\""),
            Request("missing", "setSolverState", "\"solver\":\"numos-headless-tests/missing\",\"member\":\"Rate\",\"value\":1"),
            Request("tick", "tick", "\"count\":2"),
            Request("seek", "seekTick", "\"tick\":1"),
            Set("inspecting", "Rate", "2"),
            Request("read", "getSolverState", solver));

        Assert.Multiple(() =>
        {
            Assert.That(run.ErrorCode("text"), Is.EqualTo("invalidValue"));
            Assert.That(run.ErrorCode("null"), Is.EqualTo("invalidValue"));
            Assert.That(run.ErrorCode("caseMode"), Is.EqualTo("invalidValue"));
            Assert.That(run.ErrorCode("object"), Is.EqualTo("invalidRequest"));
            Assert.That(run.ErrorCode("readOnly"), Is.EqualTo("memberReadOnly"));
            Assert.That(run.ErrorCode("member"), Is.EqualTo("memberNotFound"));
            Assert.That(run.ErrorCode("noValue"), Is.EqualTo("missingProperty"));
            Assert.That(run.ErrorCode("noMember"), Is.EqualTo("missingProperty"));
            Assert.That(run.ErrorCode("builtIn"), Is.EqualTo("solverNotFound"));
            Assert.That(run.ErrorCode("missing"), Is.EqualTo("solverNotFound"));
            Assert.That(run.ErrorCode("inspecting"), Is.EqualTo("operationRejected"));
            Assert.That(Member(run.Result("read").GetProperty("members"), "Rate").GetProperty("value").GetSingle(), Is.EqualTo(1f));
        });
    }

    [Test]
    public async Task SimulationComponentCommands_ListAndSetMembers()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("empty", "getSimulationComponents"),
            Request("add", "addSolver", $"\"name\":\"{HeadlessCounterSolver.Name}\""),
            Request("warm", "tick", "\"count\":1"),
            Request("set", "setSimulationComponentState", $"\"simulation\":0,\"key\":\"{HeadlessCounter.Key}\",\"member\":\"Bonus\",\"value\":2"),
            Request("missing", "setSimulationComponentState", "\"key\":\"numos-headless-tests/missing\",\"member\":\"Bonus\",\"value\":2"),
            Request("noKey", "setSimulationComponentState", "\"member\":\"Bonus\",\"value\":2"),
            Request("after", "tick", "\"count\":1"),
            Request("read", "getSimulationComponents", "\"simulation\":0"));

        var components = run.Result("read").GetProperty("components");
        var counter = components.EnumerateArray().Single();
        Assert.Multiple(() =>
        {
            Assert.That(run.Result("empty").GetProperty("components").GetArrayLength(), Is.Zero);
            Assert.That(run.Result("set").GetProperty("member").GetProperty("value").GetInt32(), Is.EqualTo(2));
            Assert.That(run.ErrorCode("missing"), Is.EqualTo("componentNotFound"));
            Assert.That(run.ErrorCode("noKey"), Is.EqualTo("missingProperty"));
            Assert.That(counter.GetProperty("key").GetString(), Is.EqualTo(HeadlessCounter.Key));
            Assert.That(Member(counter.GetProperty("members"), "Bonus").GetProperty("value").GetInt32(), Is.EqualTo(2));
            Assert.That(Member(counter.GetProperty("members"), "Ticks").GetProperty("value").GetInt32(), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task LoadReplay_NamesTheSolversTheCatalogLacks()
    {
        const string host = "numos-headless-tests/host";
        string path = Path.Combine(_directory, "host.numos");
        using (var world = new AtmosWorld(new AtmosConfig()))
        {
            world.CreateSimulation(1, 1, 1);
            world.Solvers.Register(host, static _ => { });
            var timeline = new AtmosWorldReplayTimeline(world, 0.05);
            world.Tick();
            var metadata = new NumosReplayMetadata("Host", DateTimeOffset.UnixEpoch, "tests", "1", "1");
            NumosReplayFile.Save(path, new NumosWorldReplayDocument(metadata, timeline.CaptureReplay()));
        }

        var run = await RunAsync(Request("load", "loadReplay", $"\"path\":\"{path.Replace('\\', '/')}\""));
        var error = run.Find("load").GetProperty("error");

        Assert.Multiple(() =>
        {
            Assert.That(run.ErrorCode("load"), Is.EqualTo("operationRejected"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain(host));
        });
    }
}
