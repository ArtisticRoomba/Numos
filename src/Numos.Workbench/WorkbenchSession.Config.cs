using Numos.CoreSim;

namespace Numos.Workbench;

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Applies <see cref="Config" /> to the world, throwing when the world rejects it.
    /// </summary>
    /// <remarks>
    ///     A rejection leaves the world on its previous configuration, so the caller must undo its edit to
    ///     <see cref="Config" />. Prefer <see cref="ApplyConfigChange" />, which does that.
    /// </remarks>
    public void ApplyConfiguration()
    {
        var world = RequireWorld();
        ThrowIfInspecting();
        world.SetAtmosConfig(Config!);
    }

    /// <summary>
    ///     Applies <see cref="Config" /> to the world, reporting the failure instead of throwing.
    /// </summary>
    /// <param name="error">Why the world rejected the configuration, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> when the world accepted the configuration.</returns>
    /// <remarks>
    ///     On failure the world keeps its previous configuration, so the caller must undo its change to
    ///     <see cref="Config" /> or a front end will show values the simulation is not using.
    /// </remarks>
    public bool TryApplyConfiguration(out string? error)
    {
        try
        {
            ApplyConfiguration();
            error = null;
            Info("Applied the atmosphere configuration.");
            return true;
        }
        catch (KeyNotFoundException exception)
        {
            // Gas reactions resolve their species by name when the config is captured.
            error = $"a gas reaction still refers to a gas that would not be registered ({exception.Message})";
            Failed("Could not apply the atmosphere configuration", exception);
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            Failed("Could not apply the atmosphere configuration", exception);
            return false;
        }
    }

    /// <summary>
    ///     Edits <see cref="Config" /> and applies it, restoring the old values when the world rejects the edit.
    /// </summary>
    /// <param name="edit">The change to make. It can change scalars, the gas registry, and solver configurations.</param>
    /// <param name="description">What the edit does, for the log and the exception message.</param>
    /// <exception cref="ConfigurationRejectedException">The world rejected the new configuration.</exception>
    public void ApplyConfigChange(Action<AtmosConfig> edit, string description = "change the configuration")
    {
        var config = RequireConfig();
        ThrowIfInspecting();
        var previousScalars = new AtmosConfig();
        AtmosConfigSettings.CopyScalarSettings(config, previousScalars);
        var previousRegistry = new GasRegistry();
        foreach (var gas in config.GasRegistry)
            previousRegistry.Add(gas);

        var previousSolvers = config.SolverConfigurations;
        try
        {
            edit(config);
        }
        catch
        {
            Restore();
            throw;
        }

        if (TryApplyConfiguration(out string? error))
            return;

        Restore();
        var rejected = new ConfigurationRejectedException($"Could not {description}: {error}", error!);
        Failed("Configuration rejected", rejected);
        throw rejected;

        void Restore()
        {
            AtmosConfigSettings.CopyScalarSettings(previousScalars, config);
            config.GasRegistry = previousRegistry;
            config.SolverConfigurations = previousSolvers;
        }
    }

    /// <summary>
    ///     Resets every scalar setting to the library default and applies the result as one operation.
    /// </summary>
    /// <remarks>
    ///     The gas registry and solver configurations stay as they are. One operation means the timeline records
    ///     one entry.
    /// </remarks>
    /// <exception cref="ConfigurationRejectedException">The world rejected the defaults.</exception>
    public void ResetConfiguration()
    {
        ApplyConfigChange(
            config => AtmosConfigSettings.CopyScalarSettings(new AtmosConfig(), config),
            "reset the settings");

        Info("Reset every AtmosConfig setting to the library defaults.");
    }

    /// <summary>
    ///     Registers a gas and applies the configuration.
    /// </summary>
    /// <param name="gas">The gas. Its name is trimmed.</param>
    /// <returns>The new gas id.</returns>
    /// <exception cref="ArgumentException">The gas is invalid or its name is taken.</exception>
    /// <exception cref="ConfigurationRejectedException">The world rejected the new registry.</exception>
    public int AddGas(GasProperties gas)
    {
        var config = RequireConfig();
        string name = gas.Name?.Trim() ?? string.Empty;
        try
        {
            ThrowIfInspecting();
            if (name.Length == 0)
                throw new ArgumentException("Could not add the gas: a name is required.", nameof(gas));

            if (!float.IsFinite(gas.MolarHeatCapacityAtConstantVolume) ||
                gas.MolarHeatCapacityAtConstantVolume < 0f ||
                !float.IsFinite(gas.BoilingPoint) ||
                gas.BoilingPoint < 0f ||
                !float.IsFinite(gas.MolarEnthalpyOfVaporization) ||
                gas.MolarEnthalpyOfVaporization < 0f ||
                !float.IsFinite(gas.DiffusionCoefficient) ||
                gas.DiffusionCoefficient < 0f)
            {
                throw new ArgumentException(
                    $"Could not add {name}: molar Cv, boiling point, vaporization enthalpy, and diffusion coefficient " +
                    "must be finite and at least 0.",
                    nameof(gas));
            }

            foreach (var registered in config.GasRegistry)
            {
                if (registered.Name == name)
                {
                    throw new ArgumentException(
                        $"Could not add {name}: a gas with that name is already registered.",
                        nameof(gas));
                }
            }

            gas.Name = name;
            ApplyConfigChange(c => c.GasRegistry.Add(gas), $"add {name}");
        }
        catch (Exception exception) when (exception is not ConfigurationRejectedException)
        {
            Failed("Could not add the gas", exception);
            throw;
        }

        int gasId = config.GasRegistry.Count - 1;
        Info($"Added gas {name} with ID {gasId}.");
        return gasId;
    }

    /// <summary>
    ///     Removes a gas from the registry and applies the configuration.
    /// </summary>
    /// <param name="gasId">The registered gas id.</param>
    /// <exception cref="ArgumentOutOfRangeException">The id is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    ///     A chunk already holds the gas or a later gas. Removing it would shift ids, so the caller must remove those
    ///     chunks first.
    /// </exception>
    /// <exception cref="ConfigurationRejectedException">The world rejected the new registry.</exception>
    public void RemoveGas(int gasId)
    {
        var world = RequireWorld();
        var config = Config!;
        try
        {
            ThrowIfInspecting();
            if (gasId < 0 || gasId >= config.GasRegistry.Count)
                throw new ArgumentOutOfRangeException(nameof(gasId), $"Gas id {gasId} is not registered.");

            string name = config.GasRegistry[gasId].Name;
            foreach (var simulation in world.Simulations)
            foreach (var handle in simulation.GetChunkHandles())
            {
                var snapshot = simulation.GetChunkSnapshot(handle);
                if (snapshot.Gases.Any(gas => gas.GasId >= gasId))
                {
                    throw new InvalidOperationException(
                        $"Could not remove {name}: it, or a later gas ID, has already been used by a chunk. " +
                        "Remove the affected chunks first so gas IDs remain stable.");
                }
            }

            ApplyConfigChange(c => c.GasRegistry.RemoveAt(gasId), $"remove {name}");
            Info($"Removed gas {name}.");
        }
        catch (Exception exception) when (exception is not ConfigurationRejectedException)
        {
            Failed("Could not remove the gas", exception);
            throw;
        }
    }
}
