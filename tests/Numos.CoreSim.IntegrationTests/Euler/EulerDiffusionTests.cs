using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     The post passes of the Euler stage: species diffusion inside a chunk and across chunk faces, and vacuum
///     cleanup.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class EulerDiffusionTests
{
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;
    private const float Diffusivity = 0.02f;

    /// <summary>
    ///     A composition step at uniform pressure and temperature is a stationary contact, which the HLLC flux
    ///     leaves bitwise alone, so the only thing the Euler tick does to the species is the diffusion pass. The
    ///     legacy pipeline moves no bulk gas either (uniform pressure, shown by the control run), so the two
    ///     pipelines diffuse from identical inputs and must agree bit for bit. Both Euler schedules are covered.
    /// </summary>
    [TestCase(1)]
    [TestCase(1 << 16)]
    public void OneChunkAtRest_DiffusesBitwiseLikeLegacy(int workerCount)
    {
        const int width = 6;
        const int height = 3;
        var config = CreateDiffusingConfig();
        using var euler = SimTestHelpers.CreateEulerSimulation(config, width, height, 1, out var eulerChunk);
        using var legacy = CreateLegacySimulation(config, width, height, 1, out var legacyChunk);
        using var control = CreateLegacySimulation(CreateDiffusingConfig(diffusivity: 0f), width, height, 1, out var controlChunk);
        FillSplit(euler, config, eulerChunk, width, height);
        FillSplit(legacy, config, legacyChunk, width, height);
        FillSplit(control, config, controlChunk, width, height);
        var initial = control.GetChunkSnapshot(controlChunk);

        EulerFlowSolver.WorkerCountOverride = workerCount;
        try
        {
            euler.Tick();
        }
        finally
        {
            EulerFlowSolver.WorkerCountOverride = null;
        }

        legacy.Tick();
        control.Tick();

        var expected = legacy.GetChunkSnapshot(legacyChunk);
        var actual = euler.GetChunkSnapshot(eulerChunk);
        var unmoved = control.GetChunkSnapshot(controlChunk);
        TestContext.Out.WriteLine(
            $"{euler.LastFlowDiagnostics}; unit gas at (0,0): {SimTestHelpers.Moles(actual, SimTestHelpers.EulerUnitGasId, 0):E3} mol");
        Assert.That(euler.LastFlowDiagnostics.Substeps, Is.GreaterThan(0), "the Euler stage ran");
        Assert.Multiple(() =>
        {
            float referencePressure = actual.TotalPressure[0];
            for (int index = 0; index < width * height; index++)
            {
                foreach (int gasId in new[] { SimTestHelpers.EulerAirGasId, SimTestHelpers.EulerUnitGasId })
                {
                    Assert.That(
                        BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(unmoved, gasId, index)),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(initial, gasId, index))),
                        $"legacy bulk flow moved gas {gasId} at {index}");

                    Assert.That(
                        BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(actual, gasId, index)),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(expected, gasId, index))),
                        $"gas {gasId} at {index}: euler {SimTestHelpers.Moles(actual, gasId, index):R}, legacy {SimTestHelpers.Moles(expected, gasId, index):R}");
                }

                Assert.That(actual.VelocityX[index], Is.Zero, $"velocity at {index}");
                Assert.That(actual.VelocityY[index], Is.Zero, $"velocity at {index}");
                Assert.That(actual.TotalPressure[index], Is.EqualTo(referencePressure).Within(1e-5).Percent, $"pressure at {index}");
            }

            // The species interface sits between columns 2 and 3. The unit gas has to have crossed it.
            Assert.That(SimTestHelpers.Moles(actual, SimTestHelpers.EulerUnitGasId, 2), Is.GreaterThan(0f));
            Assert.That(SimTestHelpers.Moles(actual, SimTestHelpers.EulerAirGasId, 3), Is.GreaterThan(0f));
        });
    }

    /// <summary>
    ///     The face exchange is exactly antisymmetric, so the only loss is float rounding of the channel values
    ///     it is added to (about 41.5 mol per voxel, so 2e-6 mol per rounding) and of the sweeps' own species
    ///     updates every substep. Twenty ticks of that land around <c>3e-7</c> relative, the same order the Euler
    ///     cross-chunk fixture allows for one tick.
    /// </summary>
    [Test]
    public void AcrossAChunkFace_SpeciesCrossAndTotalsAreConserved()
    {
        const int width = 4;
        var config = CreateDiffusingConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, 2, 2, out var left);
        var right = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(simulation, left, width, 2, 2, SimTestHelpers.EulerAirGasName, moles);
        FillRoom(simulation, right, width, 2, 2, SimTestHelpers.EulerUnitGasName, moles);
        double initialAir = TotalMoles(simulation, SimTestHelpers.EulerAirGasId, left, right);
        double initialUnit = TotalMoles(simulation, SimTestHelpers.EulerUnitGasId, left, right);

        for (int tick = 0; tick < 20; tick++)
            simulation.Tick();

        var leftSnapshot = simulation.GetChunkSnapshot(left);
        var rightSnapshot = simulation.GetChunkSnapshot(right);
        int leftFace = SimTestHelpers.Index(width - 1, 0, 0, width, 2);
        TestContext.Out.WriteLine(
            $"left face: air {SimTestHelpers.Moles(leftSnapshot, SimTestHelpers.EulerAirGasId, leftFace):F5}, " +
            $"unit {SimTestHelpers.Moles(leftSnapshot, SimTestHelpers.EulerUnitGasId, leftFace):E3}; " +
            $"right face: unit {SimTestHelpers.Moles(rightSnapshot, SimTestHelpers.EulerUnitGasId, 0):F5}, " +
            $"air {SimTestHelpers.Moles(rightSnapshot, SimTestHelpers.EulerAirGasId, 0):E3}");

        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(leftSnapshot, SimTestHelpers.EulerUnitGasId, leftFace), Is.GreaterThan(0f), "unit gas crossed into the left chunk");
            Assert.That(SimTestHelpers.Moles(rightSnapshot, SimTestHelpers.EulerAirGasId, 0), Is.GreaterThan(0f), "air crossed into the right chunk");
            Assert.That(TotalMoles(simulation, SimTestHelpers.EulerAirGasId, left, right), Is.EqualTo(initialAir).Within(1e-4).Percent, "air");
            Assert.That(TotalMoles(simulation, SimTestHelpers.EulerUnitGasId, left, right), Is.EqualTo(initialUnit).Within(1e-4).Percent, "unit");
            foreach (var snapshot in new[] { leftSnapshot, rightSnapshot })
            {
                for (int index = 0; index < snapshot.Temperature.Length; index++)
                {
                    Assert.That(snapshot.TotalPressure[index], Is.EqualTo(Atmosphere).Within(1e-3).Percent, $"pressure at {index}");
                    Assert.That(float.IsFinite(snapshot.Temperature[index]), $"temperature at {index}");
                }
            }
        });
    }

    /// <summary>
    ///     After one tick the only cross-chunk term is the face exchange itself (each chunk is still uniform
    ///     inside, so its intra-chunk diffusion nets to exactly zero), and that exchange must not care which chunk
    ///     has the lower coordinate: the mirrored arrangement produces the same bits in the mirrored cells.
    /// </summary>
    [Test]
    public void AcrossAChunkFace_OneTickMirrorsBitwise()
    {
        const int width = 4;
        var config = CreateDiffusingConfig();
        using var forward = SimTestHelpers.CreateEulerSimulation(config, width, 1, 1, out var forwardLeft);
        var forwardRight = SimTestHelpers.CreateOpenChunk(forward, Int3.PosX);
        using var mirrored = SimTestHelpers.CreateEulerSimulation(config, width, 1, 1, out var mirroredLeft);
        var mirroredRight = SimTestHelpers.CreateOpenChunk(mirrored, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(forward, forwardLeft, width, 1, 1, SimTestHelpers.EulerAirGasName, moles);
        FillRoom(forward, forwardRight, width, 1, 1, SimTestHelpers.EulerUnitGasName, moles);
        FillRoom(mirrored, mirroredLeft, width, 1, 1, SimTestHelpers.EulerUnitGasName, moles);
        FillRoom(mirrored, mirroredRight, width, 1, 1, SimTestHelpers.EulerAirGasName, moles);

        forward.Tick();
        mirrored.Tick();

        var expectedLeft = forward.GetChunkSnapshot(forwardLeft);
        var expectedRight = forward.GetChunkSnapshot(forwardRight);
        var actualLeft = mirrored.GetChunkSnapshot(mirroredLeft);
        var actualRight = mirrored.GetChunkSnapshot(mirroredRight);
        float received = SimTestHelpers.Moles(expectedRight, SimTestHelpers.EulerAirGasId, 0);
        TestContext.Out.WriteLine($"air across the face: {received:E3} mol");
        Assert.That(received, Is.GreaterThan(0f));
        Assert.Multiple(() =>
        {
            for (int x = 0; x < 2 * width; x++)
            {
                int m = 2 * width - 1 - x;
                var expected = x < width ? expectedLeft : expectedRight;
                var actual = m < width ? actualLeft : actualRight;
                foreach (int gasId in new[] { SimTestHelpers.EulerAirGasId, SimTestHelpers.EulerUnitGasId })
                {
                    Assert.That(
                        BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(actual, gasId, m % width)),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(expected, gasId, x % width))),
                        $"gas {gasId} at {x}");
                }

                Assert.That(
                    BitConverter.SingleToInt32Bits(actual.Temperature[m % width]),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(expected.Temperature[x % width])), $"temperature at {x}");
            }
        });
    }

    /// <summary>
    ///     Cleanup under Euler has the legacy semantics: a voxel below the threshold is emptied exactly when every
    ///     air neighbour is below it too. A run with the threshold at zero shows the field cleanup saw, since the
    ///     threshold is read by nothing else in the tick.
    /// </summary>
    [Test]
    public void VacuumCleanup_EmptiesOnlyVoxelsWithNoPressurisedNeighbour()
    {
        const int length = 24;
        const float threshold = 20f;
        var config = SimTestHelpers.CreateEulerConfig();
        config.VacuumThreshold = threshold;
        var reference = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, length, 1, 1, out var chunk);
        using var unclean = SimTestHelpers.CreateEulerSimulation(reference, length, 1, 1, out var uncleanChunk);
        float moles = SimTestHelpers.MolesFor(config, 400f, Temperature);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);
        unclean.AddGasToVoxel(uncleanChunk, 0, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);

        simulation.Tick();
        unclean.Tick();

        var cleaned = simulation.GetChunkSnapshot(chunk);
        var field = unclean.GetChunkSnapshot(uncleanChunk);
        int emptied = 0;
        int survivedBelowThreshold = 0;
        Assert.Multiple(() =>
        {
            for (int x = 0; x < length; x++)
            {
                bool below = field.TotalPressure[x] < threshold;
                bool leftBelow = x == 0 || field.TotalPressure[x - 1] < threshold;
                bool rightBelow = x == length - 1 || field.TotalPressure[x + 1] < threshold;
                float actual = SimTestHelpers.Moles(cleaned, SimTestHelpers.EulerAirGasId, x);
                float untouched = SimTestHelpers.Moles(field, SimTestHelpers.EulerAirGasId, x);
                Assert.That(float.IsFinite(actual) && float.IsFinite(cleaned.Temperature[x]) && float.IsFinite(cleaned.VelocityX[x]), $"finite at {x}");
                if (below && leftBelow && rightBelow)
                {
                    emptied++;
                    Assert.That(actual, Is.Zero, $"moles at {x}");
                    Assert.That(cleaned.TotalPressure[x], Is.Zero, $"pressure at {x}");
                    Assert.That(cleaned.VelocityX[x], Is.Zero, $"velocity at {x}");
                }
                else
                {
                    if (below)
                        survivedBelowThreshold++;

                    Assert.That(
                        BitConverter.SingleToInt32Bits(actual),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(untouched)), $"moles at {x}");
                }
            }
        });

        TestContext.Out.WriteLine(
            $"{simulation.LastFlowDiagnostics}; emptied {emptied}, kept {survivedBelowThreshold} below {threshold} Pa next to pressurised gas");
        Assert.That(emptied, Is.GreaterThan(0), "nothing was cleaned up");
        Assert.That(survivedBelowThreshold, Is.GreaterThan(0), "no sub-threshold front survived next to pressurised gas");
    }

    /// <summary>
    ///     The Euler configuration with both gases diffusing.
    /// </summary>
    private static AtmosConfig CreateDiffusingConfig(float diffusivity = Diffusivity)
    {
        var config = SimTestHelpers.CreateEulerConfig();
        var registry = new GasRegistry();
        for (int gasId = 0; gasId < config.GasRegistry.Count; gasId++)
            registry.Add(config.GasRegistry[gasId] with { DiffusionCoefficient = diffusivity });

        config.GasRegistry = registry;
        return config;
    }

    /// <summary>
    ///     The same chunk on the legacy pipeline, with the same two stages disabled as
    ///     <see cref="SimTestHelpers.CreateEulerSimulation" /> disables.
    /// </summary>
    private static AtmosSimulation CreateLegacySimulation(
        AtmosConfig config, int width, int height, int depth, out AtmosChunkHandle chunk)
    {
        var simulation = new AtmosSimulation(config, width, height, depth);
        simulation.World.Solvers.ResetToDefaults(AtmosTransportModel.Legacy);
        simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
        simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false);
        chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        return simulation;
    }

    /// <summary>
    ///     Air in the left half of the columns and the unit gas in the right half, the same moles everywhere.
    /// </summary>
    private static void FillSplit(
        AtmosSimulation simulation, AtmosConfig config, AtmosChunkHandle chunk, int width, int height)
    {
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            simulation.AddGasToVoxel(
                chunk, x, y, 0, x < width / 2 ? SimTestHelpers.EulerAirGasName : SimTestHelpers.EulerUnitGasName,
                moles, Temperature);
        }
    }

    private static void FillRoom(
        AtmosSimulation simulation, AtmosChunkHandle chunk,
        int width, int height, int depth, string gas, float moles)
    {
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, gas, moles, Temperature);
    }

    private static double TotalMoles(AtmosSimulation simulation, int gasId, params AtmosChunkHandle[] chunks)
    {
        double total = 0d;
        foreach (var chunk in chunks)
        {
            AtmosChunkSnapshot snapshot = simulation.GetChunkSnapshot(chunk);
            for (int index = 0; index < snapshot.Temperature.Length; index++)
                total += SimTestHelpers.Moles(snapshot, gasId, index);
        }

        return total;
    }
}
