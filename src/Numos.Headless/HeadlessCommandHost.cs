using System.Globalization;
using System.Text.Json;
using Numos.Build;
using Numos.Headless.Protocol;
using Numos.Workbench;

namespace Numos.Headless;

/// <summary>
///     Processes one versioned JSON request per input line and emits exactly one JSON response for it.
/// </summary>
internal sealed class HeadlessCommandHost : IDisposable
{
    internal const int ProtocolVersion = 2;

    private readonly TextWriter _error;
    private readonly bool _quiet;
    private readonly WorkbenchSession _session;
    private readonly HeadlessOperations _operations;
    private bool _exitRequested;

    /// <summary>
    ///     Creates a host and starts streaming the session log to <paramref name="error" />.
    /// </summary>
    /// <param name="error">Receives one line per log entry and unexpected exceptions.</param>
    /// <param name="quiet">When <see langword="true" />, the host drops entries below warning level.</param>
    internal HeadlessCommandHost(TextWriter error, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(error);
        _error = error;
        _quiet = quiet;
        var log = new WorkbenchLog();
        log.EntryWritten += WriteLogEntry;
        _session = new WorkbenchSession(log, new WorkbenchHostInfo("Numos.Headless", GetVersion()));
        _operations = new HeadlessOperations(_session);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private static string GetVersion()
    {
        string commit = GeneratedBuildInfo.GitCommit is { Length: >= 7 } and not "unknown"
            ? GeneratedBuildInfo.GitCommit[..7]
            : "unknown";

        return $"{GeneratedBuildInfo.PackageVersion}+{commit}";
    }

    /// <summary>
    ///     Gets whether an <c>exit</c> request ran. A server stops accepting input once this is <see langword="true" />.
    /// </summary>
    internal bool ExitRequested => _exitRequested;

    /// <summary>
    ///     Gets the session log, which doubles as the server output shell.
    /// </summary>
    internal WorkbenchLog Log => _session.Log;

    /// <summary>
    ///     Formats a log entry as one line: timestamp, level, tick, source, message.
    /// </summary>
    internal static string FormatLogEntry(WorkbenchLogEntry entry)
    {
        string level = entry.Level switch
        {
            WorkbenchLogLevel.Debug => "DEBUG",
            WorkbenchLogLevel.Info => "INFO",
            WorkbenchLogLevel.Warn => "WARN",
            WorkbenchLogLevel.Error => "ERROR",
            _ => "FATAL"
        };

        string tick = entry.Tick is { } value ? value.ToString(CultureInfo.InvariantCulture) : "-";
        string message = entry.Message.ReplaceLineEndings(" ");
        return $"{entry.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)} " +
               $"{level,-5} tick={tick} {entry.Source}: {message}";
    }

    private void WriteLogEntry(WorkbenchLogEntry entry)
    {
        if (_quiet && entry.Level <= WorkbenchLogLevel.Info)
            return;

        try
        {
            lock (_error)
            {
                _error.WriteLine(FormatLogEntry(entry));
                _error.Flush();
            }
        }
        catch (IOException)
        {
            // A closed stderr must not stop the simulation.
        }
    }

    /// <summary>
    ///     Processes the requests of one input stream, such as stdin, a script, or one socket connection.
    /// </summary>
    /// <param name="input">Supplies one request per line.</param>
    /// <param name="output">Receives one response line per request.</param>
    /// <param name="cancellationToken">Stops the read loop.</param>
    /// <returns>1 when any response reported an error, otherwise 0.</returns>
    /// <remarks>
    ///     Line numbers in error responses count from the start of <paramref name="input" />. The session persists
    ///     across calls, so a server can call this once per connection.
    /// </remarks>
    internal async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

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

            var response = ProcessLine(line, lineNumber);
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

    private HeadlessResponse ProcessLine(string line, int lineNumber)
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

                var execution = _operations.Execute(request);
                _exitRequested = execution.ExitRequested;
                return new HeadlessResponse
                {
                    Id = request.Id,
                    Op = request.Op,
                    Ok = true,
                    State = _operations.GetState(),
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
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Failure(request, "ioError", exception.Message, lineNumber, exception.GetType().Name);
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
                _error.WriteLine($"Unhandled exception while processing JSONL line {lineNumber}: {exception}");
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
            State = _operations.GetState(),
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