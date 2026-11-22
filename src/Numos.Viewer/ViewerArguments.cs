using Numos.Workbench;

namespace Numos.Viewer;

/// <summary>
///     The parsed command line of the viewer.
/// </summary>
/// <param name="SolverPaths">The `--solvers` paths, in the order given.</param>
/// <param name="ShowHelp">Whether `--help` was given.</param>
internal sealed record ViewerArguments(IReadOnlyList<string> SolverPaths, bool ShowHelp)
{
    /// <summary>
    ///     The usage text that `--help` and a parse error print.
    /// </summary>
    public const string Usage =
        """
        Usage: Numos.Viewer [--solvers <path>]...

        Options:
          --solvers <path>  Load a solver assembly, or every Numos assembly in a folder, before the window opens.
                            Repeat the option to load more. A NativeAOT build cannot load assemblies.
          -h, --help        Print this text and exit.
        """;

    /// <summary>
    ///     Parses the command line.
    /// </summary>
    /// <param name="args">The arguments after the program name.</param>
    /// <param name="arguments">The parsed arguments, or <see langword="null" /> on an error.</param>
    /// <param name="error">Why the command line does not parse, or <see langword="null" />.</param>
    /// <returns><see langword="true" /> when the command line parses.</returns>
    /// <remarks>
    ///     `--solvers` takes its path as the next argument or after an equals sign, as in `--solvers=mods`.
    /// </remarks>
    public static bool TryParse(
        IReadOnlyList<string> args,
        out ViewerArguments? arguments,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        const string solversOption = "--solvers";
        var paths = new List<string>();
        bool help = false;
        arguments = null;
        error = null;
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument is "-h" or "--help")
            {
                help = true;
            }
            else if (argument == solversOption)
            {
                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    error = "--solvers needs a path.";
                    return false;
                }

                paths.Add(args[++index]);
            }
            else if (argument.StartsWith(solversOption + "=", StringComparison.Ordinal))
            {
                string path = argument[(solversOption.Length + 1)..];
                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "--solvers needs a path.";
                    return false;
                }

                paths.Add(path);
            }
            else
            {
                error = $"Unknown argument '{argument}'.";
                return false;
            }
        }

        arguments = new ViewerArguments(paths, help);
        return true;
    }

    /// <summary>
    ///     Parses the command line and loads every `--solvers` path.
    /// </summary>
    /// <param name="args">The arguments after the program name.</param>
    /// <param name="loader">Loads the assemblies.</param>
    /// <param name="output">Receives the usage text and the load reports.</param>
    /// <param name="errorOutput">Receives parse and load errors.</param>
    /// <param name="reports">The load report of each path, in order.</param>
    /// <returns>
    ///     The exit code when the viewer must not start: 0 after `--help`, 2 for a bad command line or a path that did
    ///     not load. <see langword="null" /> when the viewer starts.
    /// </returns>
    /// <remarks>
    ///     A path that fails stops the viewer before the window opens, so a mistyped path never leaves a viewer
    ///     without the solvers its user asked for.
    /// </remarks>
    public static int? Prepare(
        IReadOnlyList<string> args,
        SolverAssemblyLoader loader,
        TextWriter output,
        TextWriter errorOutput,
        out IReadOnlyList<SolverAssemblyLoadReport> reports)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errorOutput);
        reports = [];
        if (!TryParse(args, out var arguments, out string? error))
        {
            errorOutput.WriteLine(error);
            errorOutput.WriteLine(Usage);
            return 2;
        }

        if (arguments!.ShowHelp)
        {
            output.WriteLine(Usage);
            return 0;
        }

        if (arguments.SolverPaths.Count > 0 && !loader.IsSupported)
        {
            errorOutput.WriteLine($"--solvers: {SolverAssemblyLoader.UnsupportedMessage}");
            return 2;
        }

        var loaded = new List<SolverAssemblyLoadReport>(arguments.SolverPaths.Count);
        bool failed = false;
        foreach (string path in arguments.SolverPaths)
        {
            var report = loader.Load(path);
            loaded.Add(report);
            var writer = report.Succeeded ? output : errorOutput;
            foreach (string line in report.Describe())
                writer.WriteLine(line);

            failed |= !report.Succeeded;
        }

        reports = loaded;
        return failed ? 2 : null;
    }
}
