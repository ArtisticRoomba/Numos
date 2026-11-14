using Numos.API;

namespace Numos.Tests;

internal static partial class SimMetrics
{
    internal static float TotalMoles(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        return TotalMoles(simulation.GetChunkSnapshot(chunk));
    }
}