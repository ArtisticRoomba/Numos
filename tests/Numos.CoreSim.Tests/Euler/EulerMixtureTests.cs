using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

public class EulerMixtureTests
{
    private const float GasConstant = AtmosPhysicalConstants.MolarGasConstant;
    private const float AirMolarMass = 0.02897f;
    private const float AirMolarHeatCapacity = 2.5f * GasConstant;

    /// <summary>
    ///     One cubic metre of air at 293 K and 1 atm, at rest unless momentum is given. Mass and heat capacity
    ///     follow from the moles exactly as the kernel would sum them.
    /// </summary>
    private static EulerCellState RoomAir(
        float momentumX = 0f, float momentumY = 0f, float momentumZ = 0f,
        float temperature = 293f)
    {
        const float volume = 1f;
        float moles = AtmosPhysicalConstants.StandardAtmosphericPressure * volume / (GasConstant * temperature);
        float mass = moles * AirMolarMass;
        float heatCapacity = moles * AirMolarHeatCapacity;
        double energy = EulerMixture.TotalEnergy(heatCapacity, temperature, mass, momentumX, momentumY, momentumZ);

        EulerCellKind kind = EulerMixture.ToPrimitive(
            mass, moles, heatCapacity,
            momentumX, momentumY, momentumZ,
            energy, volume,
            out EulerCellState state);
        Assert.That(kind, Is.EqualTo(EulerCellKind.Gas));
        return state;
    }

    [Test]
    public void RoomAir_HasTextbookGammaSoundSpeedAndPressure()
    {
        EulerCellState air = RoomAir();

        // γ is not stored, so recover it from c² = γ·p/ρ.
        double gamma = (double)air.SoundSpeed * air.SoundSpeed * air.Density / air.Pressure;
        Assert.That(gamma, Is.EqualTo(1.4).Within(1e-6));
        Assert.That(air.SoundSpeed, Is.EqualTo(343.2f).Within(0.5f));
        Assert.That(
            air.Pressure,
            Is.EqualTo(AtmosPhysicalConstants.StandardAtmosphericPressure)
                .Within(1e-3f * AtmosPhysicalConstants.StandardAtmosphericPressure));
        Assert.That(air.Density, Is.EqualTo(1.2049f).Within(1e-3f));
        Assert.That(air.Temperature, Is.EqualTo(293f).Within(1e-3f));
        Assert.That(air.VelocityX, Is.Zero);
        Assert.That(air.SignalSpeed, Is.EqualTo(air.SoundSpeed));
        Assert.That(air.InverseMass * (air.Density * 1f), Is.EqualTo(1f).Within(1e-6f));
        TestContext.Out.WriteLine($"air: gamma={gamma:R} c={air.SoundSpeed:R} p={air.Pressure:R} rho={air.Density:R}");
    }

    [Test]
    public void TotalEnergy_RoundTripsTemperatureWithinOneUlp()
    {
        const float temperature = 293f;
        float moles = AtmosPhysicalConstants.StandardAtmosphericPressure / (GasConstant * temperature);
        float mass = moles * AirMolarMass;
        float momentum = mass * 10f;

        EulerCellState air = RoomAir(momentum, -momentum, 0.5f * momentum, temperature);

        float ulp = MathF.BitIncrement(temperature) - temperature;
        Assert.That(air.Temperature, Is.EqualTo(temperature).Within(ulp));
        Assert.That(air.VelocityX, Is.EqualTo(10f).Within(1e-5f));
        Assert.That(air.VelocityY, Is.EqualTo(-10f).Within(1e-5f));
        Assert.That(air.VelocityZ, Is.EqualTo(5f).Within(1e-5f));
        Assert.That(air.SignalSpeed, Is.EqualTo(air.SoundSpeed + air.VelocityX));
    }

