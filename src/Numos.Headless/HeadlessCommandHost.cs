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

    public void Dispose()
    {
        _session.Dispose();
    }

    internal async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        int lineNumber = 0;
        bool hadErrors = false;
        while (!_exitRequested)
        {
            string? line = await input.ReadLineAsync(cancellationToken);
            if (line == null)
                break;

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var response = ProcessLine(line, lineNumber, error);
            if (!response.Ok)
                hadErrors = true;

            await WriteResponseAsync(output, response, cancellationToken);
        }

        return hadErrors ? 1 : 0;
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
        {
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

                var execution = _session.Execute(request);
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
    }

    private static HeadlessRequest? ReadEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        int? protocolVersion = root.TryGetProperty("protocolVersion", out var versionElement) &&
                               versionElement.ValueKind == JsonValueKind.Number &&
                               versionElement.TryGetInt32(out int parsedVersion)
            ? parsedVersion
            : null;

        string? id = root.TryGetProperty("id", out var idElement) &&
                     idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;

        string? op = root.TryGetProperty("op", out var opElement) &&
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