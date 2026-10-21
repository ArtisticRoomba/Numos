using Numos.CoreSim.State;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Owns the immutable reaction definitions consumed by the built-in reaction solver.
/// </summary>
/// <remarks>
///     Register this object in <see cref="AtmosConfig.SolverConfigurations" />. Reaction IDs follow list order:
///     linear reactions first, then standard reactions. Changing definitions invalidates data derived from the
///     configuration.
/// </remarks>
/// <example>
///     <code>
///     config.SolverConfigurations.Add(new GasReactionConfig(linearReactions: [combustion]));
///     simulation.SetAtmosConfig(config);
///     </code>
/// </example>
[NumosState(Key = ConfigurationKey)]
public sealed partial class GasReactionConfig : IAtmosSolverConfiguration
{
    /// <summary>
    ///     Identifies the configuration consumed by the built-in reaction solver.
    /// </summary>
    public const string ConfigurationKey = "numos/gas-reactions";

    internal readonly static GasReactionConfig Empty = new();

    // Reaction-ID-indexed and built once at construction, because reaction definitions never change after capture.
    // Callers that need a reaction's energy balance on every iteration skip the Linear-vs-Standard branch
    // and the IReadOnlyList indexer call.
    [NotState]
    private readonly JoulePerMole[] _energyBalances;

    /// <summary>
    ///     Captures ordered reaction definitions without retaining mutable source collections.
    /// </summary>
    /// <param name="linearReactions">Linear reactions, or null for none.</param>
    /// <param name="standardReactions">Standard rate-law reactions, or null for none.</param>
    /// <remarks>Gas names are validated when the simulation captures this configuration.</remarks>
    public GasReactionConfig(
        IEnumerable<LinearGasReaction>? linearReactions = null,
        IEnumerable<StandardGasReaction>? standardReactions = null)
    {
        LinearReactions = Array.AsReadOnly(linearReactions?.ToArray() ?? []);
        StandardReactions = Array.AsReadOnly(standardReactions?.ToArray() ?? []);

        _energyBalances = new JoulePerMole[LinearReactions.Count + StandardReactions.Count];
        for (int i = 0; i < LinearReactions.Count; i++)
            _energyBalances[i] = LinearReactions[i].EnergyBalance;

        for (int i = 0; i < StandardReactions.Count; i++)
            _energyBalances[LinearReactions.Count + i] = StandardReactions[i].EnergyBalance;
    }

    /// <summary>
    ///     Gets linear reaction definitions in reaction-ID order.
    /// </summary>
    [StateField(1)]
    public IReadOnlyList<LinearGasReaction> LinearReactions { get; }

    /// <summary>
    ///     Gets standard reactions, whose IDs follow all linear reactions.
    /// </summary>
    [StateField(2)]
    public IReadOnlyList<StandardGasReaction> StandardReactions { get; }

    /// <summary>
    ///     Gets the combined number of reactions.
    /// </summary>
    public int Count => LinearReactions.Count + StandardReactions.Count;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="gasRegistry" /> is null.</exception>
    /// <exception cref="KeyNotFoundException">A reaction references an unregistered gas name.</exception>
    /// <exception cref="ArgumentException">A reaction contains duplicate definitions of the same gas name.</exception>
    public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry)
    {
        ArgumentNullException.ThrowIfNull(gasRegistry);
        foreach (var reaction in LinearReactions)
            reaction.ValidateGasRegistry(gasRegistry);

        foreach (var reaction in StandardReactions)
            reaction.ValidateGasRegistry(gasRegistry);

        return this;
    }

    internal static GasReactionConfig Get(IAtmosConfig config)
    {
        return SolverConfigurationLookup.TryGet(config.SolverConfigurations, ConfigurationKey, out GasReactionConfig? reactions)
            ? reactions
            : Empty;
    }

    internal JoulePerMole GetEnergyBalance(int reactionId) => _energyBalances[reactionId];

    // Unlike GetEnergyBalance, this cannot be precomputed at construction.
    // It depends on the caller's temperature, which changes for every voxel.
    internal ConcentrationPerSecond GetRateConstant(int reactionId, Kelvin temperature)
    {
        return reactionId < LinearReactions.Count
            ? LinearReactions[reactionId].GetRateConstantForTemperature(temperature)
            : StandardReactions[reactionId - LinearReactions.Count].GetRateConstant(temperature);
    }

    internal GasReactionData CreateGasData(int gasId, GasProperties gas)
    {
        var data = new GasReactionData(gasId, gas.Name, Count);
        int reactionId = 0;
        foreach (var reaction in LinearReactions)
            reaction.AppendGasData(data, reactionId++);

        foreach (var reaction in StandardReactions)
            reaction.AppendGasData(data, reactionId++);

        return data;
    }
}