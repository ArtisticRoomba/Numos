namespace Numos.LocaleLint;

/// <summary>
///     The command-line front end. It parses arguments, runs <see cref="LocaleLinter" />, and prints findings.
/// </summary>
internal static class LocaleLintCli
{
    private const string Usage = """
                                 Usage: Numos.LocaleLint [options]

                                 Checks the viewer's Fluent files and the code that reads them.

                                 Options:
                                   --root <dir>       The repo root. Defaults to the nearest parent of the current directory with Numos.slnx.
                                   --culture <name>   Report only this culture's findings. en also gets code findings.
                                   --strict           Warnings fail the run. Notes never do.
                                   --untranslated     Also print LOC201 notes.
                                   --help             Show this text.

                                 Exit codes: 0 clean, 1 failing findings, 2 usage or setup error.
                                 """;

    /// <summary>
    ///     Runs the tool and returns the process exit code.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="output">Receives findings, the summary, and help text.</param>
    /// <param name="error">Receives usage and setup errors.</param>
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        string? root = null;
        string? culture = null;
        bool strict = false;
        bool untranslated = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case "--strict":
                    strict = true;
                    break;
                case "--untranslated":
                    untranslated = true;
                    break;
                case "--root" or "--culture":
                    if (i + 1 >= args.Length)
                        return UsageError(error, $"{args[i]} needs a value.");

                    if (args[i] == "--root")
                        root = args[++i];
                    else
                        culture = args[++i];

                    break;
                default:
                    return UsageError(error, $"Unknown argument '{args[i]}'.");
            }
        }

        try
        {
            root ??= FindRoot(Directory.GetCurrentDirectory());
            var result = LocaleLinter.Run(
                new LocaleLintOptions(root)
                {
                    Culture = culture,
                    Strict = strict,
                    Untranslated = untranslated
                });

            foreach (var finding in result.Findings)
                output.WriteLine(finding);

            output.WriteLine(
                $"{result.Count(Severity.Error)} errors, {result.Count(Severity.Warning)} warnings, " +
                $"{result.Count(Severity.Note)} notes, {result.Cultures.Count} cultures ({string.Join(", ", result.Cultures)})");

            return result.ExitCode(strict);
        }
        catch (LocaleLintSetupException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int UsageError(TextWriter error, string message)
    {
        error.WriteLine($"error: {message}");
        error.WriteLine("Run with --help for usage.");
        return 2;
    }

    private static string FindRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Numos.slnx")))
            directory = directory.Parent;

        return directory?.FullName ??
               throw new LocaleLintSetupException("Numos.slnx was not found in the current directory or any parent. Pass --root.");
    }
}