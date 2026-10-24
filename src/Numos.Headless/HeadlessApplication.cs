using Numos.Headless.Protocol;

namespace Numos.Headless;

/// <summary>
///     Command-line entry point with injectable streams for contract tests.
/// </summary>
internal static class HeadlessApplication
{
    private const string HelpText = """
                                    Numos.Headless - deterministic JSONL access to the Numos simulation

                                    Usage:
                                      dotnet run --project src/Numos.Headless
                                      dotnet run --project src/Numos.Headless -- [--quiet] --script <experiment.jsonl>
                                      dotnet run --project src/Numos.Headless -- [--quiet] <experiment.jsonl>

                                    With no script, one JSON request is read from stdin per line. Exactly one compact
                                    JSON response is written to stdout for each request. The session log goes to stderr,
                                    one line per entry. --quiet drops info-level log lines.
                                    See docs/headless_runner.md.
                                    """;

    internal static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args is ["--help" or "-h"])
        {
            await output.WriteLineAsync(HelpText.AsMemory(), cancellationToken);
            return 0;
        }

        if (!TryParseArguments(args, out string? scriptPath, out bool quiet))
        {
            await error.WriteLineAsync("Invalid arguments. Use --help for usage.");
            return 2;
        }

        if (scriptPath == null)
        {
            using var host = new HeadlessCommandHost(error, quiet);
            return await host.RunAsync(input, output, cancellationToken);
        }

        StreamReader script;
        try
        {
            script = File.OpenText(scriptPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            var response = new HeadlessResponse
            {
                Ok = false,
                Error = new HeadlessError
                {
                    Code = "scriptUnavailable",
                    Message = exception.Message,
                    ExceptionType = exception.GetType().Name
                }
            };

            await HeadlessCommandHost.WriteResponseAsync(output, response, cancellationToken);
            return 2;
        }

        using (script)
        using (var host = new HeadlessCommandHost(error, quiet))
        {
            return await host.RunAsync(script, output, cancellationToken);
        }
    }

    private static bool TryParseArguments(string[] args, out string? scriptPath, out bool quiet)
    {
        scriptPath = null;
        quiet = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "--quiet")
            {
                quiet = true;
            }
            else if (argument == "--script" && index + 1 < args.Length && scriptPath == null)
            {
                scriptPath = args[++index];
            }
            else if (!argument.StartsWith('-') && scriptPath == null)
            {
                scriptPath = argument;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
