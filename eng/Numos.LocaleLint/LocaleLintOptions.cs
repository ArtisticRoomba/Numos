namespace Numos.LocaleLint;

/// <summary>
///     The settings for one <see cref="LocaleLinter.Run" /> call.
/// </summary>
/// <param name="Root">The repo root that holds <c>src/Numos.Viewer</c>.</param>
internal sealed record LocaleLintOptions(string Root)
{
    /// <summary>
    ///     Gets the only culture to report, or <see langword="null" /> for all of them.
    /// </summary>
    public string? Culture { get; init; }

    /// <summary>
    ///     Gets a value that makes warnings fail the run.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>
    ///     Gets a value that keeps LOC201 notes in the result.
    /// </summary>
    public bool Untranslated { get; init; }

    /// <summary>
    ///     Gets the font coverage to use instead of reading the viewer's font files. Tests set this.
    /// </summary>
    public FontCoverage? Fonts { get; init; }
}

/// <summary>
///     The outcome of a lint run.
/// </summary>
/// <param name="Findings">The findings, sorted by path, line, column, and rule.</param>
/// <param name="Cultures">The culture directories the run checked.</param>
internal sealed record LocaleLintResult(IReadOnlyList<Finding> Findings, IReadOnlyList<string> Cultures)
{
    /// <summary>
    ///     Counts the findings of one severity.
    /// </summary>
    public int Count(Severity severity)
    {
        return Findings.Count(f => f.Severity == severity);
    }

    /// <summary>
    ///     Gets the process exit code for these findings: 0 when clean, 1 when a failing finding exists.
    /// </summary>
    public int ExitCode(bool strict)
    {
        return Findings.Any(f => f.Severity == Severity.Error || strict && f.Severity == Severity.Warning) ? 1 : 0;
    }
}

/// <summary>
///     Thrown when the linter cannot start, such as a missing locale directory or an unknown culture.
/// </summary>
internal sealed class LocaleLintSetupException(string message) : Exception(message);