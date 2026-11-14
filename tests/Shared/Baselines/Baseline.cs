using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Numos.Tests.Baselines;

/// <summary>
///     Checks characterization values against a JSON baseline checked in next to the test project, at
///     <c>Baselines/&lt;Fixture&gt;/&lt;Test&gt;.json</c>. See <c>docs/testing.md</c> for when to use one and how to
///     regenerate it.
/// </summary>
internal static class Baseline
{
    private const string ProjectDirectoryKey = "NumosTestProjectDirectory";
    private const string ProjectPathKey = "NumosTestProjectPath";

    /// <summary>
    ///     Compares <paramref name="values" /> with the current test's baseline, or rewrites the baseline when
    ///     <c>NUMOS_UPDATE_BASELINES</c> selects it.
    /// </summary>
    /// <param name="values">The values to compare, in the order they should appear in the file.</param>
    /// <param name="guard">
    ///     Marks a baseline that must not move by accident. It only regenerates when the update variable names the
    ///     guard, as in <c>NUMOS_UPDATE_BASELINES=wire-format</c>; <c>=1</c> leaves it alone.
    /// </param>
    /// <remarks>
    ///     A mismatch fails the test. A missing file, entry, or architecture value reports a warning locally and fails
    ///     when <c>CI</c> is set. Either way the received file is written. Every update reports a warning, so a
    ///     regenerate run never comes back silently green. Run the test's own self-consistency checks before this
    ///     call: locally, on an architecture with no baseline, they are the only thing that can fail.
    /// </remarks>
    public static void Verify(BaselineValues values, string? guard = null)
    {
        var verdict = BaselineEngine.Verify(values, CreateContext(), guard);
        switch (verdict.Outcome)
        {
            case BaselineOutcome.Failed:
                Assert.Fail(verdict.Message);
                break;
            case BaselineOutcome.Warning:
            case BaselineOutcome.Updated:
                Assert.Warn(verdict.Message);
                break;
        }
    }

    /// <summary>
    ///     Turns an NUnit test name such as <c>Flow(1.5f,"a b")</c> into something safe as a file name.
    /// </summary>
    internal static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            bool keep = char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_';
            if (keep)
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }

        return builder.ToString().TrimEnd('_', '.');
    }

    /// <summary>
    ///     Turns a full class name into the fixture directory name under <c>Baselines/</c>: the text after the last
    ///     <c>.</c>, sanitized. A nested type's <see cref="Type.FullName" /> is <c>Ns.Outer+Inner</c>, which becomes
    ///     <c>Outer_Inner</c>.
    /// </summary>
    internal static string FixtureName(string className)
    {
        return SanitizeFileName(className[(className.LastIndexOf('.') + 1)..]);
    }

    /// <summary>
    ///     The directory of the test project this assembly was built from. It comes from build metadata rather than
    ///     [CallerFilePath], which deterministic builds may path-map away from the real source tree.
    /// </summary>
    internal static string ProjectDirectory => ReadMetadata(typeof(Baseline).Assembly, ProjectDirectoryKey);

    private static BaselineContext CreateContext()
    {
        var test = TestContext.CurrentContext.Test;
        string className = test.ClassName
                           ?? throw new InvalidOperationException("Baseline.Verify must run inside a test.");
        string fixture = FixtureName(className);

        var assembly = typeof(Baseline).Assembly;

        return new BaselineContext
        {
            BaselinePath = Path.Combine(ProjectDirectory, "Baselines", fixture, SanitizeFileName(test.Name) + ".json"),
            TestFullName = test.FullName,
            TestFilter = $"{fixture}.{test.MethodName ?? test.Name}",
            ProjectPath = ReadMetadata(assembly, ProjectPathKey).Replace('\\', '/'),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            GetEnvironmentVariable = Environment.GetEnvironmentVariable
        };
    }

    private static string ReadMetadata(Assembly assembly, string key)
    {
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                   .FirstOrDefault(attribute => attribute.Key == key)?.Value
               ?? throw new InvalidOperationException(
                   $"{assembly.GetName().Name} has no '{key}' metadata. Set NumosBaselineSupport in its csproj.");
    }
}
