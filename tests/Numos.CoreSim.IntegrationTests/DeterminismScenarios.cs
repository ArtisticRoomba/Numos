namespace Numos.CoreSim.IntegrationTests;

internal static class DeterminismScenarios
{
    /// <summary>
    ///     Grows the deterministic config's registry to <paramref name="gasCount" /> gases whose diffusion coefficient
    ///     and heat capacity step with the gas id, so every channel carries different numbers through the solvers.
    /// </summary>
    /// <returns>The gas names, indexed by gas id.</returns>
    internal static string[] AddSteppedGases(
        AtmosConfig config,
        int gasCount,
        string namePrefix,
        float diffusionStep,
        float baseHeatCapacity)
    {
        string[] gasNames = new string[gasCount];
        gasNames[0] = SimConfigs.FirstGasName;
        gasNames[1] = SimConfigs.SecondGasName;

        for (int gas = 0; gas < gasNames.Length; gas++)
        {
            gasNames[gas] ??= $"{namePrefix}{gas}";
            if (gas >= config.GasRegistry.Count)
                config.GasRegistry.Add(TestGases.Create(gasNames[gas], 0.01f + gas * diffusionStep));

            var properties = config.GasRegistry[gas];
            properties.DiffusionCoefficient = 0.01f + gas * diffusionStep;
            properties.MolarHeatCapacityAtConstantVolume = baseHeatCapacity + gas;
            config.GasRegistry.Replace(gas, properties);
        }

        return gasNames;
    }
}
