namespace Numos.Workbench;

/// <summary>
///     The outcome of a command that ran on a <see cref="WorkbenchHost" />.
/// </summary>
/// <typeparam name="T">The command's return type.</typeparam>
/// <param name="Value">The value the command returned, or the default when it failed.</param>
/// <param name="Exception">The exception the command threw, or <see langword="null" /> when it succeeded.</param>
public readonly record struct HostResult<T>(T? Value, Exception? Exception)
{
    /// <summary>
    ///     Whether the command returned without throwing.
    /// </summary>
    public bool Succeeded => Exception == null;
}
