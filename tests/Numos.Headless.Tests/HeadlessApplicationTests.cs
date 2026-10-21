using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Numos.Headless.Tests;

[TestFixture]
public sealed class HeadlessApplicationTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public async Task JsonlSession_DeterministicTwoCellFlow_ReportsExpectedGasMovement()
    {
        // The 1.5 / 0.5 split is the legacy pressure-relaxation law's one-tick answer.
        var run = await RunAsync(
            CreateProjectRequest("create", 2, 1, 1, "legacy"),
            Request(
                "chunk",
                "addChunk",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0},\"classification\":1"),
            SetTemperatureRequest("temperature-0", 0, 300f),
            SetTemperatureRequest("temperature-1", 1, 300f),
            Request(
                "inject",
                "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"gasId\":0,\"moles\":2,\"temperatureK\":300"),
            Request("tick", "tick", "\"count\":1"),
            Request("observe", "observe", "\"includeVoxels\":true"),
            Request("exit", "exit"));

        var observationResponse = FindResponse(run.Responses, "observe");
        var observation = observationResponse.GetProperty("observation");
        var voxels = FindArraysNamed(observation, "voxels").Single();
        float[] gasMoles = voxels.EnumerateArray()
            .Select(ReadGasZeroMoles)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.Zero);
            Assert.That(observationResponse.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(
                observationResponse.GetProperty("state").GetProperty("tick").GetInt32(),
                Is.EqualTo(1));

            Assert.That(gasMoles, Has.Length.EqualTo(2));
            Assert.That(gasMoles[0], Is.EqualTo(1.5f).Within(Tolerance));
            Assert.That(gasMoles[1], Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(gasMoles.Sum(), Is.EqualTo(2f).Within(Tolerance));
        });
    }

    [Test]
    public async Task JsonlSession_EulerTube_MovesGasTowardTheLowPressureHalf()
    {
        // SI air at 1 m³ per voxel: 80 mol is about 2 atm at 300 K, 40 mol about 1 atm. Eight voxels rather than
        // two so the tube is still moving when the tick ends; a two-cell tube rings down inside one tick.
        List<string> requests =
        [
            Request(
                "create",
                "createProject",
                "\"chunkDimensions\":{\"x\":8,\"y\":1,\"z\":1}," +
                "\"config\":{\"voxelVolumeM3\":1,\"defaultDiffusionCoefficient\":0,\"vacuumThresholdPa\":0," +
                "\"sleepThreshold\":2147483647,\"sleepEpsilonPercent\":0}," +
                "\"gases\":[{\"name\":\"Air\",\"molarHeatCapacityAtConstantVolume\":20.786," +
                "\"molarMassKgPerMol\":0.02897}]"),
            AddChunkRequest("chunk", 0, 0, 0)
        ];

        for (int x = 0; x < 8; x++)
        {
            requests.Add(Request(
                $"inject-{x}",
                "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                $"\"voxel\":{{\"x\":{x},\"y\":0,\"z\":0}}," +
                $"\"gasId\":0,\"moles\":{(x < 4 ? 80 : 40)},\"temperatureK\":300"));
        }

        requests.Add(Request("before", "observe"));
        requests.Add(Request("tick", "tick", "\"count\":1"));
        requests.Add(Request("observe", "observe", "\"includeVoxels\":true"));
        requests.Add(Request("exit", "exit"));
        var run = await RunAsync(requests.ToArray());

        var before = FindResponse(run.Responses, "before").GetProperty("observation");
        var observation = FindResponse(run.Responses, "observe").GetProperty("observation");
        var flow = observation.GetProperty("flow");
        var voxels = FindArraysNamed(observation, "voxels").Single().EnumerateArray().ToArray();
        float[] gasMoles = voxels.Select(ReadGasZeroMoles).ToArray();
        float[] speeds = voxels.Select(static voxel => voxel.GetProperty("speedMps").GetSingle()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.Zero);
            Assert.That(
                observation.GetProperty("solverPipeline")[0].GetProperty("name").GetString(),
                Is.EqualTo("euler-flow"));

            Assert.That(before.GetProperty("flow").GetProperty("substepsLastTick").GetInt32(), Is.Zero);
            Assert.That(gasMoles, Has.Length.EqualTo(8));
            Assert.That(gasMoles.Sum(), Is.EqualTo(480f).Within(0.01f));
            Assert.That(gasMoles[0], Is.LessThan(80f));
            Assert.That(gasMoles[7], Is.GreaterThan(40f));
            Assert.That(speeds.Max(), Is.GreaterThan(0f));
            Assert.That(flow.GetProperty("substepsLastTick").GetInt32(), Is.GreaterThanOrEqualTo(1));
            Assert.That(flow.GetProperty("maxSignalSpeedMps").GetSingle(), Is.GreaterThan(0f));
            Assert.That(flow.GetProperty("substepCapEngaged").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task CreateProject_UnknownTransportModel_IsRejected()
    {
        var run = await RunAsync(
            CreateProjectRequest("create", 1, 1, 1, "lattice-boltzmann"),
            Request("exit", "exit"));

        var response = FindResponse(run.Responses, "create");

        Assert.Multiple(() =>
        {
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(
                response.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("invalidTransportModel"));
        });
    }

    [Test]
    public async Task UpdateConfig_EulerFlowPatch_ReplacesOnlyTheGivenSettings()
    {
        var run = await RunAsync(
            CreateProjectRequest("create", 1, 1, 1),
            Request("before", "observe"),
            Request(
                "patch",
                "updateConfig",
                "\"config\":{\"eulerFlow\":{\"courantNumber\":0.4,\"reconstruction\":\"musclHancock\"}}"),
            Request(
                "patch-again",
                "updateConfig",
                "\"config\":{\"eulerFlow\":{\"maxSubstepsPerTick\":0,\"allMachCorrection\":true}}"),
            Request("after", "observe"),
            Request(
                "bad",
                "updateConfig",
                "\"config\":{\"eulerFlow\":{\"reconstruction\":\"spectral\"}}"),
            Request("exit", "exit"));

        var before = FindResponse(run.Responses, "before").GetProperty("observation").GetProperty("config");
        var after = FindResponse(run.Responses, "after").GetProperty("observation").GetProperty("config");
        var euler = after.GetProperty("eulerFlow");
        var bad = FindResponse(run.Responses, "bad");

        Assert.Multiple(() =>
        {
            Assert.That(before.TryGetProperty("eulerFlow", out _), Is.False);
            Assert.That(euler.GetProperty("courantNumber").GetSingle(), Is.EqualTo(0.4f).Within(Tolerance));
            Assert.That(euler.GetProperty("reconstruction").GetString(), Is.EqualTo("musclHancock"));
            Assert.That(euler.GetProperty("maxSubstepsPerTick").GetInt32(), Is.Zero);
            Assert.That(euler.GetProperty("allMachCorrection").GetBoolean(), Is.True);
            Assert.That(euler.GetProperty("maxVelocityMps").GetSingle(), Is.EqualTo(5000f));
            Assert.That(bad.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(bad.GetProperty("error").GetProperty("code").GetString(), Is.EqualTo("invalidConfig"));
        });
    }

    [Test]
    public async Task JsonlSession_MalformedJson_ReturnsStructuredErrorAndProcessesNextLine()
    {
        var run = await RunAsync(
            "{\"protocolVersion\":2,\"id\":\"broken\",\"op\":",
            CreateProjectRequest("recovered", 1, 1, 1),
            Request("exit", "exit"));

        var errorResponse = run.Responses.Single(response =>
            response.TryGetProperty("ok", out var ok) && !ok.GetBoolean());

        var error = errorResponse.GetProperty("error");
        var recovered = FindResponse(run.Responses, "recovered");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(error.GetProperty("code").GetString(), Is.EqualTo("invalidJson"));
            Assert.That(error.GetProperty("message").GetString(), Is.Not.Null.And.Not.Empty);
            Assert.That(error.GetProperty("line").GetInt32(), Is.EqualTo(1));
            Assert.That(recovered.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(recovered.GetProperty("state").GetProperty("chunkCount").GetInt32(), Is.Zero);
            Assert.That(run.Responses.All(IsValidProtocolResponse), Is.True);
            Assert.That(run.StandardError, Is.Empty);
        });
    }

    [Test]
    public async Task JsonlSession_SchemaErrors_ReturnCorrelatedRequestErrors()
    {
        var run = await RunAsync(
            Request(
                "typo",
                "createProject",
                "\"chunkDimensions\":{\"x\":1,\"y\":1,\"z\":1},\"widht\":1"),
            "{\"id\":\"no-version\",\"op\":\"observe\"}",
            Request("exit", "exit"));

        var response = FindResponse(run.Responses, "typo");
        var missingVersion = FindResponse(run.Responses, "no-version");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(response.GetProperty("op").GetString(), Is.EqualTo("createProject"));
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(
                response.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("invalidRequest"));

            Assert.That(missingVersion.GetProperty("op").GetString(), Is.EqualTo("observe"));
            Assert.That(
                missingVersion.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("missingProperty"));
        });
    }

    [Test]
    public async Task JsonlSession_IncompleteCoordinate_IsRejectedWithoutMutatingState()
    {
        var run = await RunAsync(
            CreateProjectRequest("create", 1, 1, 1),
            Request("incomplete", "addChunk", "\"position\":{\"x\":7},\"classification\":1"),
            Request("observe", "observe"),
            Request("exit", "exit"));

        var rejected = FindResponse(run.Responses, "incomplete");
        var observation = FindResponse(run.Responses, "observe");

        Assert.Multiple(() =>
        {
            Assert.That(rejected.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(
                rejected.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("invalidRequest"));

            Assert.That(observation.GetProperty("state").GetProperty("chunkCount").GetInt32(), Is.Zero);
            Assert.That(observation.GetProperty("observation").GetProperty("chunks").GetArrayLength(), Is.Zero);
        });
    }

    [Test]
    public async Task SetVoxelMomentum_Observe_ReportsDerivedVelocityWithoutTicking()
    {
        var run = await RunAsync(
            HeadlessTestSupport.CreateProject("create", 2, 1, 1, "\"referenceMolarMassKgPerMol\":0.01"),
            AddChunkRequest("chunk", 0, 0, 0),
            Request(
                "inject",
                "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"gasId\":0,\"moles\":2,\"temperatureK\":300"),
            // 2 mol × 0.01 kg/mol = 0.02 kg, so (0.06, 0, -0.08) kg·m/s is (3, 0, -4) m/s.
            Request(
                "momentum",
                "setVoxelMomentum",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"momentumX\":0.06,\"momentumY\":0,\"momentumZ\":-0.08"),
            Request(
                "observe",
                "observe",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0},\"voxel\":{\"x\":0,\"y\":0,\"z\":0}"),
            Request("exit", "exit"));

        var momentumResponse = FindResponse(run.Responses, "momentum");
        var observationResponse = FindResponse(run.Responses, "observe");
        var observation = observationResponse.GetProperty("observation");
        var chunk = observation.GetProperty("chunks")[0];
        var voxel = chunk.GetProperty("voxels")[0];
        var velocity = voxel.GetProperty("velocityMps");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.Zero);
            Assert.That(momentumResponse.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(observationResponse.GetProperty("state").GetProperty("tick").GetInt32(), Is.Zero);
            Assert.That(velocity.GetProperty("x").GetSingle(), Is.EqualTo(3f).Within(Tolerance));
            Assert.That(velocity.GetProperty("y").GetSingle(), Is.EqualTo(0f).Within(Tolerance));
            Assert.That(velocity.GetProperty("z").GetSingle(), Is.EqualTo(-4f).Within(Tolerance));
            Assert.That(voxel.GetProperty("speedMps").GetSingle(), Is.EqualTo(5f).Within(Tolerance));
            Assert.That(
                chunk.GetProperty("summary").GetProperty("speedMps").GetProperty("maximum").GetDouble(),
                Is.EqualTo(5d).Within(Tolerance));

            Assert.That(
                observation.GetProperty("global").GetProperty("speedMps").GetProperty("finiteCount").GetInt32(),
                Is.EqualTo(2));

            Assert.That(
                observation.GetProperty("global").GetProperty("anomalies").GetProperty("nonFiniteVelocityCount").GetInt32(),
                Is.Zero);
        });
    }

    [Test]
    public async Task InjectGas_UnregisteredGasId_IsRejected()
    {
        var run = await RunAsync(
            CreateProjectRequest("create", 1, 1, 1),
            AddChunkRequest("chunk", 0, 0, 0),
            Request(
                "inject",
                "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"gasId\":999,\"moles\":1,\"temperatureK\":300"),
            Request("exit", "exit"));

        var response = FindResponse(run.Responses, "inject");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(
                response.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("gasNotFound"));

            Assert.That(response.GetProperty("state").GetProperty("gasCount").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Program_StdinProtocol_WritesParseableResponsesAndExitsCleanly()
    {
        string applicationPath = Path.Combine(AppContext.BaseDirectory, "Numos.Headless.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(applicationPath);
        startInfo.ArgumentList.Add("--quiet");

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Could not start the Headless process.");

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(CreateProjectRequest("process-create", 1, 1, 1));
        await process.StandardInput.WriteLineAsync(Request("process-exit", "exit"));
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        string output = await standardOutput;
        string error = await standardError;
        JsonElement[] responses = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseJson)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.Zero);
            Assert.That(error, Is.Empty);
            Assert.That(responses, Has.Length.EqualTo(2));
            Assert.That(FindResponse(responses, "process-create").GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(FindResponse(responses, "process-exit").GetProperty("ok").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Application_InvalidScriptPath_ReturnsStructuredSetupError()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await HeadlessApplication.RunAsync(
            ["\0"],
            input,
            output,
            error,
            CancellationToken.None);

        var response = ParseJson(output.ToString().Trim());

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(
                response.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("scriptUnavailable"));
        });
    }

    [Test]
    public async Task Observe_NonFiniteTemperature_EmitsValidJsonWithNamedValue()
    {
        var run = await RunAsync(
            CreateProjectRequest("create", 1, 1, 1),
            Request(
                "chunk",
                "addChunk",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0},\"classification\":1"),
            Request(
                "temperature",
                "setVoxelTemperature",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0},\"temperatureK\":\"NaN\""),
            Request("observe", "observe", "\"includeVoxels\":true"),
            Request("exit", "exit"));

        var observationResponse = FindResponse(run.Responses, "observe");
        var observation = observationResponse.GetProperty("observation");
        var voxel = observation.GetProperty("chunks")[0].GetProperty("voxels")[0];
        JsonElement[] namedNonfiniteValues = DescendantsAndSelf(observation)
            .Where(element => element.ValueKind == JsonValueKind.String && element.GetString() == "NaN")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(FindResponse(run.Responses, "temperature").GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(observationResponse.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(voxel.GetProperty("temperatureK").GetString(), Is.EqualTo("NaN"));
            Assert.That(
                observation.GetProperty("global").GetProperty("anomalies")
                    .GetProperty("nonFiniteTemperatureCount").GetInt32(),
                Is.EqualTo(1));

            Assert.That(
                namedNonfiniteValues,
                Is.Not.Empty,
                "The diagnostic response must remain serializable when a stored simulation value is NaN.");

            Assert.That(run.OutputLines.All(IsValidJson), Is.True);
        });
    }

    [Test]
    public async Task Observe_ChunksCreatedOutOfOrder_ReturnsStableCoordinateOrder()
    {
        var run = await RunAsync(
            CreateProjectRequest("create", 1, 1, 1),
            AddChunkRequest("chunk-2", 2, 0, 0),
            AddChunkRequest("chunk-negative-later", -1, 4, 0),
            AddChunkRequest("chunk-negative-first", -1, 3, 5),
            Request("observe", "observe", "\"includeVoxels\":false"),
            Request("exit", "exit"));

        var observation = FindResponse(run.Responses, "observe").GetProperty("observation");
        var chunks = FindArraysNamed(observation, "chunks").Single();
        (int X, int Y, int Z)[] positions = chunks.EnumerateArray()
            .Select(chunk => ReadCoordinate(chunk.GetProperty("position")))
            .ToArray();

        Assert.That(
            positions,
            Is.EqualTo(
                new[]
                {
                    (-1, 3, 5),
                    (-1, 4, 0),
                    (2, 0, 0)
                }));
    }

    private static string CreateProjectRequest(
        string id, int width, int height, int depth, string? transportModel = null)
    {
        string model = transportModel == null ? string.Empty : $"\"transportModel\":\"{transportModel}\",";
        return Request(
            id,
            "createProject",
            $"\"name\":\"Headless tests\"," +
            model +
            $"\"chunkDimensions\":{{\"x\":{width},\"y\":{height},\"z\":{depth}}}," +
            "\"config\":{" +
            "\"defaultTemperatureFallbackK\":300," +
            "\"defaultMolarHeatCapacityAtConstantVolume\":1," +
            "\"voxelVolumeM3\":8.31446262," +
            "\"saturationReferencePressurePa\":1000," +
            "\"defaultDiffusionCoefficient\":0," +
            "\"bulkFlowCoefficient\":0.25," +
            "\"vacuumThresholdPa\":0," +
            "\"sleepThreshold\":2147483647," +
            "\"sleepEpsilonPercent\":0," +
            "\"thermalConductance\":0.05," +
            "\"condensationRateFactor\":0.5," +
            "\"maxPressureTransferFractionPerNeighbor\":0.16" +
            "}," +
            "\"gases\":[{" +
            "\"name\":\"First\"," +
            "\"molarHeatCapacityAtConstantVolume\":1," +
            "\"diffusionCoefficient\":0" +
            "}]");
    }

    private static string AddChunkRequest(string id, int x, int y, int z)
    {
        return Request(
            id,
            "addChunk",
            $"\"position\":{{\"x\":{x},\"y\":{y},\"z\":{z}}},\"classification\":1");
    }

    private static string SetTemperatureRequest(string id, int x, float temperature)
    {
        return Request(
            id,
            "setVoxelTemperature",
            "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
            $"\"voxel\":{{\"x\":{x},\"y\":0,\"z\":0}}," +
            $"\"temperatureK\":{temperature.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string Request(string id, string op, string? payload = null)
    {
        string suffix = string.IsNullOrEmpty(payload) ? string.Empty : $",{payload}";
        return $"{{\"protocolVersion\":2,\"id\":\"{id}\",\"op\":\"{op}\"{suffix}}}";
    }

    private static Task<RunResult> RunAsync(params string[] requests)
    {
        return RunWithArgsAsync(["--quiet"], requests);
    }

    private static async Task<RunResult> RunWithArgsAsync(string[] args, params string[] requests)
    {
        string inputText = string.Join('\n', requests) + "\n";
        using var input = new StringReader(inputText);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await HeadlessApplication.RunAsync(
            args,
            input,
            output,
            error,
            CancellationToken.None);

        string[] lines = output.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        JsonElement[] responses = lines.Select(ParseJson).ToArray();
        return new RunResult(exitCode, lines, responses, error.ToString());
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement FindResponse(IEnumerable<JsonElement> responses, string id)
    {
        return responses.Single(response =>
            response.TryGetProperty("id", out var responseId) && responseId.GetString() == id);
    }

    private static float ReadGasZeroMoles(JsonElement voxel)
    {
        if (!voxel.TryGetProperty("gases", out var gases))
            return 0f;

        foreach (var gas in gases.EnumerateArray())
        {
            if (gas.GetProperty("gasId").GetInt32() == 0)
                return gas.GetProperty("moles").GetSingle();
        }

        return 0f;
    }

    private static (int X, int Y, int Z) ReadCoordinate(JsonElement coordinate)
    {
        return (
            coordinate.GetProperty("x").GetInt32(),
            coordinate.GetProperty("y").GetInt32(),
            coordinate.GetProperty("z").GetInt32());
    }

    private static IEnumerable<JsonElement> FindArraysNamed(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.Array)
                    yield return property.Value;

                foreach (var match in FindArraysNamed(property.Value, propertyName))
                    yield return match;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            foreach (var match in FindArraysNamed(item, propertyName))
                yield return match;
        }
    }

    private static IEnumerable<JsonElement> DescendantsAndSelf(JsonElement root)
    {
        yield return root;

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            foreach (var descendant in DescendantsAndSelf(property.Value))
                yield return descendant;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            foreach (var descendant in DescendantsAndSelf(item))
                yield return descendant;
        }
    }

    private static bool IsValidProtocolResponse(JsonElement response)
    {
        return response.ValueKind == JsonValueKind.Object &&
               response.TryGetProperty("protocolVersion", out var version) &&
               version.GetInt32() == 2 &&
               response.TryGetProperty("ok", out _);
    }

    private static bool IsValidJson(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record RunResult(
        int ExitCode,
        string[] OutputLines,
        JsonElement[] Responses,
        string StandardError);
}