using System.Text.Json;
using static Numos.Headless.Tests.HeadlessTestSupport;

namespace Numos.Headless.Tests;

[TestFixture]
public sealed class HeadlessProtocolTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, true);
    }

    private string PathFor(string name)
    {
        return Path.Combine(_directory, name).Replace('\\', '/');
    }

    private static float GetFloat(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? float.Parse(element.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : element.GetSingle();
    }

    [Test]
    public async Task Hello_ReportsProtocolAndVersionsWithoutAProject()
    {
        var run = await RunAsync(Request("hello", "hello"), Request("exit", "exit"));

        var hello = run.Result("hello").GetProperty("hello");
        Assert.Multiple(() =>
        {
            Assert.That(hello.GetProperty("protocol").GetInt32(), Is.EqualTo(2));
            Assert.That(hello.GetProperty("host").GetString(), Is.EqualTo("Numos.Headless"));
            Assert.That(hello.GetProperty("hostVersion").GetString(), Is.Not.Empty);
            Assert.That(hello.GetProperty("coreSimVersion").GetString(), Is.Not.Empty);
            Assert.That(run.Find("hello").TryGetProperty("state", out _), Is.False);
        });
    }

    [Test]
    public async Task ProtocolVersionOne_IsRejected()
    {
        var run = await RunAsync("{\"protocolVersion\":1,\"id\":\"old\",\"op\":\"hello\"}");

        Assert.That(run.ErrorCode("old"), Is.EqualTo("unsupportedProtocol"));
    }

    [Test]
    public async Task OperationsNeedingAProject_ReturnSimulationNotCreated()
    {
        var run = await RunAsync(Request("tick", "tick", "\"count\":1"));

        Assert.That(run.ErrorCode("tick"), Is.EqualTo("simulationNotCreated"));
    }

    [Test]
    public async Task LogStream_GoesToStderrAndQuietDropsInfoLines()
    {
        var verbose = await RunWithArgsAsync([], CreateProject("create", 2, 1, 1), Request("exit", "exit"));
        var quiet = await RunAsync(CreateProject("create", 2, 1, 1), Request("exit", "exit"));

        Assert.Multiple(() =>
        {
            Assert.That(verbose.StandardError, Does.Contain("INFO").And.Contain("Created project 'Headless tests'"));
            Assert.That(verbose.StandardError, Does.Contain("tick=0"));
            Assert.That(verbose.OutputLines.All(line => line.StartsWith('{')), Is.True);
            Assert.That(quiet.StandardError, Is.Empty);
        });
    }

    [Test]
    public async Task State_ReportsProjectSimulationAndTimelineFlag()
    {
        var run = await RunAsync(
            CreateProject("create", 4, 4, 1),
            Request("add", "addChunk", $"\"position\":{Coordinate0}"),
            Request("tick", "tick", "\"count\":3"),
            Request("seek", "seekTick", "\"tick\":1"));

        var state = run.Find("seek").GetProperty("state");
        Assert.Multiple(() =>
        {
            Assert.That(state.GetProperty("project").GetString(), Is.EqualTo("Headless tests"));
            Assert.That(state.GetProperty("simulation").GetString(), Is.EqualTo("Simulation 1"));
            Assert.That(state.GetProperty("simulationCount").GetInt32(), Is.EqualTo(1));
            Assert.That(state.GetProperty("chunkCount").GetInt32(), Is.EqualTo(1));
            Assert.That(state.GetProperty("gasCount").GetInt32(), Is.EqualTo(1));
            Assert.That(state.GetProperty("tick").GetInt32(), Is.EqualTo(1));
            Assert.That(state.GetProperty("inspecting").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Simulations_AddSelectAndAddressByNameOrId()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 2, 1),
            Request("add", "addSimulation", $"\"name\":\"Shuttle\",\"chunkDimensions\":{C(3, 3, 1)}"),
            Request("chunk-active", "addChunk", $"\"position\":{Coordinate0}"),
            Request("chunk-by-id", "addChunk", $"\"position\":{Coordinate0},\"simulation\":0"),
            Request("chunk-by-name", "addChunk", $"\"position\":{C(1, 0, 0)},\"simulation\":\"simulation 1\""),
            Request("missing", "addChunk", $"\"position\":{Coordinate0},\"simulation\":\"Nope\""),
            Request("select", "selectSimulation", "\"simulation\":0"),
            Request("list", "listSimulations"),
            Request("remove", "removeSimulation", "\"simulation\":\"Shuttle\""),
            Request("after", "listSimulations"));

        var simulations = run.Result("list").GetProperty("simulations");
        Assert.Multiple(() =>
        {
            Assert.That(run.Result("add").GetProperty("simulationName").GetString(), Is.EqualTo("Shuttle"));
            Assert.That(run.Find("chunk-active").GetProperty("state").GetProperty("simulation").GetString(), Is.EqualTo("Shuttle"));
            Assert.That(run.Find("chunk-active").GetProperty("state").GetProperty("chunkCount").GetInt32(), Is.EqualTo(1));
            Assert.That(run.Result("chunk-by-id").GetProperty("position").GetProperty("x").GetInt32(), Is.Zero);
            Assert.That(run.ErrorCode("missing"), Is.EqualTo("simulationNotFound"));
            Assert.That(simulations.GetArrayLength(), Is.EqualTo(2));
            Assert.That(simulations[0].GetProperty("chunkCount").GetInt32(), Is.EqualTo(2));
            Assert.That(simulations[0].GetProperty("active").GetBoolean(), Is.True);
            Assert.That(simulations[1].GetProperty("name").GetString(), Is.EqualTo("Shuttle"));
            Assert.That(simulations[1].GetProperty("chunkCount").GetInt32(), Is.EqualTo(1));
            Assert.That(run.Result("remove").GetProperty("changed").GetBoolean(), Is.True);
            Assert.That(run.Result("after").GetProperty("simulations").GetArrayLength(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ChunkOperations_SealFillSleepAndWake()
    {
        var run = await RunAsync(
            CreateProject("create", 4, 4, 1),
            Request("add", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request("seal", "sealChunk", $"\"position\":{Coordinate0}"),
            Request("sleep", "sleepChunk", $"\"position\":{Coordinate0}"),
            Request("wake", "wakeChunk", $"\"position\":{Coordinate0}"),
            Request("fill", "fillChunk", $"\"position\":{Coordinate0},\"classification\":2"),
            Request("sample", "sampleVoxels", $"\"points\":[{C(0, 0, 0)},{C(1, 1, 0)}],\"fields\":[\"classification\"]"),
            Request("remove", "removeChunk", $"\"position\":{Coordinate0}"),
            Request("again", "removeChunk", $"\"position\":{Coordinate0}"));

        var values = run.Result("sample").GetProperty("samples").GetProperty("values");
        Assert.Multiple(() =>
        {
            Assert.That(GetFloat(values[0][0]), Is.EqualTo(2f));
            Assert.That(GetFloat(values[1][0]), Is.EqualTo(2f));
            Assert.That(run.Result("remove").GetProperty("changed").GetBoolean(), Is.True);
            Assert.That(run.Result("again").GetProperty("changed").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task VoxelOperations_ClassifyHeatInjectAndClear()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request("heat", "setVoxelTemperature", $"\"position\":{Coordinate0},\"voxel\":{C(0, 0, 0)},\"temperatureK\":350"),
            Request("inject", "injectGas", $"\"position\":{Coordinate0},\"voxel\":{C(0, 0, 0)},\"gasId\":0,\"moles\":2,\"temperatureK\":350"),
            Request("wall", "setVoxelClassification", $"\"position\":{Coordinate0},\"voxel\":{C(1, 0, 0)},\"classification\":-2"),
            Request("before", "sampleVoxels", $"\"points\":[{C(0, 0, 0)},{C(1, 0, 0)}],\"fields\":[\"temperature\",\"totalMoles\",\"moles:0\"]"),
            Request("clear", "clearVoxelGas", $"\"position\":{Coordinate0},\"voxel\":{C(0, 0, 0)}"),
            Request("after", "sampleVoxels", $"\"points\":[{C(0, 0, 0)}],\"fields\":[\"totalMoles\"]"));

        var before = run.Result("before").GetProperty("samples").GetProperty("values");
        Assert.Multiple(() =>
        {
            Assert.That(GetFloat(before[0][0]), Is.EqualTo(350f).Within(0.01f));
            Assert.That(GetFloat(before[0][1]), Is.EqualTo(2f).Within(1e-4f));
            Assert.That(GetFloat(before[0][2]), Is.EqualTo(2f).Within(1e-4f));
            Assert.That(float.IsNaN(GetFloat(before[1][0])), Is.True, "solid voxels read as NaN");
            Assert.That(GetFloat(run.Result("after").GetProperty("samples").GetProperty("values")[0][0]), Is.Zero);
        });
    }

    [Test]
    public async Task EditRegion_FillsGlobalBoxAcrossChunksAndCountsVoxels()
    {
        var run = await RunAsync(
            CreateProject("create", 4, 4, 1),
            Request(
                "region",
                "editRegion",
                $"\"min\":{C(-2, 0, 0)},\"max\":{C(5, 2, 0)},\"createChunks\":true,\"classification\":1," +
                "\"temperatureK\":300,\"gas\":[{\"gasId\":0,\"moles\":3}]"),
            Request("sample", "sampleVoxels", $"\"points\":[{C(-2, 0, 0)},{C(5, 2, 0)},{C(6, 2, 0)},{C(-3, 0, 0)}],\"fields\":[\"totalMoles\"]"),
            Request("repeat", "editRegion", $"\"min\":{C(-2, 0, 0)},\"max\":{C(5, 2, 0)}"),
            Request("outside", "editRegion", $"\"min\":{C(100, 0, 0)},\"max\":{C(101, 0, 0)}"),
            Request("reversed", "editRegion", $"\"min\":{C(2, 0, 0)},\"max\":{C(1, 0, 0)}"));

        var region = run.Result("region").GetProperty("region");
        var values = run.Result("sample").GetProperty("samples").GetProperty("values");
        Assert.Multiple(() =>
        {
            Assert.That(region.GetProperty("voxelsEdited").GetInt64(), Is.EqualTo(8 * 3));
            Assert.That(region.GetProperty("voxelsSkipped").GetInt64(), Is.Zero);
            Assert.That(region.GetProperty("chunksCreated").GetInt32(), Is.EqualTo(3));
            Assert.That(region.GetProperty("chunksTouched").GetInt32(), Is.EqualTo(3));
            Assert.That(run.Find("region").GetProperty("state").GetProperty("chunkCount").GetInt32(), Is.EqualTo(3));
            Assert.That(GetFloat(values[0][0]), Is.EqualTo(3f));
            Assert.That(GetFloat(values[1][0]), Is.EqualTo(3f));
            Assert.That(float.IsNaN(GetFloat(values[2][0])), Is.False, "x = 6 shares a chunk with x = 5");
            Assert.That(float.IsNaN(GetFloat(values[3][0])), Is.False, "x = -3 shares a chunk with x = -2");
            Assert.That(run.Result("repeat").GetProperty("region").GetProperty("chunksCreated").GetInt32(), Is.Zero);
            Assert.That(run.Result("outside").GetProperty("region").GetProperty("voxelsSkipped").GetInt64(), Is.EqualTo(2));
            Assert.That(run.ErrorCode("reversed"), Is.EqualTo("invalidRegion"));
        });
    }

    [Test]
    public async Task EditRegion_PartialPressureConvertsWithTheIdealGasLaw()
    {
        // Voxel volume 8.31446262 m^3 makes n = p / T numerically, because R / V = 1.
        var run = await RunAsync(
            CreateProject("create", 4, 1, 1),
            Request(
                "region",
                "editRegion",
                $"\"min\":{C(0, 0, 0)},\"max\":{C(3, 0, 0)},\"createChunks\":true,\"classification\":1," +
                "\"gas\":[{\"gasId\":0,\"partialPressurePa\":6000}],\"gasTemperatureK\":300"),
            Request("pressure", "sampleVoxels", $"\"points\":[{C(1, 0, 0)}],\"fields\":[\"pressure\",\"temperature\",\"totalMoles\"]"),
            Request("both", "editRegion", $"\"min\":{C(0, 0, 0)},\"max\":{C(0, 0, 0)},\"gas\":[{{\"gasId\":0,\"moles\":1,\"partialPressurePa\":1}}]"),
            Request("none", "editRegion", $"\"min\":{C(0, 0, 0)},\"max\":{C(0, 0, 0)},\"gas\":[{{\"gasId\":0}}]"),
            Request("badGas", "editRegion", $"\"min\":{C(0, 0, 0)},\"max\":{C(0, 0, 0)},\"gas\":[{{\"gasId\":9,\"moles\":1}}]"));

        var region = run.Result("region").GetProperty("region");
        var values = run.Result("pressure").GetProperty("samples").GetProperty("values")[0];
        Assert.Multiple(() =>
        {
            Assert.That(region.GetProperty("molesPerVoxel")[0].GetSingle(), Is.EqualTo(20f).Within(1e-3f));
            Assert.That(GetFloat(values[0]), Is.EqualTo(6000f).Within(1f));
            Assert.That(GetFloat(values[1]), Is.EqualTo(300f).Within(0.01f));
            Assert.That(GetFloat(values[2]), Is.EqualTo(20f).Within(1e-3f));
            Assert.That(run.ErrorCode("both"), Is.EqualTo("operationRejected"));
            Assert.That(run.ErrorCode("none"), Is.EqualTo("operationRejected"));
            Assert.That(run.ErrorCode("badGas"), Is.EqualTo("operationRejected"));
        });
    }

    [Test]
    public async Task EditRegion_ClearGasReplacesExistingGas()
    {
        var run = await RunAsync(
            CreateProject("create", 4, 1, 1),
            Request("fill", "editRegion", $"\"min\":{C(0, 0, 0)},\"max\":{C(3, 0, 0)},\"createChunks\":true,\"classification\":1,\"temperatureK\":300,\"gas\":[{{\"gasId\":0,\"moles\":5}}]"),
            Request("replace", "editRegion", $"\"min\":{C(1, 0, 0)},\"max\":{C(2, 0, 0)},\"clearGas\":true,\"gas\":[{{\"gasId\":0,\"moles\":1}}],\"gasTemperatureK\":300"),
            Request("sample", "sampleVoxels", $"\"points\":[{C(0, 0, 0)},{C(1, 0, 0)},{C(2, 0, 0)},{C(3, 0, 0)}],\"fields\":[\"totalMoles\"]"));

        var values = run.Result("sample").GetProperty("samples").GetProperty("values");
        Assert.That(
            Enumerable.Range(0, 4).Select(index => GetFloat(values[index][0])),
            Is.EqualTo(new[] { 5f, 1f, 1f, 5f }).Within(1e-4f));
    }

    [Test]
    public async Task SampleVoxels_UnknownFieldAndMissingChunk()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request("unknown", "sampleVoxels", $"\"points\":[{Coordinate0}],\"fields\":[\"density\"]"),
            Request("missing", "sampleVoxels", $"\"points\":[{C(50, 0, 0)}],\"fields\":[\"pressure\",\"classification\"]"));

        var values = run.Result("missing").GetProperty("samples").GetProperty("values")[0];
        Assert.Multiple(() =>
        {
            Assert.That(run.ErrorCode("unknown"), Is.EqualTo("fieldNotFound"));
            Assert.That(float.IsNaN(GetFloat(values[0])), Is.True);
            Assert.That(float.IsNaN(GetFloat(values[1])), Is.True);
        });
    }

    [Test]
    public async Task GasAndConfigOperations_RoundTrip()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addGas", "\"gas\":{\"name\":\"Second\",\"molarHeatCapacityAtConstantVolume\":2}"),
            Request("gases", "listGases"),
            Request("update", "updateConfig", "\"config\":{\"bulkFlowCoefficient\":0.5}"),
            Request("bad", "updateConfig", "\"config\":{\"bulkFlowCoefficient\":-1}"),
            Request("config", "getConfig"),
            Request("remove", "removeGas", "\"gasId\":1"),
            Request("missing", "removeGas", "\"gasId\":7"));

        var config = run.Result("config").GetProperty("config");
        Assert.Multiple(() =>
        {
            Assert.That(run.Result("add").GetProperty("gasId").GetInt32(), Is.EqualTo(1));
            Assert.That(run.Result("gases").GetProperty("gases").GetArrayLength(), Is.EqualTo(2));
            Assert.That(config.GetProperty("bulkFlowCoefficient").GetSingle(), Is.EqualTo(0.5f));
            Assert.That(config.GetProperty("gases").GetArrayLength(), Is.EqualTo(2));
            Assert.That(run.Find("remove").GetProperty("state").GetProperty("gasCount").GetInt32(), Is.EqualTo(1));
            Assert.That(run.ErrorCode("missing"), Is.EqualTo("gasNotFound"));
        });
    }

    [Test]
    public async Task Solvers_ListDisableAndReset()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("list", "listSolvers"),
            Request("off", "setSolverEnabled", "\"solver\":\"boundary-flow\",\"enabled\":false"),
            Request("after", "listSolvers"),
            Request("reset", "resetSolvers"),
            Request("restored", "listSolvers"),
            Request("unknown", "setSolverEnabled", "\"solver\":\"nope\",\"enabled\":false"));

        bool Enabled(string id)
        {
            return run.Result(id).GetProperty("solvers").EnumerateArray()
                .Single(step => step.GetProperty("name").GetString() == "boundary-flow")
                .GetProperty("isEnabled").GetBoolean();
        }

        Assert.Multiple(() =>
        {
            Assert.That(Enabled("list"), Is.True);
            Assert.That(Enabled("after"), Is.False);
            Assert.That(Enabled("restored"), Is.True);
            Assert.That(run.ErrorCode("unknown"), Is.EqualTo("solverNotFound"));
        });
    }

    [Test]
    public async Task Portals_ConnectTwoSimulationsAndAppearInTheLinkList()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("chunk-a", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request("add", "addSimulation", $"\"name\":\"B\",\"chunkDimensions\":{C(2, 1, 1)}"),
            Request("chunk-b", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request(
                "portal",
                "createPortal",
                $"\"first\":{{\"simulation\":0,\"position\":{Coordinate0},\"voxel\":{C(1, 0, 0)}}}," +
                $"\"second\":{{\"simulation\":\"B\",\"position\":{Coordinate0},\"voxel\":{C(0, 0, 0)}}},\"flags\":\"gas\""),
            Request("tick", "tick", "\"count\":1"),
            Request("links", "listLinkSets"),
            Request("destroy", "destroyLinkSet", "\"linkSet\":{\"index\":0,\"generation\":1}"),
            Request("tick-2", "tick", "\"count\":1"),
            Request("after", "listLinkSets"));

        var sets = run.Result("links").GetProperty("linkSets");
        var handle = run.Result("portal").GetProperty("linkSet");

        Assert.Multiple(() =>
        {
            Assert.That(sets.GetArrayLength(), Is.EqualTo(1));
            Assert.That(sets[0].GetProperty("kind").GetString(), Is.EqualTo("Portal"));
            Assert.That(sets[0].GetProperty("linkCount").GetInt32(), Is.EqualTo(1));
            Assert.That(sets[0].GetProperty("flags").GetString(), Is.EqualTo("GasTransport"));
            Assert.That(handle.GetProperty("index").GetInt32(), Is.Zero);
            Assert.That(handle.GetProperty("generation").GetUInt32(), Is.EqualTo(1));
            Assert.That(run.Find("destroy").GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(run.Result("after").GetProperty("linkSets").GetArrayLength(), Is.Zero);
        });
    }

    [Test]
    public async Task Timeline_SeekReturnBranchAndInfo()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request("tick", "tick", "\"count\":6"),
            Request("live", "timelineInfo"),
            Request("seek", "seekTick", "\"tick\":2"),
            Request("inspect", "timelineInfo"),
            Request("blocked", "tick", "\"count\":1"),
            Request("step", "stepForward"),
            Request("branch", "createBranch"),
            Request("select", "selectBranch", "\"branchId\":0"),
            Request("missing", "selectBranch", "\"branchId\":99"),
            Request("head", "returnToHead"),
            Request("back", "timelineInfo"),
            Request("continue", "continueBranch"),
            Request("resumed", "timelineInfo"),
            Request("branches", "listBranches"));

        var live = run.Result("live").GetProperty("timeline");
        var inspect = run.Result("inspect").GetProperty("timeline");
        var back = run.Result("back").GetProperty("timeline");
        Assert.Multiple(() =>
        {
            Assert.That(live.GetProperty("headTick").GetInt32(), Is.EqualTo(6));
            Assert.That(live.GetProperty("inspecting").GetBoolean(), Is.False);
            Assert.That(inspect.GetProperty("inspecting").GetBoolean(), Is.True);
            Assert.That(inspect.GetProperty("positionTick").GetInt32(), Is.EqualTo(2));
            Assert.That(inspect.GetProperty("headTick").GetInt32(), Is.EqualTo(6));
            Assert.That(inspect.GetProperty("stateHash").GetString(), Is.Not.EqualTo(live.GetProperty("stateHash").GetString()));
            Assert.That(run.ErrorCode("blocked"), Is.EqualTo("operationRejected"));
            Assert.That(run.Result("step").GetProperty("moved").GetString(), Is.EqualTo("Seeked"));
            Assert.That(run.Result("branch").GetProperty("branch").GetProperty("forkTick").GetInt32(), Is.EqualTo(3));
            Assert.That(run.ErrorCode("missing"), Is.EqualTo("branchNotFound"));
            Assert.That(back.GetProperty("positionTick").GetInt32(), Is.EqualTo(6));
            Assert.That(run.Result("resumed").GetProperty("timeline").GetProperty("inspecting").GetBoolean(), Is.False);
            Assert.That(back.GetProperty("checkpoints").GetArrayLength(), Is.GreaterThan(0));
            Assert.That(run.Result("branches").GetProperty("branches").GetArrayLength(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Replay_SaveThenLoadRestoresTheSameStateHash()
    {
        string path = PathFor("shock.numos");
        var save = await RunAsync(
            CreateProject("create", 4, 1, 1),
            Request(
                "fill",
                "editRegion",
                $"\"min\":{C(0, 0, 0)},\"max\":{C(7, 0, 0)},\"createChunks\":true,\"classification\":1,\"temperatureK\":300,\"gas\":[{{\"gasId\":0,\"partialPressurePa\":3000}}],\"gasTemperatureK\":300"),
            Request("gas", "editRegion", $"\"min\":{C(0, 0, 0)},\"max\":{C(3, 0, 0)},\"gas\":[{{\"gasId\":0,\"partialPressurePa\":3000}}],\"gasTemperatureK\":300"),
            Request("tick", "tick", "\"count\":10"),
            Request("info", "timelineInfo"),
            Request("save", "saveReplay", $"\"path\":\"{path}\""),
            Request("again", "saveReplay", $"\"path\":\"{path}\""),
            Request("overwrite", "saveReplay", $"\"path\":\"{path}\",\"overwrite\":true"));

        var load = await RunAsync(
            Request("load", "loadReplay", $"\"path\":\"{path}\""),
            Request("start", "timelineInfo"),
            Request("head", "returnToHead"),
            Request("info", "timelineInfo"),
            Request("continue", "continueBranch"),
            Request("tick", "tick", "\"count\":1"));

        string savedHash = save.Result("info").GetProperty("timeline").GetProperty("stateHash").GetString()!;
        string loadedHash = load.Result("info").GetProperty("timeline").GetProperty("stateHash").GetString()!;
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(save.Result("save").GetProperty("path").GetString(), Does.EndWith("shock.numos"));
            Assert.That(save.ErrorCode("again"), Is.EqualTo("ioError"));
            Assert.That(loadedHash, Is.EqualTo(savedHash));
            Assert.That(load.Find("load").GetProperty("state").GetProperty("tick").GetInt32(), Is.Zero);
            Assert.That(load.Result("start").GetProperty("timeline").GetProperty("headTick").GetInt32(), Is.EqualTo(10));
            Assert.That(load.Find("head").GetProperty("state").GetProperty("tick").GetInt32(), Is.EqualTo(10));
            Assert.That(load.Find("tick").GetProperty("state").GetProperty("tick").GetInt32(), Is.EqualTo(11));
        });
    }

    [Test]
    public async Task Observe_ReportsTheStateHashOfTheAddressedSimulation()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("add", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"),
            Request("one", "observe"),
            Request("tick", "tick", "\"count\":1"),
            Request("two", "observe"),
            Request("missing", "observe", "\"simulation\":\"Nope\""));

        Assert.Multiple(() =>
        {
            Assert.That(run.Result("one").GetProperty("stateHash").GetString(), Has.Length.EqualTo(16));
            Assert.That(
                run.Result("two").GetProperty("stateHash").GetString(),
                Is.Not.EqualTo(run.Result("one").GetProperty("stateHash").GetString()));

            Assert.That(run.ErrorCode("missing"), Is.EqualTo("simulationNotFound"));
        });
    }
}