    [Test]
    public void TotalEnergy_IncludesKineticTerm()
    {
        const float temperature = 293f;
        const float mass = 2f;
        const float heatCapacity = 1000f;

        double atRest = EulerMixture.TotalEnergy(heatCapacity, temperature, mass, 0f, 0f, 0f);
        // |P| = 2·(3, 4, 0) → K = |P|²/(2m) = 100/4 = 25 J.
        double moving = EulerMixture.TotalEnergy(heatCapacity, temperature, mass, 6f, 8f, 0f);

        Assert.That(atRest, Is.EqualTo(293_000d));
        Assert.That(moving - atRest, Is.EqualTo(25d).Within(1e-9));
        // A massless voxel cannot carry momentum, so only C·T counts.
        Assert.That(EulerMixture.TotalEnergy(heatCapacity, temperature, 0f, 6f, 8f, 0f), Is.EqualTo(293_000d));
    }

    [TestCase(0f, 1f, 1f)]
    [TestCase(1f, 0f, 1f)]
    [TestCase(1f, 1f, 0f)]
    [TestCase(-1f, 1f, 1f)]
    public void ToPrimitive_VacuumGivesZeroState(float mass, float moles, float heatCapacity)
    {
        EulerCellKind kind = EulerMixture.ToPrimitive(
            mass, moles, heatCapacity,
            3f, 4f, 5f,
            1000d, 1f,
            out EulerCellState state);

        Assert.That(kind, Is.EqualTo(EulerCellKind.Empty));
        Assert.That(state.Density, Is.Zero);
        Assert.That(state.VelocityX, Is.Zero);
        Assert.That(state.VelocityY, Is.Zero);
        Assert.That(state.VelocityZ, Is.Zero);
        Assert.That(state.Pressure, Is.Zero);
        Assert.That(state.EnergyDensity, Is.Zero);
        Assert.That(state.SoundSpeed, Is.Zero);
        Assert.That(state.InverseMass, Is.Zero);
        Assert.That(state.Temperature, Is.Zero);
        Assert.That(state.SignalSpeed, Is.Zero);
    }

    [Test]
    public void ToPrimitive_ClampsInternalEnergyAtZero()
    {
        // Kinetic energy 25 J against a total of 10 J: the mixture reads as 0 K rather than negative.
        EulerCellKind kind = EulerMixture.ToPrimitive(
            2f, 1f, 1000f,
            6f, 8f, 0f,
            10d, 1f,
            out EulerCellState state);

        Assert.That(kind, Is.EqualTo(EulerCellKind.Gas));
        Assert.That(state.Temperature, Is.Zero);
        Assert.That(state.Pressure, Is.Zero);
        Assert.That(state.SoundSpeed, Is.Zero);
        Assert.That(state.Density, Is.EqualTo(2f));
        Assert.That(state.VelocityX, Is.EqualTo(3f));
        Assert.That(state.VelocityY, Is.EqualTo(4f));
        Assert.That(state.EnergyDensity, Is.EqualTo(10f));
        Assert.That(state.SignalSpeed, Is.EqualTo(4f));
        Assert.That(float.IsFinite(state.SoundSpeed));
    }

    [Test]
    public void ToPrimitive_HeatCapacityBeyondFloatRangeStillGivesTheTemperature()
    {
        // 1e30 J/K per mole, ten moles: C does not fit in float, but C·T/V = 4e32 J/m³ still does.
        const double heatCapacity = 10d * 1e30;
        const float temperature = 400f;
        float mass = 10f * AirMolarMass;
        double energy = EulerMixture.TotalEnergy(heatCapacity, temperature, mass, mass, 0f, 0f);

        EulerCellKind kind = EulerMixture.ToPrimitive(
            mass, 10f, heatCapacity, mass, 0f, 0f, energy, 1f,
            out EulerCellState state);

        Assert.That(kind, Is.EqualTo(EulerCellKind.Gas));
        Assert.That(state.Temperature, Is.EqualTo(temperature).Within(1e-3f));
        Assert.That(state.Pressure, Is.EqualTo(10f * GasConstant * temperature).Within(1e-4f).Percent);
        Assert.That(state.VelocityX, Is.EqualTo(1f).Within(1e-6f));
        // γ → 1 as Cv → ∞, so c² → N·R·T/m.
        Assert.That(state.SoundSpeed, Is.EqualTo(MathF.Sqrt(10f * GasConstant * temperature / mass)).Within(1e-3f).Percent);
        Assert.That(float.IsFinite(state.EnergyDensity));
    }

