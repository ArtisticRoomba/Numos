using Numos.API;
using Numos.CoreSim;
using Numos.Serialization;
using Numos.Serialization.FileSystem;

namespace Numos.Workbench.Tests;

/// <summary>
///     Loads Numos.Tests.SolverAssembly from the solver-assembly folder that NumosSolverAssembly.targets fills.
/// </summary>
/// <remarks>
///     The default load context never unloads, so the first load in the process is the only one that reports
///     <see cref="SolverAssemblyStatus.Loaded" />. <see cref="LoadOnce" /> makes it, and every other test sees the
///     assembly as loaded already.
/// </remarks>
[TestFixture]
public sealed class SolverAssemblyLoaderTests
{
    private const string AssemblyName = "Numos.Tests.SolverAssembly";
    private const string SolverName = "numos-tests/loaded/solver";
    private const string ChunkFieldKey = "numos-tests/loaded/heat/Value";
    private const string ComponentKey = "numos-tests/loaded/counter";

    private SolverAssemblyLoadReport _first = null!;
    private bool _catalogHadSolverBefore;

    private static string Folder => Path.Combine(TestContext.CurrentContext.TestDirectory, "solver-assembly");

    private static string AssemblyPath => Path.Combine(Folder, AssemblyName + ".dll");

    [OneTimeSetUp]
    public void LoadOnce()
    {
        _catalogHadSolverBefore = AtmosSolverCatalog.TryGet(SolverName, out _);
        _first = new SolverAssemblyLoader().Load(AssemblyPath);
    }

    private static string CreateTemporaryFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "numos-solver-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Test]
    public void Load_File_RunsTheModuleInitializerAndReportsWhatAppeared()
    {
        var result = _first.Assemblies.Single();

        Assert.Multiple(() =>
        {
            Assert.That(_catalogHadSolverBefore, Is.False, "the test process must not reference the assembly");
            Assert.That(_first.Error, Is.Null);
            Assert.That(_first.Succeeded, Is.True);
            Assert.That(_first.IsFolder, Is.False);
            Assert.That(_first.Path, Is.EqualTo(Path.GetFullPath(AssemblyPath)));
            Assert.That(result.Status, Is.EqualTo(SolverAssemblyStatus.Loaded));
            Assert.That(result.AssemblyName, Is.EqualTo(AssemblyName));
            Assert.That(result.Solvers, Is.EqualTo(new[] { SolverName }));
            Assert.That(result.ChunkFields, Is.EqualTo(new[] { ChunkFieldKey }));
            Assert.That(result.Components, Is.EqualTo(new[] { ComponentKey }));
            Assert.That(result.Message, Is.Null);
            Assert.That(AtmosSolverCatalog.TryGet(SolverName, out _), Is.True);
            Assert.That(SimulationComponentCatalog.Keys, Does.Contain(ComponentKey));
            Assert.That(
                _first.Describe(),
                Is.EqualTo(new[]
                {
                    $"Loaded {AssemblyName} from '{Path.GetFullPath(AssemblyPath)}': solvers {SolverName}; " +
                    $"chunk fields {ChunkFieldKey}; components {ComponentKey}."
                }));
        });
    }

    [Test]
    public void Load_SameFileAgain_ReportsAlreadyLoadedWithNothingNew()
    {
        var report = new SolverAssemblyLoader().Load(AssemblyPath);
        var result = report.Assemblies.Single();

        Assert.Multiple(() =>
        {
            Assert.That(report.Succeeded, Is.True);
            Assert.That(result.Status, Is.EqualTo(SolverAssemblyStatus.AlreadyLoaded));
            Assert.That(result.Solvers, Is.Empty);
            Assert.That(result.ChunkFields, Is.Empty);
            Assert.That(result.Components, Is.Empty);
            Assert.That(
                report.Describe(),
                Is.EqualTo(new[] { $"{AssemblyName} was already loaded: solvers none; chunk fields none; components none." }));
        });
    }

    [Test]
    public void Load_ModFolder_SkipsTheDependenciesBesideTheMod()
    {
        var report = new SolverAssemblyLoader().Load(Folder);
        var skipped = report.Assemblies.Where(static result => result.Status == SolverAssemblyStatus.Skipped).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(report.IsFolder, Is.True);
            Assert.That(report.Error, Is.Null);
            Assert.That(report.Succeeded, Is.True);
            Assert.That(
                report.Assemblies.Select(static result => Path.GetFileName(result.Path)),
                Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));

            Assert.That(
                report.Assemblies.Single(static result => result.AssemblyName == AssemblyName).Status,
                Is.EqualTo(SolverAssemblyStatus.AlreadyLoaded));

