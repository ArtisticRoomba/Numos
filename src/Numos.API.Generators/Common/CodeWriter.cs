using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Numos.API.Generators.Common;

/// <summary>
///     Builds generated source with four-space indentation and <c>\n</c> line endings on every platform.
/// </summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0)
            _builder.Append(' ', _indent * 4).Append(text);
        _builder.Append('\n');
    }

    public void Open()
    {
        Line("{");
        _indent++;
    }

    public void Close()
    {
        _indent--;
        Line("}");
    }

    public override string ToString()
    {
        return _builder.ToString();
    }

    /// <summary>
    ///     Prefixes <paramref name="identifier" /> with <c>@</c> when it is a reserved keyword.
    /// </summary>
    public static string Escape(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;
    }
}