    [Test]
    public void ToPrimitive_EnergyDensityBeyondFloatRangeReadsAsNonFinite()
    {
        // float.MaxValue per mole at 400 K: C fits in float, C·T/V does not.
        const double heatCapacity = float.MaxValue;
        float mass = AirMolarMass;
        double energy = EulerMixture.TotalEnergy(heatCapacity, 400f, mass, mass, 0f, 0f);

        EulerCellKind kind = EulerMixture.ToPrimitive(
            mass, 1f, heatCapacity, mass, 0f, 0f, energy, 1f,
            out EulerCellState state);

        Assert.That(kind, Is.EqualTo(EulerCellKind.NonFinite));
        Assert.That(state, Is.EqualTo(default(EulerCellState)));
    }

    [Test]
    public void ToPrimitive_PressureMatchesPrecomputedCoefficientOverload()
    {
        const float volume = 2.5f;
        float moles = 40f;
        float mass = moles * AirMolarMass;
        float heatCapacity = moles * AirMolarHeatCapacity;
        double energy = EulerMixture.TotalEnergy(heatCapacity, 300f, mass, mass, 0f, 0f);

        EulerMixture.ToPrimitive(
            mass, moles, heatCapacity, mass, 0f, 0f, energy, volume,
            out EulerCellState fromVolume);
        EulerMixture.ToPrimitive(
            mass, moles, heatCapacity, mass, 0f, 0f, energy, volume, GasConstant / volume,
            out EulerCellState fromCoefficient);

        Assert.That(fromCoefficient.Pressure, Is.EqualTo(fromVolume.Pressure));
        Assert.That(fromCoefficient.Pressure, Is.EqualTo(moles * GasConstant * 300f / volume).Within(1e-4f).Percent);
        Assert.That(fromVolume.Density, Is.EqualTo(mass / volume));
        Assert.That(fromVolume.EnergyDensity, Is.EqualTo((float)(energy / volume)));
    }

    [Test]
    public void FromCell_PermutesVelocitiesCyclically()
    {
        EulerCellState cell = RoomAir(1f, 2f, 3f);

        EulerFaceState x = EulerFaceState.FromCell(cell, 0);
        EulerFaceState y = EulerFaceState.FromCell(cell, 1);
        EulerFaceState z = EulerFaceState.FromCell(cell, 2);

        Assert.That((x.NormalVelocity, x.TangentialVelocity1, x.TangentialVelocity2),
            Is.EqualTo((cell.VelocityX, cell.VelocityY, cell.VelocityZ)));
        Assert.That((y.NormalVelocity, y.TangentialVelocity1, y.TangentialVelocity2),
            Is.EqualTo((cell.VelocityY, cell.VelocityZ, cell.VelocityX)));
        Assert.That((z.NormalVelocity, z.TangentialVelocity1, z.TangentialVelocity2),
            Is.EqualTo((cell.VelocityZ, cell.VelocityX, cell.VelocityY)));
        Assert.That(x.Density, Is.EqualTo(cell.Density));
        Assert.That(x.Pressure, Is.EqualTo(cell.Pressure));
        Assert.That(x.EnergyDensity, Is.EqualTo(cell.EnergyDensity));
        Assert.That(x.SoundSpeed, Is.EqualTo(cell.SoundSpeed));
        Assert.That(x.InverseMass, Is.EqualTo(cell.InverseMass));
        Assert.Throws<ArgumentOutOfRangeException>(() => EulerFaceState.FromCell(cell, 3));
    }
}