            Assert.That(skipped.Select(static result => result.AssemblyName), Does.Contain("Numos.API"));
            Assert.That(skipped.Select(static result => result.Message), Has.All.Contain("NumosModule"));
            Assert.That(report.Describe().Last(), Does.Match("^Skipped [0-9]+ files that are not Numos assemblies\\.$"));
        });
    }

    [Test]
    public void Load_FolderWithoutANumosAssembly_IsAnError()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "notes.dll"), "not an assembly");
            File.Copy(typeof(Assert).Assembly.Location, Path.Combine(folder, "nunit.framework.dll"));
            var report = new SolverAssemblyLoader().Load(folder);

            Assert.Multiple(() =>
            {
                Assert.That(report.Error, Does.Contain("holds no Numos assembly"));
                Assert.That(report.Succeeded, Is.False);
                Assert.That(report.Assemblies.Select(static result => result.Status), Is.All.EqualTo(SolverAssemblyStatus.Skipped));
                Assert.That(report.Assemblies[0].Message, Is.EqualTo("the file is not a .NET assembly."));
                Assert.That(report.Assemblies[0].AssemblyName, Is.Null);
                Assert.That(report.Assemblies[1].AssemblyName, Is.EqualTo("nunit.framework"));
            });
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public void Load_NamedFileThatIsNotANumosAssembly_IsAnError()
    {
        var report = new SolverAssemblyLoader().Load(typeof(Assert).Assembly.Location);

        Assert.Multiple(() =>
        {
            Assert.That(report.Succeeded, Is.False);
            Assert.That(report.Error, Does.Contain("is not a Numos assembly"));
            Assert.That(report.Assemblies.Single().Status, Is.EqualTo(SolverAssemblyStatus.Skipped));
            Assert.That(report.Describe(), Has.Count.EqualTo(1), "a skipped named file is only the error line");
        });
    }

    [Test]
    public void Load_MissingPath_IsAnError()
    {
        string path = Path.Combine(Path.GetTempPath(), "numos-missing-" + Guid.NewGuid().ToString("N") + ".dll");
        var report = new SolverAssemblyLoader().Load(path);

        Assert.Multiple(() =>
        {
            Assert.That(report.Succeeded, Is.False);
            Assert.That(report.Error, Does.Contain("does not exist"));
            Assert.That(report.Assemblies, Is.Empty);
        });
    }

    [Test]
    public void Load_WhenDisabled_RefusesWithoutReadingThePath()
    {
        var loader = new SolverAssemblyLoader(enabled: false);
        var report = loader.Load(AssemblyPath);

        Assert.Multiple(() =>
        {
            Assert.That(loader.IsSupported, Is.False);
            Assert.That(new SolverAssemblyLoader().IsSupported, Is.True, "the tests run on the JIT");
            Assert.That(report.Error, Is.EqualTo(SolverAssemblyLoader.UnsupportedMessage));
            Assert.That(report.Assemblies, Is.Empty);
            Assert.That(report.Succeeded, Is.False);
        });
    }

    [Test]
    public void LoadedSolver_CanBeAddedEditedAndRemovedThroughTheSession()
    {
        using var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Loaded", 4, 4, 1, TickRate: 20));
        session.AddChunk(default, 1);

        Assert.That(session.GetSolverCatalog().Single(entry => entry.Name == SolverName).IsAdded, Is.False);
        Assert.That(session.AddSolver(SolverName).Applied, Is.True);
        session.SetSolverState(SolverName, "Rate", "2");
        session.SetSolverState(SolverName, "Flag", "true");
        session.Tick(3);

        Assert.That(session.TryGetSolverState(SolverName, out var members), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(members.Select(static member => member.Name), Is.EqualTo(new[] { "Rate", "Total", "Mode", "Flag" }));
            Assert.That(members.Single(static member => member.Name == "Total").Value, Is.EqualTo(6f));
            Assert.That(members.Single(static member => member.Name == "Flag").Value, Is.EqualTo(true));
            Assert.That(members.Single(static member => member.Name == "Mode").Text, Is.EqualTo("Counting"));
            Assert.That(session.GetSolverCatalog().Single(entry => entry.Name == SolverName).IsAdded, Is.True);
        });

        Assert.That(session.RemoveSolver(SolverName).Applied, Is.True);
        Assert.That(session.GetSolverSteps().Select(static step => step.Name), Does.Not.Contain(SolverName));
    }

    [Test]
    public void LoadReplay_ListsEveryRequiredSolverTheCatalogLacks()
    {
        const string first = "numos-workbench-tests/host-b";
        const string second = "numos-workbench-tests/host-a";
        string folder = CreateTemporaryFolder();
        try
        {
            string path = Path.Combine(folder, "host.numos");
            using (var world = new AtmosWorld(new AtmosConfig()))
            {
                world.CreateSimulation(1, 1, 1);
                world.Solvers.Register(first, static _ => { });
                world.Solvers.Register(second, static _ => { });
                var timeline = new AtmosWorldReplayTimeline(world, 0.05);
                world.Tick();
                var metadata = new NumosReplayMetadata("Host", DateTimeOffset.UnixEpoch, "tests", "1", "1");
                NumosReplayFile.Save(path, new NumosWorldReplayDocument(metadata, timeline.CaptureReplay()));
            }

            using var session = new WorkbenchSession();
            var exception = Assert.Throws<MissingSolversException>(() => session.LoadReplay(path))!;

            Assert.Multiple(() =>
            {
                Assert.That(exception.MissingSolvers, Is.EqualTo(new[] { second, first }));
                Assert.That(exception.Path, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(exception.Message, Does.Contain($"{second}, {first}"));
                Assert.That(session.World, Is.Null);
            });
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
