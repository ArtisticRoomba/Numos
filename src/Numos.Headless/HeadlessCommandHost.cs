using System.Text.Json;
using Numos.Headless.Protocol;

namespace Numos.Headless;

/// <summary>
///     Processes one versioned JSON request per input line and emits exactly one JSON response for it.
/// </summary>
internal sealed class HeadlessCommandHost : IDisposable
{
    internal const int ProtocolVersion = 1;

    private readonly SimulationSession _session = new();
    private bool _exitRequested;

    internal async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var lineNumber = 0;
        var hadErrors = false;
        while (!_exitRequested)
        {
            string? line = await input.ReadLineAsync(cancellationToken);
            if (line == null)
                break;

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            HeadlessResponse response = ProcessLine(line, lineNumber, error);
            if (!response.Ok)
                hadErrors = true;

            await WriteResponseAsync(output, response, cancellationToken);
        }

        return hadErrors ? 1 : 0;
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    internal static async Task WriteResponseAsync(
        TextWriter output,
        HeadlessResponse response,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(response, HeadlessJsonContext.Default.HeadlessResponse);
        await output.WriteLineAsync(json.AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private HeadlessResponse ProcessLine(string line, int lineNumber, TextWriter errorOutput)
    {
        HeadlessRequest? request = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return Failure(
                null,
                "invalidJson",
                exception.Message,
                lineNumber,
                nameof(JsonException));
        }

        using (document)
        try
        {
            request = ReadEnvelope(document.RootElement);
            request = document.RootElement.Deserialize(HeadlessJsonContext.Default.HeadlessRequest);
            if (request == null)
                throw new HeadlessRequestException("invalidRequest", "A request must be a JSON object.");
            if (!request.ProtocolVersion.HasValue)
                throw new HeadlessRequestException("missingProperty", "The 'protocolVersion' property is required.");
            if (request.ProtocolVersion.Value != ProtocolVersion)
            {
                throw new HeadlessRequestException(
                    "unsupportedProtocol",
                    $"protocolVersion must be {ProtocolVersion}; received {request.ProtocolVersion.Value}.");
            }

            if (string.IsNullOrWhiteSpace(request.Id))
                throw new HeadlessRequestException("missingProperty", "The 'id' property is required.");
            if (string.IsNullOrWhiteSpace(request.Op))
                throw new HeadlessRequestException("missingProperty", "The 'op' property is required.");

            ValidateOperationProperties(document.RootElement, request.Op);
            CommandExecution execution = _session.Execute(request);
            _exitRequested = execution.ExitRequested;
            return new HeadlessResponse
            {
                Id = request.Id,
                Op = request.Op,
                Ok = true,
                State = _session.GetState(),
                Result = execution.Result,
                Observation = execution.Observation
            };
        }
        catch (JsonException exception)
        {
            return Failure(
                request,
                "invalidRequest",
                exception.Message,
                lineNumber,
                nameof(JsonException));
        }
        catch (HeadlessRequestException exception)
        {
            return Failure(request, exception.Code, exception.Message, lineNumber);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return Failure(
                request,
                "operationRejected",
                exception.Message,
                lineNumber,
                exception.GetType().Name);
        }
        catch (Exception exception)
        {
            errorOutput.WriteLine($"Unhandled exception while processing JSONL line {lineNumber}: {exception}");
            return Failure(
                request,
                "internalError",
                "The operation failed unexpectedly. See stderr for the exception.",
                lineNumber,
                exception.GetType().Name);
        }
    }

    private static HeadlessRequest? ReadEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        int? protocolVersion = root.TryGetProperty("protocolVersion", out JsonElement versionElement) &&
                               versionElement.ValueKind == JsonValueKind.Number &&
                               versionElement.TryGetInt32(out int parsedVersion)
            ? parsedVersion
            : null;
        string? id = root.TryGetProperty("id", out JsonElement idElement) &&
                     idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        string? op = root.TryGetProperty("op", out JsonElement opElement) &&
                     opElement.ValueKind == JsonValueKind.String
            ? opElement.GetString()
            : null;
        return new HeadlessRequest
        {
            ProtocolVersion = protocolVersion,
            Id = id,
            Op = op
        };
    }

    private static void ValidateOperationProperties(JsonElement root, string operation)
    {
        if (!IsSupportedOperation(operation))
            return;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is "protocolVersion" or "id" or "op" ||
                IsOperationProperty(operation, property.Name))
                continue;

            throw new HeadlessRequestException(
                "invalidRequest",
                $"The '{property.Name}' property is not valid for the '{operation}' operation.");
        }
    }

    private static bool IsSupportedOperation(string operation)
    {
        return operation is
            "createSimulation" or
            "closeSimulation" or
            "addChunk" or
            "removeChunk" or
            "sealChunk" or
            "setChunkClassification" or
            "setVoxelClassification" or
            "setVoxelTemperature" or
            "addGas" or
            "injectGas" or
            "wakeRoom" or
            "sleepChunk" or
            "updateConfig" or
            "setSolverEnabled" or
            "resetSolvers" or
            "tick" or
            "observe" or
            "exit";
    }

    private static bool IsOperationProperty(string operation, string property)
    {
        return operation switch
        {
            "createSimulation" => property is "name" or "dimensions" or "config" or "gases",
            "addChunk" => property is "position" or "classification",
            "removeChunk" or "sealChunk" or "sleepChunk" => property == "position",
            "setChunkClassification" => property is "position" or "classification",
            "setVoxelClassification" => property is "position" or "voxel" or "classification",
            "setVoxelTemperature" => property is "position" or "voxel" or "temperatureK",
            "addGas" => property == "gas",
            "injectGas" => property is "position" or "voxel" or "gasId" or "moles" or "temperatureK",
            "wakeRoom" => property is "position" or "roomId",
            "updateConfig" => property == "config",
            "setSolverEnabled" => property is "solver" or "enabled",
            "tick" => property == "count",
            "observe" => property is "position" or "voxel" or "includeVoxels" or
                "onlyGasBearingVoxels" or "maxIssueLocations",
            _ => false
        };
    }

    private HeadlessResponse Failure(
        HeadlessRequest? request,
        string code,
        string message,
        int lineNumber,
        string? exceptionType = null)
    {
        return new HeadlessResponse
        {
            Id = request?.Id,
            Op = request?.Op,
            Ok = false,
            State = _session.GetState(),
            Error = new HeadlessError
            {
                Code = code,
                Message = message,
                ExceptionType = exceptionType,
                Line = lineNumber
            }
        };
    }
}
