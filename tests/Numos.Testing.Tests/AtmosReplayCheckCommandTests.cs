using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Tests;

namespace Numos.Testing.Tests;

// The chunk field registry is process-wide, so the key is namespaced to this file.
internal static partial class ReplayCheckCommandData
{
    [ChunkData("tests/replay-check/vented")]
    public static partial ChunkField<float> Vented { get; }
}

/// <summary>
///     A solver whose only host input is a command, so the check covers commands from recording to a second world.
/// </summary>
internal sealed partial class VentCommandSolver : IAtmosWorldSolver
{
    [NumosSerialize] public int Vents;
    [NumosIgnore] public AtmosWorld? World;

    [SolverCommand]
    public partial void Vent(AtmosCellRef cell, float moles);

    public void Solve(AtmosWorldSolverContext context)
    {
    }

    private void ApplyVent(AtmosCellRef cell, float moles)
    {
        World!.TryGetSimulation(cell.Simulation, out var simulation);
        simulation!.AddGasToVoxel(cell.Chunk, cell.LocalVoxelIndex, 0, moles, 310f);
        float vented = simulation.TryGetChunkField(cell.Chunk, ReplayCheckCommandData.Vented, out var values)
            ? values.AsSpan()[cell.LocalVoxelIndex]
            : 0f;

        simulation.SetChunkData(cell.Chunk, ReplayCheckCommandData.Vented, cell.LocalVoxelIndex, vented + moles);
        Vents++;
    }
}

[TestFixture]
public sealed class AtmosReplayCheckCommandTests
{
    [Test]
    public void SolverDrivenThroughCommands_PassesEveryCheck()
    {
        var result = AtmosReplayCheck.Run(
            static () =>
            {
                var world = new AtmosWorld(new TestAtmosConfig());
                var simulation = world.CreateSimulation(4, 4, 1);
                SimSetup.CreateOpenChunk(simulation, Int3.Zero);
                var solver = new VentCommandSolver { World = world };
                world.Solvers.Register("tests/replay-check/vent", solver);
                world.Services.Register(solver);
                return world;
            },
            static (world, tick) =>
            {
                if (tick % 9 != 0)
                    return;

                var simulation = world.Simulations[0];
                var cell = simulation.GetCellRef(new AtmosChunkHandle(Int3.Zero), (ushort)(tick % 16));
                world.Services.Get<VentCommandSolver>().Vent(cell, 0.25f * (tick % 5 + 1));
            },
            120);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => string.Join("\n", result.Failures));
            Assert.That(result.Skipped, Is.Empty);
        });
    }
}
