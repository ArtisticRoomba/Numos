using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Numos.Tests.Roslyn;

/// <summary>
///     Compilation plumbing shared by the generator and analyzer tests.
/// </summary>
internal static class RoslynTestSupport
{
    /// <summary>
    ///     The test host's trusted platform assemblies minus every <c>Numos.*</c> assembly, so each test decides which
    ///     Numos code, real or stand-in, its compilation sees.
    /// </summary>
    internal static readonly MetadataReference[] FrameworkReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(static path => !Path.GetFileName(path).StartsWith("Numos.", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    internal static Diagnostic[] Errors(Compilation compilation)
    {
        return compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    /// <summary>
    ///     Emits <paramref name="compilation"/> in memory and fails the test if that does not succeed.
    /// </summary>
    internal static MetadataReference EmitToReference(Compilation compilation)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}

internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly SourceText _text;

    internal InMemoryAdditionalText(string path, string text)
    {
        Path = path;
        _text = SourceText.From(text);
    }

    public override string Path { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default)
    {
        return _text;
    }
}