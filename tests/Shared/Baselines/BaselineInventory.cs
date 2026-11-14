using System.Reflection;
using NUnit.Framework.Interfaces;

namespace Numos.Tests.Baselines;

/// <summary>
///     One test method as the baseline layout names it. Both parts are already sanitized, so they compare directly
///     with a directory name and a file stem under <c>Baselines/</c>.
/// </summary>
/// <param name="Fixture">The directory name, from <see cref="Baseline.FixtureName" />.</param>
/// <param name="Method">The sanitized method name.</param>
/// <param name="HasParameters">
///     Whether the method takes arguments. NUnit names each case <c>Method(args)</c>, so its baseline stem is
///     <c>Method_args</c>.
/// </param>
internal readonly record struct BaselineTestName(string Fixture, string Method, bool HasParameters);

/// <summary>
///     Finds baseline files that no test owns. The path is <c>Baselines/&lt;Fixture&gt;/&lt;Test&gt;.json</c>, so a
///     test owns a file only while its fixture and method names match the path.
/// </summary>
internal static class BaselineInventory
{
    /// <summary>
    ///     Lists every baseline file under <paramref name="baselineRoot" /> that does not belong to a test in
    ///     <paramref name="tests" />.
    /// </summary>
    /// <param name="baselineRoot">The <c>Baselines</c> directory of a test project.</param>
    /// <param name="tests">The tests that may own a file, from <see cref="DiscoverTests" />.</param>
    /// <returns>
    ///     Paths relative to <paramref name="baselineRoot" />, with <c>/</c> separators, in ordinal order. The list is
    ///     empty when the directory does not exist.
    /// </returns>
    /// <remarks>
    ///     A file belongs to a test when it sits exactly one directory below the root, that directory is the test's
    ///     fixture, and the file name without its extension is the test's method name. For a method with parameters,
    ///     a name that starts with the method name and <c>_</c> also matches. A file deeper than that, or directly in
    ///     the root, never matches. <c>*.received.json</c> files are scratch output and are skipped.
    ///     A <c>[TestCase]</c> with its own <c>TestName</c> renames the test, so its baseline is reported here.
    /// </remarks>
    public static List<string> FindOrphans(string baselineRoot, IEnumerable<BaselineTestName> tests)
    {
        var orphans = new List<string>();
        if (!Directory.Exists(baselineRoot))
            return orphans;

        var byFixture = tests
            .GroupBy(test => test.Fixture, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(baselineRoot, "*.json", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".received.json", StringComparison.Ordinal))
                continue;

            string relative = Path.GetRelativePath(baselineRoot, file).Replace('\\', '/');
            string[] parts = relative.Split('/');
            bool owned = parts.Length == 2
                         && byFixture.TryGetValue(parts[0], out var candidates)
                         && candidates.Any(test => Owns(test, Path.GetFileNameWithoutExtension(parts[1])));

            if (!owned)
                orphans.Add(relative);
        }

        orphans.Sort(StringComparer.Ordinal);
        return orphans;
    }

    /// <summary>
    ///     Yields one <see cref="BaselineTestName" /> for each test method in <paramref name="assembly" />.
    /// </summary>
    /// <remarks>
    ///     A method counts when it is public and carries an attribute that builds tests, which covers
    ///     <c>[Test]</c>, <c>[TestCase]</c>, and <c>[TestCaseSource]</c>. Inherited methods count once for each
    ///     derived type, because NUnit reports the derived type as the fixture. Abstract types are not skipped,
    ///     since NUnit runs static fixtures and an extra name only makes the check more lenient.
    /// </remarks>
    public static IEnumerable<BaselineTestName> DiscoverTests(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.FlattenHierarchy;

        foreach (var type in assembly.GetTypes())
        {
            string fixture = Baseline.FixtureName(type.FullName ?? type.Name);
            foreach (var method in type.GetMethods(flags))
            {
                if (!IsTest(method))
                    continue;

                yield return new BaselineTestName(
                    fixture,
                    Baseline.SanitizeFileName(method.Name),
                    method.GetParameters().Length > 0);
            }
        }
    }

    private static bool Owns(BaselineTestName test, string stem)
    {
        if (stem == test.Method)
            return true;

        return test.HasParameters && stem.StartsWith(test.Method + "_", StringComparison.Ordinal);
    }

    private static bool IsTest(MethodInfo method)
    {
        return method.GetCustomAttributes(inherit: true)
            .Any(attribute => attribute is ISimpleTestBuilder or ITestBuilder);
    }
}
