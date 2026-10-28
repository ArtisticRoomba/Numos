using Numos.CoreSim;

namespace Numos.Tests;

/// <summary>
///     Supplies named gases for tests that exercise storage and lifecycle rather than a particular physical species.
/// </summary>
internal sealed class TestAtmosConfig : AtmosConfig
{
    internal TestAtmosConfig()
    {
        for (int index = 0; index < 8; index++)
        {
            GasRegistry.Add(TestGases.Create($"TestGas{index}"));
        }
    }
}

internal static class TestGases
{
    internal static GasProperties Create(
        string name,
        float diffusionCoefficient = AtmosConfigDefaults.DefaultDiffusionCoefficient,
        float molarMass = 0f)
    {
        return new GasProperties
        {
            Name = name,
            // Zero selects the configuration's heat-capacity fallback, and the same for molar mass.
            MolarHeatCapacityAtConstantVolume = 0f,
            DiffusionCoefficient = diffusionCoefficient,
            MolarMass = molarMass
        };
    }
}