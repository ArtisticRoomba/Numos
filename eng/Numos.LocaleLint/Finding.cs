namespace Numos.LocaleLint;

/// <summary>
///     How a finding affects the exit code.
/// </summary>
internal enum Severity
{
    /// <summary>
    ///     Informational. Never fails the run.
    /// </summary>
    Note,

    /// <summary>
    ///     Fails the run only with <see cref="LocaleLintOptions.Strict" />.
    /// </summary>
    Warning,

    /// <summary>
    ///     Always fails the run.
    /// </summary>
    Error
}

/// <summary>
///     One problem the linter found.
/// </summary>
/// <param name="Rule">The rule id, such as <c>LOC102</c>.</param>
/// <param name="Severity">The severity of the rule.</param>
/// <param name="Path">The file path relative to the repo root, with forward slashes.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="Message">The human-readable description.</param>
/// <param name="Culture">
///     The culture the finding belongs to, or <see langword="null" /> for findings about code or about files shared by
///     all cultures. <c>--culture en</c> keeps findings with a <see langword="null" /> culture.
/// </param>
internal sealed record Finding(
    string Rule,
    Severity Severity,
    string Path,
    int Line,
    int Column,
    string Message,
    string? Culture)
{
    /// <summary>
    ///     Formats the finding as one line that IDEs and CI can link.
    /// </summary>
    public override string ToString()
    {
        string label = Severity switch
        {
            Severity.Error => "error",
            Severity.Warning => "warning",
            _ => "note"
        };

        return $"{Path}:{Line}:{Column}: {label} {Rule}: {Message}";
    }
}