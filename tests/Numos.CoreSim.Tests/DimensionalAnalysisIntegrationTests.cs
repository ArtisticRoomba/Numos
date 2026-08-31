using System.Reflection;
using Numos.Units;
using Numos.Units.Generated;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class DimensionalAnalysisIntegrationTests
{
    [Test]
    public void QuantityAliasesPreserveSolverClrStorageTypes()
    {
        Assembly assembly = typeof(AtmosConfig).Assembly;
        Type chunkType = assembly.GetType("Numos.CoreSim.AtmosChunk", true)!;
        Type gasChannelType = assembly.GetType("Numos.CoreSim.GasChannel", true)!;
        Type solverMathType = assembly.GetType("Numos.CoreSim.Solvers.AtmosSolverMath", true)!;
        Type configType = assembly.GetType("Numos.CoreSim.IAtmosConfig", true)!;

        FieldInfo temperature = chunkType.GetField("Temperature")!;
        FieldInfo pressure = chunkType.GetField("TotalPressure")!;
        FieldInfo moles = gasChannelType.GetField("Moles")!;
        MethodInfo calculatePressure = solverMathType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CalculatePressure" &&
                              method.GetParameters().First().ParameterType == configType);

        Assert.Multiple(() =>
        {
            Assert.That(temperature.FieldType.GetGenericArguments().Single(), Is.EqualTo(typeof(float)));
            Assert.That(pressure.FieldType.GetGenericArguments().Single(), Is.EqualTo(typeof(float)));
            Assert.That(moles.FieldType, Is.EqualTo(typeof(float[])));
            Assert.That(calculatePressure.ReturnType, Is.EqualTo(typeof(float)));
            Assert.That(calculatePressure.GetParameters().Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[] { configType, typeof(float), typeof(float) }));
        });
    }

    [Test]
    public void PublicQuantityMetadataSurvivesAliasErasure()
    {
        PropertyInfo temperature = typeof(AtmosConfig).GetProperty(nameof(AtmosConfig.GlobalTemperature))!;
        FieldInfo molarEnergy = typeof(GasProperties).GetField(
            nameof(GasProperties.MolarEnthalpyOfVaporization))!;

        Assert.Multiple(() =>
        {
            Assert.That(temperature.PropertyType, Is.EqualTo(typeof(float)));
            Assert.That(temperature.GetCustomAttribute<QuantityAttribute>()?.Id,
                Is.EqualTo("temperature"));
            Assert.That(molarEnergy.FieldType, Is.EqualTo(typeof(float)));
            Assert.That(molarEnergy.GetCustomAttribute<QuantityAttribute>()?.Id,
                Is.EqualTo("molarEnergy"));
        });
    }

    [Test]
    public void GeneratedConversionsUseCanonicalSolverUnits()
    {
        float kelvin = UnitConversions.FromCelsius(20f);
        float pascals = UnitConversions.FromKilopascal(101.325f);

        Assert.Multiple(() =>
        {
            Assert.That(kelvin, Is.EqualTo(293.15f).Within(0.0001f));
            Assert.That(UnitConversions.ToCelsius(kelvin), Is.EqualTo(20f).Within(0.0001f));
            Assert.That(pascals, Is.EqualTo(101_325f).Within(0.01f));
            Assert.That(UnitConversions.ToKilopascal(pascals), Is.EqualTo(101.325f).Within(0.0001f));
        });
    }
}
