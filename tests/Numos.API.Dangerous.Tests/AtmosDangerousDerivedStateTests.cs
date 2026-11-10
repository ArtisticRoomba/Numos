using Numos.CoreSim;
using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousDerivedStateTests
{
    private const ushort Voxel = 3;

    /// <summary>
    ///     Builds two chunks that should hold the same gas. The first reaches it through raw writes that leave the
    ///     caches stale. The second reaches it through the recorded API, which keeps its caches current.
    /// </summary>
    private static (AtmosSimulation Stale, AtmosChunkHandle StaleHandle, AtmosSimulation Reference, AtmosChunkHandle ReferenceHandle)
        CreatePair()
    {
        var stale = new AtmosSimulation(new TestAtmosConfig(), 2, 2, 2);
        var staleHandle = stale.CreateAndRegisterChunk(default);
        stale.AddGasToVoxel(staleHandle, Voxel, "TestGas0", 1f, 300f);

        var reference = new AtmosSimulation(new TestAtmosConfig(), 2, 2, 2);
        var referenceHandle = reference.CreateAndRegisterChunk(default);
        reference.AddGasToVoxel(referenceHandle, Voxel, "TestGas0", 2.5f, 450f);

        var chunk = stale.Dangerous().GetChunk(staleHandle);
        chunk.GetGasChannel(0).Moles[Voxel] = 2.5f;
        chunk.Temperature[Voxel] = 450f;
        return (stale, staleHandle, reference, referenceHandle);
    }

    [Test]
    public void RefreshDerivedState_CorrectsStalePressureAndHeatCapacity()
    {
        var (stale, staleHandle, reference, referenceHandle) = CreatePair();
        using var _ = stale;
        using var __ = reference;
        var view = stale.Dangerous().GetChunk(staleHandle);
        var expected = reference.Dangerous().GetChunk(referenceHandle);
        float stalePressure = view.TotalPressure[Voxel];
        float staleHeatCapacity = view.TotalHeatCapacity[Voxel];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.TotalPressure[Voxel], Is.GreaterThan(0f));
            Assert.That(stalePressure, Is.Not.EqualTo(expected.TotalPressure[Voxel]).Within(1e-3f));
            Assert.That(staleHeatCapacity, Is.Not.EqualTo(expected.TotalHeatCapacity[Voxel]).Within(1e-3f));
        }

        view.RefreshDerivedState();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(view.TotalPressure[Voxel], Is.EqualTo(expected.TotalPressure[Voxel]).Within(1e-3f));
            Assert.That(view.TotalHeatCapacity[Voxel], Is.EqualTo(expected.TotalHeatCapacity[Voxel]).Within(1e-3f));
            Assert.That(view.TotalPressure[0], Is.Zero, "an empty voxel reads zero");
        }
    }

    [Test]
    public void RefreshDerivedState_FromASolverView_CorrectsStaleCaches()
    {
        var (stale, staleHandle, reference, referenceHandle) = CreatePair();
        using var _ = stale;
        using var __ = reference;
        float expectedPressure = reference.Dangerous().GetChunk(referenceHandle).TotalPressure[Voxel];
        float expectedHeatCapacity = reference.Dangerous().GetChunk(referenceHandle).TotalHeatCapacity[Voxel];
        float pressure = -1f;
        float heatCapacity = -1f;
        stale.World.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        stale.World.Solvers.SetEnabled(AtmosBuiltInSolvers.VacuumCleanup, false);
        stale.World.Solvers.Register(
            "tests/dangerous-derived/refresh",
            context =>
            {
                var live = context.Chunks(stale)[0].Dangerous();
                live.RefreshDerivedState();
                pressure = live.TotalPressure[Voxel];
                heatCapacity = live.TotalHeatCapacity[Voxel];
            });

        stale.World.Tick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pressure, Is.EqualTo(expectedPressure).Within(1e-3f));
            Assert.That(heatCapacity, Is.EqualTo(expectedHeatCapacity).Within(1e-3f));
        }
    }

    [Test]
    public void RefreshDerivedState_OnAMixturePageView_Throws()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var mixture = world.CreateGasMixture(3f, 350f);
        var failed = false;
        world.Solvers.Register(
            "tests/dangerous-derived/page",
            context =>
            {
                try
                {
                    context.GasMixtures[0].Dangerous().Chunk.RefreshDerivedState();
                }
                catch (InvalidOperationException)
                {
                    failed = true;
                }
            });

        world.Tick();

        Assert.That(failed, Is.True);
    }
}
