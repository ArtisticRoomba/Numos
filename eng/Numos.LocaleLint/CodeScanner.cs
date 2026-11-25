using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Numos.LocaleLint;

/// <summary>
///     One <c>Locale.GetString</c> call whose key the scanner could read.
/// </summary>
/// <param name="Path">The source file path, relative to the repo root, with forward slashes.</param>
/// <param name="Line">The 1-based line of the key.</param>
/// <param name="Column">The 1-based column of the key.</param>
/// <param name="Key">The key, which can be <c>message.attribute</c>.</param>
/// <param name="Arguments">
///     The argument names the call passes, or <see langword="null" /> when an argument is not a tuple that starts with
///     a string literal.
/// </param>
internal sealed record GetStringCall(string Path, int Line, int Column, string Key, IReadOnlySet<string>? Arguments);

/// <summary>
///     The facts the code rules need from the viewer's C# source.
/// </summary>
/// <param name="Calls">Every readable <c>Locale.GetString</c> call.</param>
/// <param name="Literals">Every string literal in the source.</param>
internal sealed record CodeScan(IReadOnlyList<GetStringCall> Calls, IReadOnlySet<string> Literals);

/// <summary>
///     Reads viewer source with Roslyn, syntax only.
/// </summary>
internal static class CodeScanner
{
    /// <summary>
    ///     Parses every <c>.cs</c> file under a directory, skipping <c>bin</c> and <c>obj</c>.
    /// </summary>
    /// <param name="viewerDirectory">The <c>src/Numos.Viewer</c> directory.</param>
    /// <param name="root">The repo root that paths are made relative to.</param>
    public static CodeScan Scan(string viewerDirectory, string root)
    {
        var calls = new List<GetStringCall>();
        var literals = new HashSet<string>(StringComparer.Ordinal);

        string[] files = Directory.GetFiles(viewerDirectory, "*.cs", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(viewerDirectory, file);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin") || segments.Contains("obj"))
                continue;

            string display = Path.GetRelativePath(root, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: display);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    literals.Add(literal.Token.ValueText);
                else if (node is InvocationExpressionSyntax invocation)
                    ReadCall(invocation, display, calls);
            }
        }

        return new CodeScan(calls, literals);
    }

    private static void ReadCall(InvocationExpressionSyntax invocation, string path, List<GetStringCall> calls)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access ||
            access.Name.Identifier.ValueText != "GetString" ||
            !IsLocale(access.Expression) ||
            invocation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
        var keyExpression = arguments[0].Expression;
        var keys = new List<LiteralExpressionSyntax>();
        if (!CollectKeys(keyExpression, keys))
            return;

        HashSet<string>? names = new(StringComparer.Ordinal);
        for (int i = 1; i < arguments.Count; i++)
        {
            if (arguments[i].Expression is TupleExpressionSyntax { Arguments.Count: 2 } tuple &&
                tuple.Arguments[0].Expression is LiteralExpressionSyntax name &&
                name.IsKind(SyntaxKind.StringLiteralExpression))
            {
                names.Add(name.Token.ValueText);
            }
            else
            {
                names = null;
                break;
            }
        }

        foreach (var key in keys)
        {
            var position = key.GetLocation().GetLineSpan().StartLinePosition;
            calls.Add(new GetStringCall(path, position.Line + 1, position.Character + 1, key.Token.ValueText, names));
        }
    }

    private static bool IsLocale(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "Locale",
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "Locale",
            _ => false
        };
    }

    /// <summary>
    ///     Collects the string literals a key expression can evaluate to. Returns <see langword="false" /> when the
    ///     expression is anything but a literal or a conditional of literals.
    /// </summary>
    private static bool CollectKeys(ExpressionSyntax expression, List<LiteralExpressionSyntax> keys)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return CollectKeys(parenthesized.Expression, keys);
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                keys.Add(literal);
                return true;
            case ConditionalExpressionSyntax conditional:
                return CollectKeys(conditional.WhenTrue, keys) && CollectKeys(conditional.WhenFalse, keys);
            default:
                return false;
        }
    }
}