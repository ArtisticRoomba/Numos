using System.Text.RegularExpressions;

namespace Numos.LocaleLint;

/// <summary>
///     Finds the source line of each entry in a Fluent file.
/// </summary>
/// <remarks>
///     Linguini's <c>AstMessage.Location.Row</c> does not point at the line of the entry id, so the linter scans the
///     text instead. The k-th definition of an id in the file maps to the k-th entry the parser returns for it.
/// </remarks>
internal sealed partial class LineIndex
{
    private readonly Dictionary<string, Queue<int>> _lines = new(StringComparer.Ordinal);

    /// <summary>
    ///     Scans a file.
    /// </summary>
    public LineIndex(string path)
    {
        string[] lines = File.ReadAllText(path).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var match = Definition().Match(lines[i].TrimStart('﻿'));
            if (!match.Success)
                continue;

            string key = match.Groups[1].Value;
            if (!_lines.TryGetValue(key, out Queue<int>? queue))
                _lines[key] = queue = new Queue<int>();

            queue.Enqueue(i + 1);
        }
    }

    [GeneratedRegex(@"^(-?[A-Za-z][A-Za-z0-9_-]*)[ \t]*=")]
    private static partial Regex Definition();

    /// <summary>
    ///     Returns the next unused line for an id, or 1 when the scan found none.
    /// </summary>
    /// <param name="id">The message id, or the term id with its leading dash.</param>
    public int Next(string id)
    {
        return _lines.TryGetValue(id, out Queue<int>? queue) && queue.Count > 0 ? queue.Dequeue() : 1;
    }
}