using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

public class EulerSweepTests
{
    private const float Temperature = 293f;
    private const float AirMolarHeatCapacity = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;

    [Test]
    public void Sweep_WritesANonFiniteSpeciesEntryAsZeroAndCountsTheCell()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = 1f,
            GasRegistry =
            [
                new GasProperties
                {
                    Name = "Air",
                    MolarMass = 0.02897f,
                    MolarHeatCapacityAtConstantVolume = AirMolarHeatCapacity
                }
            ]
        }.CreateSnapshot();

        var tickConfig = new AtmosSolverConfigSnapshot();
        tickConfig.Capture(config);

        var chunk = new AtmosChunk(3, 1, 1);
        chunk.SetChunkClassification(1);
        chunk.Wake();
        float moles = AtmosPhysicalConstants.StandardAtmosphericPressure /
                      (AtmosPhysicalConstants.MolarGasConstant * Temperature);

        for (ushort x = 0; x < 3; x++)
            chunk.InjectGasToVoxel(x, 0, moles, Temperature, AirMolarHeatCapacity, tickConfig.PressurePerMoleKelvin);

        var workspace = new EulerChunkWorkspace();
        workspace.Bind(chunk, tickConfig);
        EulerFaceTopology.Build(chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
        workspace.Gather(tickConfig);
        // The gather already derived the cell from a finite row, so the NaN enters through the species update alone.
        workspace.Read.Species[0]![1] = float.NaN;

        EulerSweep.RunPencils(workspace, 0, 0, 1, 1e-3f, 1f, tickConfig);

        float[] written = workspace.Write.Species[0]!;
        TestContext.Out.WriteLine(
            $"written: {string.Join(", ", written.Take(3))}; non-finite cells: {workspace.PencilCounters[0].NonFiniteCells}");
        Assert.Multiple(() =>
        {
            Assert.That(written[1], Is.Zero);
            Assert.That(written.Take(3).All(float.IsFinite), Is.True, "no non-finite species reaches the write buffer");
            Assert.That(workspace.PencilCounters[0].NonFiniteCells, Is.GreaterThanOrEqualTo(1));
            Assert.That(double.IsFinite(workspace.Write.HeatCapacity[1]), Is.True);
        });
    }
}
