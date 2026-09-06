using Numos.CoreSim.Replay;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Owns the immutable reaction definitions consumed by the built-in reaction solver.
/// </summary>
/// <remarks>
///     Register this object in <see cref="AtmosConfig.SolverConfigurations" />. Reaction IDs follow list order:
///     linear reactions first, then standard reactions. Changing definitions invalidates derived gas attachments.
/// </remarks>
/// <example>
///     <code>
///     config.SolverConfigurations.Add(new GasReactionConfig(linearReactions: [combustion]));
///     simulation.SetAtmosConfig(config);
///     </code>
/// </example>
public sealed class GasReactionConfig : IAtmosSolverConfiguration
{
    /// <summary>
    ///     Identifies the configuration consumed by the built-in reaction solver.
    /// </summary>
    public const string ConfigurationKey = "gas-reactions";

    internal readonly static GasReactionConfig Empty = new();

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
    }

    /// <summary>
    ///     Gets linear reaction definitions in reaction-ID order.
    /// </summary>
    public IReadOnlyList<LinearGasReaction> LinearReactions { get; }

    /// <summary>
    ///     Gets standard reactions, whose IDs follow all linear reactions.
    /// </summary>
    public IReadOnlyList<StandardGasReaction> StandardReactions { get; }

    /// <summary>
    ///     Gets the combined number of reactions.
    /// </summary>
    public int Count => LinearReactions.Count + StandardReactions.Count;

    /// <inheritdoc />
    public string Key => ConfigurationKey;

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

    /// <inheritdoc />
    public bool SemanticallyEquals(IAtmosSolverConfiguration other)
    {
        if (other is not GasReactionConfig reactions ||
            LinearReactions.Count != reactions.LinearReactions.Count ||
            StandardReactions.Count != reactions.StandardReactions.Count)
            return false;

        for (int i = 0; i < LinearReactions.Count; i++)
        {
            if (!LinearReactions[i].SemanticallyEquals(reactions.LinearReactions[i]))
                return false;
        }

        for (int i = 0; i < StandardReactions.Count; i++)
        {
            if (!StandardReactions[i].SemanticallyEquals(reactions.StandardReactions[i]))
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public ulong ComputeStateHash()
    {
        var hash = new AtmosStateHasher();
        hash.Add(LinearReactions.Count);
        foreach (var reaction in LinearReactions)
            reaction.AppendHash(ref hash);

        hash.Add(StandardReactions.Count);
        foreach (var reaction in StandardReactions)
            reaction.AppendHash(ref hash);

        return hash.Value;
    }

    internal static GasReactionConfig Get(IAtmosConfig config)
    {
        for (int i = 0; i < config.SolverConfigurations.Count; i++)
        {
            var settings = config.SolverConfigurations[i];
            if (settings.Key == ConfigurationKey)
            {
                return settings as GasReactionConfig ??
                       throw new InvalidOperationException(
                           $"Configuration '{ConfigurationKey}' must be a {nameof(GasReactionConfig)}.");
            }
        }

        return Empty;
    }

    internal Joule GetEnergyBalance(int reactionId)
    {
        return reactionId < LinearReactions.Count
            ? LinearReactions[reactionId].EnergyBalance
            : StandardReactions[reactionId - LinearReactions.Count].EnergyBalance;
    }

    internal PerSecond GetRateConstant(int reactionId, Kelvin temperature)
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