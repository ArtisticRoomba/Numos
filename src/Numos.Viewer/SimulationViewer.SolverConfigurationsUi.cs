using System.Globalization;
using System.Text;
using ImGuiNET;
using Numos.CoreSim;
using Numos.CoreSim.GasReactions;
using Numos.Viewer.Ui;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    /// <summary>
    ///     View of the configured <see cref="IAtmosSolverConfiguration" /> entries, with details for the
    ///     configuration types the viewer knows about.
    /// </summary>
    private static void DrawSolverConfigurations(IReadOnlyList<IAtmosSolverConfiguration> configurations)
    {
        if (configurations.Count == 0)
        {
            ImGui.TextUnformatted("No solver configurations.");
            return;
        }

        ImGui.TextUnformatted($"Configurations: {configurations.Count}");
        for (int i = 0; i < configurations.Count; i++)
        {
            var configuration = configurations[i];
            ImGui.SeparatorText($"Configuration {i}:");
            ImGui.TextUnformatted(configuration.Key);
            using (var table = ImGuiExtensions.BeginDefinitionTable($"solver-configuration-{i}"))
            {
                if (table.IsVisible)
                {
                    ImGuiExtensions.DefinitionRow("Type", configuration.GetType().Name);
                    ImGuiExtensions.DefinitionRow(
                        "State hash",
                        configuration.ComputeStateHash().ToString("X16", CultureInfo.InvariantCulture));
                }
            }

            if (configuration is GasReactionConfig reactions)
                DrawGasReactionConfig(reactions);
        }
    }

    private static void DrawGasReactionConfig(GasReactionConfig reactions)
    {
        if (reactions.Count == 0)
        {
            ImGui.TextUnformatted("No reactions.");
            return;
        }

        var countLinearReactions = reactions.LinearReactions.Count;
        ImGui.TextUnformatted($"Linear Reactions: {countLinearReactions}");

        if (countLinearReactions == 0)
        {
            ImGui.TextDisabled($"-");
        }

        // Reaction IDs follow GasReactionConfig's order: linear reactions first, then standard reactions.
        int reactionId = 0;
        foreach (var reaction in reactions.LinearReactions)
        {
            int id = reactionId++;
            if (!ImGui.TreeNode($"#{id} Linear: {FormatEquation(reaction.Input, reaction.Output)}##reaction-{id}"))
                continue;

            DrawLinearReaction(reaction, id);
            ImGui.TreePop();
        }

        var countStandardReactions = reactions.StandardReactions.Count;
        ImGui.TextUnformatted($"Standard Reactions: {countStandardReactions}");

        if (countStandardReactions == 0)
        {
            ImGui.TextDisabled($"-");
        }

        foreach (var reaction in reactions.StandardReactions)
        {
            int id = reactionId++;
            if (!ImGui.TreeNode($"#{id} Standard: {FormatEquation(reaction.Input, reaction.Output)}##reaction-{id}"))
                continue;

            DrawStandardReaction(reaction, id);
            ImGui.TreePop();
        }
    }

    private static void DrawLinearReaction(LinearGasReaction reaction, int id)
    {
        using (var table = ImGuiExtensions.BeginDefinitionTable($"linear-reaction-{id}"))
        {
            if (table.IsVisible)
            {
                ImGuiExtensions.DefinitionRow("Energy balance", FormatMolarEnergy(reaction.EnergyBalance));
                ImGuiExtensions.DefinitionRow(
                    "Low bound",
                    $"{FormatNumber(reaction.LowTemperatureBound)} K -> {FormatNumber(reaction.LowTempSpeed)} mol/(m³·s)" +
                    (reaction.LowStrict ? ", strict" : ", extrapolated"));
                ImGuiExtensions.DefinitionRow(
                    "High bound",
                    $"{FormatNumber(reaction.HighTemperatureBound)} K -> {FormatNumber(reaction.HighTempSpeed)} mol/(m³·s)" +
                    (reaction.HighStrict ? ", strict" : ", extrapolated"));
            }
        }

        if (reaction.SpeedFactors.Count == 0)
            return;

        ImGui.TextDisabled("Speed factors (concentration in mol/m³ -> multiplier)");
        using var factors = ImGuiExtensions.BeginDefinitionTable($"linear-reaction-{id}-factors");
        if (!factors.IsVisible)
            return;

        foreach (var factor in reaction.SpeedFactors
                     .OrderBy(static factor => factor.Gas.Name, StringComparer.Ordinal)
                     .ThenBy(static factor => factor.LowMolarityBound))
        {
            ImGuiExtensions.DefinitionRow(
                factor.Gas.Name,
                $"{FormatNumber(factor.LowMolarityBound)} -> {FormatNumber(factor.LowMolaritySpeed)}" +
                (factor.LowStrict ? " (strict)" : string.Empty) +
                $", {FormatNumber(factor.HighMolarityBound)} -> {FormatNumber(factor.HighMolaritySpeed)}" +
                (factor.HighStrict ? " (strict)" : string.Empty));
        }
    }

    private static void DrawStandardReaction(StandardGasReaction reaction, int id)
    {
        using var table = ImGuiExtensions.BeginDefinitionTable($"standard-reaction-{id}");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow("Energy balance", FormatMolarEnergy(reaction.EnergyBalance));
        ImGuiExtensions.DefinitionRow("Arrhenius factor (A)", FormatNumber(reaction.ArrheniusFactor));
        ImGuiExtensions.DefinitionRow("Activation energy (Ea)", FormatMolarEnergy(reaction.ActivationEnergy));
        ImGuiExtensions.DefinitionRow("Rate law", FormatRateLaw(reaction.SpeedFactors));
    }

    /// <summary>
    ///     Formats a reaction as <c>2 Hydrogen + Oxygen -> 2 Water</c>, with gases sorted by name so the text is stable.
    /// </summary>
    private static string FormatEquation(
        IReadOnlyDictionary<GasProperties, float> input,
        IReadOnlyDictionary<GasProperties, float> output)
    {
        return $"{FormatSide(input)} -> {FormatSide(output)}";

        static string FormatSide(IReadOnlyDictionary<GasProperties, float> side)
        {
            if (side.Count == 0)
                return "(nothing)";

            return string.Join(
                " + ",
                side.OrderBy(static entry => entry.Key.Name, StringComparer.Ordinal)
                    .Select(static entry => entry.Value == 1f
                        ? entry.Key.Name
                        : $"{FormatNumber(entry.Value)} {entry.Key.Name}"));
        }
    }

    /// <summary>
    ///     Formats the rate law as <c>k(T) · [Hydrogen]^1 · [Oxygen]^0.5</c>.
    /// </summary>
    private static string FormatRateLaw(IReadOnlyDictionary<GasProperties, float> exponents)
    {
        var builder = new StringBuilder("k(T)");
        foreach ((var gas, float exponent) in exponents.OrderBy(static entry => entry.Key.Name, StringComparer.Ordinal))
            builder.Append(CultureInfo.InvariantCulture, $" · [{gas.Name}]^{FormatNumber(exponent)}");

        return builder.ToString();
    }

    private static string FormatMolarEnergy(float joulesPerMole)
    {
        return $"{FormatNumber(joulesPerMole / 1000f)} kJ/mol";
    }

    private static string FormatNumber(float value)
    {
        return value.ToString("G6", CultureInfo.InvariantCulture);
    }
}
