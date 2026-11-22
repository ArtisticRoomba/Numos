namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverCatalogTests
{
    [Test]
    public void DeclaredSolver_IsInTheCatalogWithItsDeclaration()
    {
        Assert.That(AtmosSolverCatalog.TryGet(CatalogFireSolver.Name, out AtmosSolverDescriptor? descriptor), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(descriptor, Is.SameAs(CatalogFireSolver.Descriptor));
            Assert.That(descriptor!.Name, Is.EqualTo(CatalogFireSolver.Name));
            Assert.That(descriptor.SolverType, Is.EqualTo(typeof(CatalogFireSolver)));
            Assert.That(descriptor.After, Is.EqualTo(AtmosBuiltInSolvers.Thermodynamics));
            Assert.That(descriptor.Before, Is.Null);
            Assert.That(descriptor.RunsPerSecond, Is.EqualTo(20));
            Assert.That(descriptor.Interval, Is.Zero);
            Assert.That(descriptor.Neighbors?.Key, Is.EqualTo(CatalogFireSolver.Name + "#all"));
            Assert.That(descriptor.Neighbors?.IncludeCartesian, Is.True);
        });
    }

    [Test]
    public void SelectorSolver_UsesTheSelectorKeyAndInterval()
    {
        Assert.That(AtmosSolverCatalog.TryGet(CatalogPortalSolver.Name, out AtmosSolverDescriptor? descriptor), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(descriptor!.Before, Is.EqualTo(AtmosBuiltInSolvers.GasReactions));
            Assert.That(descriptor.After, Is.Null);
            Assert.That(descriptor.RunsPerSecond, Is.Zero);
            Assert.That(descriptor.Interval, Is.EqualTo(7));
            Assert.That(descriptor.Neighbors?.Key, Is.EqualTo("numos-tests/catalog/portal#gas-links"));
            Assert.That(descriptor.Neighbors?.IncludeCartesian, Is.False);
        });
    }

    [Test]
    public void SolverWithoutNeighbors_HasNoSelection()
    {
        Assert.That(AtmosSolverCatalog.TryGet(CatalogQuietSolver.Name, out AtmosSolverDescriptor? descriptor), Is.True);
        Assert.That(descriptor!.Neighbors, Is.Null);
    }

    [Test]
    public void Names_AreOrdinalAndIncludeDeclaredSolvers()
    {
        IReadOnlyList<string> names = AtmosSolverCatalog.Names;

        Assert.That(names, Is.SupersetOf(new[] { CatalogFireSolver.Name, CatalogPortalSolver.Name, CatalogQuietSolver.Name }));
        Assert.That(names, Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
    }

    [Test]
    public void Create_ReturnsANewInstanceEachCall()
    {
        IAtmosWorldSolver first = CatalogFireSolver.Descriptor.Create();
        IAtmosWorldSolver second = CatalogFireSolver.Descriptor.Create();

        Assert.That(first, Is.TypeOf<CatalogFireSolver>());
        Assert.That(second, Is.TypeOf<CatalogFireSolver>());
        Assert.That(second, Is.Not.SameAs(first));
    }

    [Test]
    public void ConflictingRegister_ThrowsAndNamesBothTypes()
    {
        var conflict = new AtmosSolverDescriptor(
            CatalogFireSolver.Name,
            typeof(CatalogQuietSolver),
            static () => new CatalogQuietSolver());

        var exception = Assert.Throws<InvalidOperationException>(() => AtmosSolverCatalog.Register(conflict));

        Assert.That(
            exception!.Message,
            Does.Contain(typeof(CatalogFireSolver).FullName).And.Contain(typeof(CatalogQuietSolver).FullName));

        Assert.That(AtmosSolverCatalog.TryGet(CatalogFireSolver.Name, out AtmosSolverDescriptor? kept), Is.True);
        Assert.That(kept, Is.SameAs(CatalogFireSolver.Descriptor));
    }

    [Test]
    public void RegisteringTheSameSolverTwice_IsANoOp()
    {
        const string name = "numos-tests/catalog/manual";
        var first = new AtmosSolverDescriptor(name, typeof(CatalogQuietSolver), static () => new CatalogQuietSolver());
        var second = new AtmosSolverDescriptor(name, typeof(CatalogQuietSolver), static () => new CatalogQuietSolver(), interval: 3);

        AtmosSolverCatalog.Register(first);
        int count = AtmosSolverCatalog.Names.Count;
        Assert.DoesNotThrow(() => AtmosSolverCatalog.Register(first));
        Assert.DoesNotThrow(() => AtmosSolverCatalog.Register(second));
        Assert.DoesNotThrow(() => AtmosSolverCatalog.Register(CatalogFireSolver.Descriptor));

        Assert.That(AtmosSolverCatalog.Names, Has.Count.EqualTo(count));
        Assert.That(AtmosSolverCatalog.TryGet(name, out AtmosSolverDescriptor? kept), Is.True);
        Assert.That(kept, Is.SameAs(first));
    }

    [Test]
    public void UnknownName_IsNotFound()
    {
        Assert.That(AtmosSolverCatalog.TryGet("numos-tests/catalog/missing", out AtmosSolverDescriptor? descriptor), Is.False);
        Assert.That(descriptor, Is.Null);
    }

    [Test]
    public void Descriptor_RejectsInvalidArguments()
    {
        Func<IAtmosWorldSolver> create = static () => new CatalogQuietSolver();
        Type type = typeof(CatalogQuietSolver);
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => _ = new AtmosSolverDescriptor(" ", type, create));
            Assert.Throws<ArgumentException>(() => _ = new AtmosSolverDescriptor("x", typeof(string), create));
            Assert.Throws<ArgumentException>(() => _ = new AtmosSolverDescriptor("x", type, create, after: "a", before: "b"));
            Assert.Throws<ArgumentException>(() =>
                _ = new AtmosSolverDescriptor("x", type, create, runsPerSecond: 20, interval: 4));

            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AtmosSolverDescriptor("x", type, create, runsPerSecond: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AtmosSolverDescriptor("x", type, create, interval: -1));
        });
    }
}

[AtmosSolver(Name, After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20, Neighbors = AtmosSolverNeighbors.All)]
public sealed partial class CatalogFireSolver : IAtmosWorldSolver
{
    public const string Name = "numos-tests/catalog/fire";

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

[AtmosSolver(Name, Before = AtmosBuiltInSolvers.GasReactions, Interval = 7)]
internal sealed partial class CatalogPortalSolver : IAtmosWorldSolver
{
    public const string Name = "numos-tests/catalog/portal";

    [SolverNeighborSelector("numos-tests/catalog/portal#gas-links")]
    private static bool SelectGasLinks(AtmosExplicitLinkInfo link)
    {
        return (link.Flags & ExplicitLinkFlags.GasTransport) != 0;
    }

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

[AtmosSolver(Name, RunsPerSecond = 10)]
public sealed partial class CatalogQuietSolver : IAtmosWorldSolver
{
    public const string Name = "numos-tests/catalog/quiet";

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}
