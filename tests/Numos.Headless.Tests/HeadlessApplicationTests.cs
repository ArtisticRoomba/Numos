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
        var run = await RunAsync(
            CreateSimulationRequest("create", 2, 1, 1),
            Request("chunk", "addChunk",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0},\"classification\":1"),
            SetTemperatureRequest("temperature-0", 0, 300f),
            SetTemperatureRequest("temperature-1", 1, 300f),
            Request("inject", "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"gasId\":0,\"moles\":2,\"temperatureK\":300"),
            Request("tick", "tick", "\"count\":1"),
            Request("observe", "observe", "\"includeVoxels\":true"),
            Request("exit", "exit"));

        JsonElement observationResponse = FindResponse(run.Responses, "observe");
        JsonElement observation = observationResponse.GetProperty("observation");
        JsonElement voxels = FindArraysNamed(observation, "voxels").Single();
        float[] gasMoles = voxels.EnumerateArray()
            .Select(ReadGasZeroMoles)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.Zero);
            Assert.That(observationResponse.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(observationResponse.GetProperty("state").GetProperty("tick").GetInt32(),
                Is.EqualTo(1));
            Assert.That(gasMoles, Has.Length.EqualTo(2));
            Assert.That(gasMoles[0], Is.EqualTo(1.75f).Within(Tolerance));
            Assert.That(gasMoles[1], Is.EqualTo(0.25f).Within(Tolerance));
            Assert.That(gasMoles.Sum(), Is.EqualTo(2f).Within(Tolerance));
        });
    }

    [Test]
    public async Task UpdateConfig_VoxelSnappingFields_RoundTripThroughObservation()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            Request("update", "updateConfig",
                "\"config\":{" +
                "\"sleepEpsilonPa\":0.75," +
                "\"voxelSnapPressureRelativeEpsilon\":0.375," +
                "\"voxelSnappingEnabled\":true," +
                "\"voxelSnapTemperatureEpsilonK\":0.125," +
                "\"voxelSnapMoleFractionEpsilon\":0.25}"),
            Request("observe", "observe"),
            Request("exit", "exit"));

        JsonElement updateResponse = FindResponse(run.Responses, "update");
        JsonElement config = FindResponse(run.Responses, "observe")
            .GetProperty("observation")
            .GetProperty("config");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.Zero);
            Assert.That(updateResponse.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(config.GetProperty("sleepEpsilonPa").GetSingle(), Is.EqualTo(0.75f));
            Assert.That(config.GetProperty("voxelSnapPressureRelativeEpsilon").GetSingle(),
                Is.EqualTo(0.375f));
            Assert.That(config.GetProperty("voxelSnappingEnabled").GetBoolean(), Is.True);
            Assert.That(config.GetProperty("voxelSnapTemperatureEpsilonK").GetSingle(), Is.EqualTo(0.125f));
            Assert.That(config.GetProperty("voxelSnapMoleFractionEpsilon").GetSingle(), Is.EqualTo(0.25f));
        });
    }

    [Test]
    public async Task JsonlSession_MalformedJson_ReturnsStructuredErrorAndProcessesNextLine()
    {
        var run = await RunAsync(
            "{\"protocolVersion\":1,\"id\":\"broken\",\"op\":",
            CreateSimulationRequest("recovered", 1, 1, 1),
            Request("exit", "exit"));

        JsonElement errorResponse = run.Responses.Single(response =>
            response.TryGetProperty("ok", out var ok) && !ok.GetBoolean());
        JsonElement error = errorResponse.GetProperty("error");
        JsonElement recovered = FindResponse(run.Responses, "recovered");

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
            Request("typo", "createSimulation",
                "\"dimensions\":{\"x\":1,\"y\":1,\"z\":1},\"widht\":1"),
            "{\"id\":\"no-version\",\"op\":\"observe\"}",
            Request("exit", "exit"));

        JsonElement response = FindResponse(run.Responses, "typo");
        JsonElement missingVersion = FindResponse(run.Responses, "no-version");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(response.GetProperty("op").GetString(), Is.EqualTo("createSimulation"));
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(response.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("invalidRequest"));
            Assert.That(missingVersion.GetProperty("op").GetString(), Is.EqualTo("observe"));
            Assert.That(missingVersion.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("missingProperty"));
        });
    }

    [Test]
    public async Task JsonlSession_FieldFromAnotherOperationIsRejectedWithoutMutation()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            Request("wrong-field", "addChunk",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0},\"roomId\":7"),
            Request("observe", "observe"),
            Request("exit", "exit"));

        JsonElement rejected = FindResponse(run.Responses, "wrong-field");
        JsonElement observation = FindResponse(run.Responses, "observe");
        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(rejected.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(rejected.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("invalidRequest"));
            Assert.That(observation.GetProperty("state").GetProperty("chunkCount").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public async Task AddSolidChunk_DoesNotTransientlyWakeSleepingAdjacentSource()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            AddChunkRequest("source", 0, 0, 0),
            Request("inject", "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"gasId\":0,\"moles\":1,\"temperatureK\":300"),
            Request("sleep", "sleepChunk", "\"position\":{\"x\":0,\"y\":0,\"z\":0}"),
            Request("solid", "addChunk",
                "\"position\":{\"x\":1,\"y\":0,\"z\":0},\"classification\":-2"),
            Request("observe", "observe", "\"position\":{\"x\":0,\"y\":0,\"z\":0}"),
            Request("exit", "exit"));

        JsonElement source = FindResponse(run.Responses, "observe")
            .GetProperty("observation")
            .GetProperty("chunks")
            .EnumerateArray()
            .Single();
        Assert.Multiple(() =>
        {
            Assert.That(FindResponse(run.Responses, "solid").GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(source.GetProperty("isAwake").GetBoolean(), Is.False);
            Assert.That(source.GetProperty("summary").GetProperty("totalMoles").GetDouble(),
                Is.EqualTo(1d).Within(0.000001d));
        });
    }

    [Test]
    public async Task JsonlSession_IncompleteCoordinate_IsRejectedWithoutMutatingState()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            Request("incomplete", "addChunk", "\"position\":{\"x\":7},\"classification\":1"),
            Request("observe", "observe"),
            Request("exit", "exit"));

        JsonElement rejected = FindResponse(run.Responses, "incomplete");
        JsonElement observation = FindResponse(run.Responses, "observe");

        Assert.Multiple(() =>
        {
            Assert.That(rejected.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(rejected.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("invalidRequest"));
            Assert.That(observation.GetProperty("state").GetProperty("chunkCount").GetInt32(), Is.Zero);
            Assert.That(observation.GetProperty("observation").GetProperty("chunks").GetArrayLength(), Is.Zero);
        });
    }

    [Test]
    public async Task InjectGas_UnregisteredGasId_IsRejected()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            AddChunkRequest("chunk", 0, 0, 0),
            Request("inject", "injectGas",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"gasId\":999,\"moles\":1,\"temperatureK\":300"),
            Request("exit", "exit"));

        JsonElement response = FindResponse(run.Responses, "inject");

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(response.GetProperty("error").GetProperty("code").GetString(),
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

        using Process process = Process.Start(startInfo) ??
                                throw new InvalidOperationException("Could not start the Headless process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(CreateSimulationRequest("process-create", 1, 1, 1));
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
        JsonElement response = ParseJson(output.ToString().Trim());

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(response.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("scriptUnavailable"));
        });
    }

    [Test]
    public async Task Observe_NonFiniteTemperature_EmitsValidJsonWithNamedValue()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            Request("chunk", "addChunk",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0},\"classification\":1"),
            Request("temperature", "setVoxelTemperature",
                "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
                "\"voxel\":{\"x\":0,\"y\":0,\"z\":0},\"temperatureK\":\"NaN\""),
            Request("observe", "observe", "\"includeVoxels\":true"),
            Request("exit", "exit"));

        JsonElement observationResponse = FindResponse(run.Responses, "observe");
        JsonElement observation = observationResponse.GetProperty("observation");
        JsonElement voxel = observation.GetProperty("chunks")[0].GetProperty("voxels")[0];
        JsonElement[] namedNonfiniteValues = DescendantsAndSelf(observation)
            .Where(element => element.ValueKind == JsonValueKind.String && element.GetString() == "NaN")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(FindResponse(run.Responses, "temperature").GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(observationResponse.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(voxel.GetProperty("temperatureK").GetString(), Is.EqualTo("NaN"));
            Assert.That(observation.GetProperty("global").GetProperty("anomalies")
                .GetProperty("nonFiniteTemperatureCount").GetInt32(), Is.EqualTo(1));
            Assert.That(namedNonfiniteValues, Is.Not.Empty,
                "The diagnostic response must remain serializable when a stored simulation value is NaN.");
            Assert.That(run.OutputLines.All(IsValidJson), Is.True);
        });
    }

    [Test]
    public async Task Observe_ChunksCreatedOutOfOrder_ReturnsStableCoordinateOrder()
    {
        var run = await RunAsync(
            CreateSimulationRequest("create", 1, 1, 1),
            AddChunkRequest("chunk-2", 2, 0, 0),
            AddChunkRequest("chunk-negative-later", -1, 4, 0),
            AddChunkRequest("chunk-negative-first", -1, 3, 5),
            Request("observe", "observe", "\"includeVoxels\":false"),
            Request("exit", "exit"));

        JsonElement observation = FindResponse(run.Responses, "observe").GetProperty("observation");
        JsonElement chunks = FindArraysNamed(observation, "chunks").Single();
        (int X, int Y, int Z)[] positions = chunks.EnumerateArray()
            .Select(chunk => ReadCoordinate(chunk.GetProperty("position")))
            .ToArray();

        Assert.That(positions, Is.EqualTo(new[]
        {
            (-1, 3, 5),
            (-1, 4, 0),
            (2, 0, 0)
        }));
    }

    [Test]
    public async Task CheckedInEquilibriumScript_Sample900IsSleepingAndNearUniform()
    {
        string scriptPath = FindRepositoryFile("examples", "headless", "16x16-equilibrium.jsonl");
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await HeadlessApplication.RunAsync(
            [scriptPath], input, output, error, CancellationToken.None);
        JsonElement[] responses = output.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseJson)
            .ToArray();
        JsonElement response = FindResponse(responses, "sample-900");
        JsonElement observation = response.GetProperty("observation");
        JsonElement global = observation.GetProperty("global");
        JsonElement chunk = observation.GetProperty("chunks").EnumerateArray().Single();
        JsonElement pressure = global.GetProperty("pressurePa");
        JsonElement temperature = global.GetProperty("temperatureK");
        float pressureSpread = pressure.GetProperty("maximum").GetSingle() -
                               pressure.GetProperty("minimum").GetSingle();
        float temperatureSpread = temperature.GetProperty("maximum").GetSingle() -
                                  temperature.GetProperty("minimum").GetSingle();

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(response.GetProperty("state").GetProperty("tick").GetInt32(),
                Is.EqualTo(900));
            Assert.That(global.GetProperty("awakeChunkCount").GetInt32(), Is.Zero);
            Assert.That(global.GetProperty("sleepingChunkCount").GetInt32(), Is.EqualTo(1));
            Assert.That(chunk.GetProperty("isAwake").GetBoolean(), Is.False);
            Assert.That(chunk.GetProperty("sleepTimer").GetInt32(),
                Is.GreaterThan(observation.GetProperty("config").GetProperty("sleepThreshold").GetInt32()));
            Assert.That(global.GetProperty("gasBearingVoxelCount").GetInt32(), Is.EqualTo(256));
            Assert.That(pressureSpread,
                Is.LessThanOrEqualTo(observation.GetProperty("config")
                    .GetProperty("sleepEpsilonPa").GetSingle()));
            Assert.That(temperatureSpread,
                Is.LessThanOrEqualTo(observation.GetProperty("config")
                    .GetProperty("voxelSnapTemperatureEpsilonK").GetSingle()));
            Assert.That(global.GetProperty("totalMoles").GetDouble(), Is.InRange(99d, 100d));
            Assert.That(global.GetProperty("anomalies").GetProperty("totalCount").GetInt32(), Is.Zero);
        });
    }

    private static string CreateSimulationRequest(string id, int width, int height, int depth)
    {
        return Request(id, "createSimulation",
            $"\"name\":\"Headless tests\"," +
            $"\"dimensions\":{{\"x\":{width},\"y\":{height},\"z\":{depth}}}," +
            "\"config\":{" +
            "\"defaultTemperatureFallbackK\":300," +
            "\"defaultMolarHeatCapacityAtConstantVolume\":1," +
            "\"voxelVolumeM3\":8.31446262," +
            "\"saturationReferencePressurePa\":1000," +
            "\"defaultDiffusionCoefficient\":0," +
            "\"bulkFlowCoefficient\":0.25," +
            "\"bulkFlowDamping\":0.5," +
            "\"lowPressureDeltaThresholdPa\":5," +
            "\"minimumPressureTransferPa\":0," +
            "\"vacuumThresholdPa\":0," +
            "\"sleepThreshold\":2147483647," +
            "\"sleepEpsilonPa\":0," +
            "\"voxelSnappingEnabled\":false," +
            "\"voxelSnapTemperatureEpsilonK\":0.01," +
            "\"voxelSnapMoleFractionEpsilon\":0.001," +
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
        return Request(id, "addChunk",
            $"\"position\":{{\"x\":{x},\"y\":{y},\"z\":{z}}},\"classification\":1");
    }

    private static string SetTemperatureRequest(string id, int x, float temperature)
    {
        return Request(id, "setVoxelTemperature",
            "\"position\":{\"x\":0,\"y\":0,\"z\":0}," +
            $"\"voxel\":{{\"x\":{x},\"y\":0,\"z\":0}}," +
            $"\"temperatureK\":{temperature.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string Request(string id, string op, string? payload = null)
    {
        string suffix = string.IsNullOrEmpty(payload) ? string.Empty : $",{payload}";
        return $"{{\"protocolVersion\":1,\"id\":\"{id}\",\"op\":\"{op}\"{suffix}}}";
    }

    private static async Task<RunResult> RunAsync(params string[] requests)
    {
        string inputText = string.Join('\n', requests) + "\n";
        using var input = new StringReader(inputText);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await HeadlessApplication.RunAsync(
            [],
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
               version.GetInt32() == 1 &&
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

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Numos.slnx")))
                continue;

            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
    }

    private sealed record RunResult(
        int ExitCode,
        string[] OutputLines,
        JsonElement[] Responses,
        string StandardError);
}
