using System.Globalization;
using System.Text.Json;

namespace Numos.Headless.Tests;

/// <summary>
///     Builds requests and runs the application in-process for protocol tests.
/// </summary>
internal static class HeadlessTestSupport
{
    internal const string Coordinate0 = "{\"x\":0,\"y\":0,\"z\":0}";

    internal static string Request(string id, string op, string? payload = null)
    {
        string suffix = string.IsNullOrEmpty(payload) ? string.Empty : $",{payload}";
        return $"{{\"protocolVersion\":2,\"id\":\"{id}\",\"op\":\"{op}\"{suffix}}}";
    }

    internal static string C(int x, int y, int z)
    {
        return $"{{\"x\":{x},\"y\":{y},\"z\":{z}}}";
    }

    /// <summary>
    ///     A project with deterministic solver settings and one gas named `First`.
    /// </summary>
    /// <remarks>
    ///     The project uses the legacy transport model unless a test asks otherwise. Most protocol tests care about
    ///     the protocol and not the transport law.
    /// </remarks>
    internal static string CreateProject(
        string id,
        int width,
        int height,
        int depth,
        string? extraConfig = null,
        string transportModel = "legacy")
    {
        string config = "\"defaultTemperatureFallbackK\":300," +
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
                        (extraConfig == null ? string.Empty : "," + extraConfig);

        return Request(
            id,
            "createProject",
            "\"name\":\"Headless tests\"," +
            $"\"transportModel\":\"{transportModel}\"," +
            $"\"chunkDimensions\":{C(width, height, depth)}," +
            $"\"config\":{{{config}}}," +
            "\"gases\":[{\"name\":\"First\",\"molarHeatCapacityAtConstantVolume\":1,\"diffusionCoefficient\":0}]");
    }

    internal static Task<RunResult> RunAsync(params string[] requests)
    {
        return RunWithArgsAsync(["--quiet"], requests);
    }

    internal static async Task<RunResult> RunWithArgsAsync(string[] args, params string[] requests)
    {
        string inputText = string.Join('\n', requests) + "\n";
        using var input = new StringReader(inputText);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await HeadlessApplication.RunAsync(args, input, output, error, CancellationToken.None);

        string[] lines = output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        JsonElement[] responses = lines.Select(Parse).ToArray();
        return new RunResult(exitCode, lines, responses, error.ToString());
    }

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static JsonElement Find(this RunResult run, string id)
    {
        return run.Responses.Single(response =>
            response.TryGetProperty("id", out var value) && value.GetString() == id);
    }

    internal static JsonElement Result(this RunResult run, string id)
    {
        var response = run.Find(id);
        Assert.That(response.GetProperty("ok").GetBoolean(), Is.True, response.GetRawText());
        return response.GetProperty("result");
    }

    internal static string ErrorCode(this RunResult run, string id)
    {
        var response = run.Find(id);
        Assert.That(response.GetProperty("ok").GetBoolean(), Is.False, response.GetRawText());
        return response.GetProperty("error").GetProperty("code").GetString()!;
    }

}

internal sealed record RunResult(
    int ExitCode,
    string[] OutputLines,
    JsonElement[] Responses,
    string StandardError);
