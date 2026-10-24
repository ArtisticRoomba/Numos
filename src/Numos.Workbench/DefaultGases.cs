using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;

namespace Numos.Workbench;

/// <summary>
///     Gas definitions and the hydrogen combustion reaction that a new project starts with.
/// </summary>
public static class DefaultGases
{
    public readonly static GasProperties Hydrogen = new()
    {
        Name = "Hydrogen",
        MolarHeatCapacityAtConstantVolume =
            AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
        BoilingPoint = 20.4f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 904f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    public readonly static GasProperties Oxygen = new()
    {
        Name = "Oxygen",
        MolarHeatCapacityAtConstantVolume =
            AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
        BoilingPoint = 90.2f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 6_820f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    public readonly static GasProperties Nitrogen = new()
    {
        Name = "Nitrogen",
        MolarHeatCapacityAtConstantVolume =
            AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
        BoilingPoint = 77.34f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 5_600f,
        LiquidId = 1,
        DiffusionCoefficient = 0.02f
    };

    public readonly static GasProperties CarbonDioxide = new()
    {
        Name = "Carbon Dioxide",
        MolarHeatCapacityAtConstantVolume = 28.2f,
        BoilingPoint = 194.7f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 9800f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    public readonly static GasProperties NitrousOxide = new()
    {
        Name = "Nitrous Oxide",
        MolarHeatCapacityAtConstantVolume = 30.3f,
        BoilingPoint = 184.71f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 16_540f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    public readonly static GasProperties Water = new()
    {
        Name = "Water Vapour",
        MolarHeatCapacityAtConstantVolume = 28f,
        BoilingPoint = 373.15f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 40_657f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };


    /// <summary>
    ///     Appends the six preset gases and the hydrogen combustion reaction to <paramref name="config" />.
    /// </summary>
    /// <param name="config">The configuration to extend. Its gas registry should be empty so gas IDs start at 0.</param>
    /// <remarks>
    ///     The preset <see cref="GasProperties" /> instances are shared by every project that uses them.
    /// </remarks>
    public static void Apply(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.GasRegistry.Add(Hydrogen);
        config.GasRegistry.Add(Oxygen);
        config.GasRegistry.Add(Nitrogen);
        config.GasRegistry.Add(CarbonDioxide);
        config.GasRegistry.Add(NitrousOxide);
        config.GasRegistry.Add(Water);

        var hydrogenFire = new StandardGasReaction(
            new Dictionary<GasProperties, float>
                { { Hydrogen, 2 }, { Oxygen, 1 } },
            new Dictionary<GasProperties, float>
            {
                { Water, 2 }
            },
            UnitConversions.FromKilojoulePerMole(483.6f),
            1.8e13f,
            UnitConversions.FromKilojoulePerMole(256f),
            new Dictionary<GasProperties, float>
            {
                { Hydrogen, 1 },
                { Oxygen, 0.5f }
            });

        config.SolverConfigurations = [new GasReactionConfig(standardReactions: [hydrogenFire])];
    }
}
