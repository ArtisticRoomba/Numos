using Numos.API;
using Numos.Workbench;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class ViewerArgumentsTests
{
    private const string LoadedSolverName = "numos-tests/loaded/solver";

    private static string SolverAssemblyFolder =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "solver-assembly");

    private static (int? ExitCode, string Output, string Error, IReadOnlyList<SolverAssemblyLoadReport> Reports) Prepare(
        SolverAssemblyLoader loader,
        params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int? exitCode = ViewerArguments.Prepare(args, loader, output, error, out var reports);
        return (exitCode, output.ToString(), error.ToString(), reports);
    }

    [Test]
    public void TryParse_TakesRepeatedSolverPathsInOrder()
    {
        Assert.That(
            ViewerArguments.TryParse(["--solvers", "a.dll", "--solvers=mods", "-h"], out var arguments, out string? error),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(arguments!.SolverPaths, Is.EqualTo(new[] { "a.dll", "mods" }));
            Assert.That(arguments.ShowHelp, Is.True);
        });
    }

    [Test]
    public void TryParse_WithoutArguments_StartsWithNothingToLoad()
    {
        Assert.That(ViewerArguments.TryParse([], out var arguments, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(arguments!.SolverPaths, Is.Empty);
            Assert.That(arguments.ShowHelp, Is.False);
        });
    }

    [TestCase(new[] { "--solvers" }, "--solvers needs a path.")]
    [TestCase(new[] { "--solvers", " " }, "--solvers needs a path.")]
    [TestCase(new[] { "--solvers=" }, "--solvers needs a path.")]
    [TestCase(new[] { "replay.numos" }, "Unknown argument 'replay.numos'.")]
    [TestCase(new[] { "--solver", "a.dll" }, "Unknown argument '--solver'.")]
    public void TryParse_RejectsBadCommandLines(string[] args, string expected)
    {
        Assert.That(ViewerArguments.TryParse(args, out var arguments, out string? error), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(arguments, Is.Null);
            Assert.That(error, Is.EqualTo(expected));
        });
    }

    [Test]
    public void Prepare_Help_PrintsUsageAndExits()
    {
        var result = Prepare(new SolverAssemblyLoader(), "--help", "--solvers", "never-loaded.dll");

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.Output, Does.Contain(ViewerArguments.Usage));
            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Reports, Is.Empty);
        });
    }

    [Test]
    public void Prepare_BadCommandLine_PrintsTheErrorAndUsage()
    {
        var result = Prepare(new SolverAssemblyLoader(), "--bogus");

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(2));
            Assert.That(result.Error, Does.StartWith("Unknown argument '--bogus'.").And.Contain(ViewerArguments.Usage));
            Assert.That(result.Output, Is.Empty);
        });
    }

    [Test]
    public void Prepare_SolversWhenLoadingIsUnsupported_ExitsWithTheReason()
    {
        // A disabled loader takes the branch a NativeAOT build takes, where IsDynamicCodeSupported is false.
        var result = Prepare(new SolverAssemblyLoader(enabled: false), "--solvers", SolverAssemblyFolder);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(2));
            Assert.That(result.Error.Trim(), Is.EqualTo($"--solvers: {SolverAssemblyLoader.UnsupportedMessage}"));
            Assert.That(result.Reports, Is.Empty);
            Assert.That(result.Output, Is.Empty);
        });
    }

    [Test]
    public void Prepare_NoSolversWhenLoadingIsUnsupported_StartsTheViewer()
    {
        var result = Prepare(new SolverAssemblyLoader(enabled: false));

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Null);
            Assert.That(result.Output, Is.Empty);
            Assert.That(result.Error, Is.Empty);
        });
    }

    [Test]
    public void Prepare_PathThatDoesNotLoad_StopsBeforeTheWindowOpens()
    {
        string missing = Path.Combine(Path.GetTempPath(), "numos-missing-" + Guid.NewGuid().ToString("N") + ".dll");
        var result = Prepare(new SolverAssemblyLoader(), "--solvers", missing);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(2));
            Assert.That(result.Error, Does.Contain("does not exist"));
            Assert.That(result.Reports.Single().Succeeded, Is.False);
        });
    }

    [Test]
    public void Prepare_SolverFolder_LoadsTheModAndReportsItsSolvers()
    {
        var result = Prepare(new SolverAssemblyLoader(), "--solvers", SolverAssemblyFolder);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Null, result.Error);
            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Reports.Single().Succeeded, Is.True);
            Assert.That(result.Output, Does.Contain("Numos.Tests.SolverAssembly"));
            Assert.That(AtmosSolverCatalog.TryGet(LoadedSolverName, out _), Is.True);
        });
    }
}
