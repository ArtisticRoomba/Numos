using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

public class HllcFluxTests
{
    private const float AirDensity = 1.2049f;
    private const float AirPressure = AtmosPhysicalConstants.StandardAtmosphericPressure;
    private const float AirGamma = 1.4f;

    /// <summary>
    ///     Builds a face state for an ideal gas of adiabatic index <paramref name="gamma" />: energy density
    ///     <c>p/(γ−1) + ½ρ|v|²</c>, sound speed <c>√(γp/ρ)</c>, inverse mass for a cell of the given volume.
    /// </summary>
    private static EulerFaceState Gas(
        float density, float normal, float tangential1, float tangential2, float pressure, float gamma,
        float volume = 1f)
    {
        EulerFaceState state;
        state.Density = density;
        state.NormalVelocity = normal;
        state.TangentialVelocity1 = tangential1;
        state.TangentialVelocity2 = tangential2;
        state.Pressure = pressure;
        float speedSquared = normal * normal + tangential1 * tangential1 + tangential2 * tangential2;
        state.EnergyDensity = pressure / (gamma - 1f) + 0.5f * density * speedSquared;
        state.SoundSpeed = MathF.Sqrt(gamma * pressure / density);
        state.InverseMass = 1f / (density * volume);
        return state;
    }

    private static EulerFaceState Air(float normal, float tangential1 = 0f, float tangential2 = 0f)
    {
        return Gas(AirDensity, normal, tangential1, tangential2, AirPressure, AirGamma);
    }

    private static EulerFaceState Mirror(in EulerFaceState state)
    {
        EulerFaceState mirrored = state;
        mirrored.NormalVelocity = -state.NormalVelocity;
        return mirrored;
    }

    /// <summary>
    ///     <c>F(U)</c> in the same operand order as the solver's supersonic branch.
    /// </summary>
    private static EulerFaceFlux Physical(in EulerFaceState s)
    {
        float massFlux = s.Density * s.NormalVelocity;
        EulerFaceFlux flux = default;
        flux.Mass = massFlux;
        flux.NormalMomentum = massFlux * s.NormalVelocity + s.Pressure;
        flux.Tangential1Momentum = massFlux * s.TangentialVelocity1;
        flux.Tangential2Momentum = massFlux * s.TangentialVelocity2;
        flux.Energy = s.NormalVelocity * (s.EnergyDensity + s.Pressure);
        return flux;
    }

    private static bool SameBits(float a, float b)
    {
        return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    }

    /// <summary>
    ///     Exact equality that treats +0 and −0 alike. Products with a zero contact speed legitimately come out as
    ///     either zero depending on the signs of the other factors.
    /// </summary>
    private static bool SameValue(float a, float b)
    {
        return !float.IsNaN(a) && a == b;
    }

    private static EulerFaceState RandomGas(Random random)
    {
        float density = MathF.Pow(10f, -3f + 4f * random.NextSingle());
        float pressure = MathF.Pow(10f, 6f * random.NextSingle());
        float gamma = 1.1f + 0.57f * random.NextSingle();
        float soundSpeed = MathF.Sqrt(gamma * pressure / density);
        float normal = soundSpeed * (-2.5f + 5f * random.NextSingle());
        float tangential1 = soundSpeed * (-1f + 2f * random.NextSingle());
        float tangential2 = soundSpeed * (-1f + 2f * random.NextSingle());
        float volume = 0.5f + 4f * random.NextSingle();
        return Gas(density, normal, tangential1, tangential2, pressure, gamma, volume);
    }

    [TestCase(500f)]
    [TestCase(-500f)]
    [TestCase(343.5f)]
    public void Solve_SupersonicUniformStateReturnsPhysicalFluxBitwise(float normal)
    {
        EulerFaceState state = Air(normal, 3f, -7f);
        EulerFaceFlux expected = Physical(state);

        HllcFlux.Solve(state, state, allMach: false, out EulerFaceFlux flux);

        Assert.That(SameBits(flux.Mass, expected.Mass));
        Assert.That(SameBits(flux.NormalMomentum, expected.NormalMomentum));
        Assert.That(SameBits(flux.Tangential1Momentum, expected.Tangential1Momentum));
        Assert.That(SameBits(flux.Tangential2Momentum, expected.Tangential2Momentum));
        Assert.That(SameBits(flux.Energy, expected.Energy));
        Assert.That(flux.UpwindIsLeft, Is.EqualTo(normal > 0f));
        Assert.That(flux.SpeciesWeight, Is.EqualTo(flux.Mass * state.InverseMass));
    }

    [Test]
    public void Solve_UniformStateAtRestIsExactlyStatic()
    {
        EulerFaceState state = Air(0f, 3f, -7f);

        HllcFlux.Solve(state, state, allMach: false, out EulerFaceFlux flux);
        HllcFlux.Solve(state, state, allMach: true, out EulerFaceFlux allMach);

        Assert.That(flux.Mass, Is.Zero);
        Assert.That(SameBits(flux.NormalMomentum, state.Pressure));
        Assert.That(flux.Tangential1Momentum, Is.Zero);
        Assert.That(flux.Tangential2Momentum, Is.Zero);
        Assert.That(flux.Energy, Is.Zero);
        Assert.That(flux.SpeciesWeight, Is.Zero);
        Assert.That(SameBits(allMach.NormalMomentum, state.Pressure));
    }

    [TestCase(10f)]
    [TestCase(-10f)]
    [TestCase(150f)]
    public void Solve_SubsonicUniformStateReturnsPhysicalFluxToRoundoff(float normal)
    {
        // S* is (a·u − b·u)/(a − b) rather than u itself here, so the star flux is F(U) only to a few ulps.
        EulerFaceState state = Air(normal, 3f, -7f);
        EulerFaceFlux expected = Physical(state);

        HllcFlux.Solve(state, state, allMach: false, out EulerFaceFlux flux);

        Assert.That(flux.Mass, Is.EqualTo(expected.Mass).Within(1e-3f).Percent);
        Assert.That(flux.NormalMomentum, Is.EqualTo(expected.NormalMomentum).Within(1e-3f).Percent);
        Assert.That(flux.Tangential1Momentum, Is.EqualTo(expected.Tangential1Momentum).Within(1e-3f).Percent);
        Assert.That(flux.Tangential2Momentum, Is.EqualTo(expected.Tangential2Momentum).Within(1e-3f).Percent);
        Assert.That(flux.Energy, Is.EqualTo(expected.Energy).Within(1e-3f).Percent);
        Assert.That(flux.UpwindIsLeft, Is.EqualTo(normal > 0f));
    }

    [Test]
    public void Solve_StationaryContactCarriesOnlyPressure()
    {
        EulerFaceState left = Gas(1.2f, 0f, 5f, 0f, AirPressure, 1.4f);
        EulerFaceState right = Gas(8f, 0f, -2f, 1f, AirPressure, 1.2f);
        Assert.That(left.SoundSpeed, Is.Not.EqualTo(right.SoundSpeed));

        foreach (bool allMach in new[] { false, true })
        {
            HllcFlux.Solve(left, right, allMach, out EulerFaceFlux flux);

            Assert.That(flux.Mass, Is.Zero);
            Assert.That(flux.Energy, Is.Zero);
            Assert.That(flux.Tangential1Momentum, Is.Zero);
            Assert.That(flux.Tangential2Momentum, Is.Zero);
            Assert.That(SameBits(flux.NormalMomentum, AirPressure), $"allMach={allMach}: {flux.NormalMomentum:R}");
            Assert.That(flux.SpeciesWeight, Is.Zero);
        }
    }

    [Test]
    public void Solve_IsBitwiseMirrorSymmetric()
    {
        var random = new Random(20261021);
        int contactAtRest = 0;
        int vacuumPairs = 0;

        for (int sample = 0; sample < 1000; sample++)
        {
            EulerFaceState left = random.NextSingle() < 0.08f ? default : RandomGas(random);
            EulerFaceState right = random.NextSingle() < 0.08f ? default : RandomGas(random);
            if (left.Density == 0f && right.Density == 0f)
                vacuumPairs++;

            bool allMach = (sample & 1) == 0;
            HllcFlux.Solve(left, right, allMach, out EulerFaceFlux flux);
            HllcFlux.Solve(Mirror(right), Mirror(left), allMach, out EulerFaceFlux mirrored);
            HllcFlux.EstimateStarState(left, right, out _, out _, out float contactSpeed, out _);

            string label = $"sample {sample}";
            Assert.That(SameValue(mirrored.Mass, -flux.Mass), label);
            Assert.That(SameValue(mirrored.NormalMomentum, flux.NormalMomentum), label);
            Assert.That(SameValue(mirrored.Tangential1Momentum, -flux.Tangential1Momentum), label);
            Assert.That(SameValue(mirrored.Tangential2Momentum, -flux.Tangential2Momentum), label);
            Assert.That(SameValue(mirrored.Energy, -flux.Energy), label);
            Assert.That(SameValue(mirrored.SpeciesWeight, -flux.SpeciesWeight), label);

            if (contactSpeed == 0f || (left.Density == 0f && right.Density == 0f))
            {
                // Both frames call the left cell upwind here. Nothing is transported, so it does not matter.
                contactAtRest++;
                Assert.That(flux.Mass, Is.Zero, label);
                continue;
            }

            Assert.That(mirrored.UpwindIsLeft, Is.EqualTo(!flux.UpwindIsLeft), label);
        }

        Assert.That(contactAtRest, Is.LessThan(50));
        Assert.That(vacuumPairs, Is.GreaterThan(0));
    }

    [Test]
    public void SolveWall_InflowAndOutflowPressures()
    {
        double density = AirDensity;
        double soundSpeed = Air(0f).SoundSpeed;

        HllcFlux.SolveWall(Air(1f, 4f, 5f), wallIsOnRight: true, out EulerFaceFlux into);
        HllcFlux.SolveWall(Air(-1f, 4f, 5f), wallIsOnRight: true, out EulerFaceFlux away);
        HllcFlux.SolveWall(Air(-1f, 4f, 5f), wallIsOnRight: false, out EulerFaceFlux intoLeftWall);
        HllcFlux.SolveWall(Air(0f), wallIsOnRight: true, out EulerFaceFlux atRest);
        HllcFlux.SolveWall(Air(-1000f), wallIsOnRight: true, out EulerFaceFlux fastRecession);
        HllcFlux.SolveWall(default, wallIsOnRight: true, out EulerFaceFlux vacuum);

        double expectedInto = AirPressure + density * 1d * (2d + soundSpeed);
        double expectedAway = AirPressure - density * soundSpeed;
        Assert.That(into.NormalMomentum, Is.EqualTo(expectedInto).Within(1e-6 * expectedInto));
        Assert.That(away.NormalMomentum, Is.EqualTo(expectedAway).Within(1e-6 * expectedAway));
        Assert.That(SameBits(intoLeftWall.NormalMomentum, into.NormalMomentum));
        Assert.That(SameBits(atRest.NormalMomentum, AirPressure));
        Assert.That(fastRecession.NormalMomentum, Is.Zero);
        Assert.That(vacuum.NormalMomentum, Is.Zero);

        foreach (EulerFaceFlux flux in new[] { into, away, intoLeftWall, atRest, fastRecession })
        {
            Assert.That(flux.Mass, Is.Zero);
            Assert.That(flux.Energy, Is.Zero);
            Assert.That(flux.Tangential1Momentum, Is.Zero);
            Assert.That(flux.Tangential2Momentum, Is.Zero);
            Assert.That(flux.SpeciesWeight, Is.Zero);
        }

        Assert.That(into.UpwindIsLeft, Is.False);
        Assert.That(intoLeftWall.UpwindIsLeft, Is.True);
    }

    [Test]
    public void Solve_AirIntoVacuumMatchesWorkedExample()
    {
        EulerFaceState air = Air(0f, 2f, -3f);

        HllcFlux.Solve(air, default, allMach: false, out EulerFaceFlux flux);
        HllcFlux.EstimateStarState(
            air, default, out float leftSpeed, out float rightSpeed, out float contactSpeed, out float starPressure);

        double expectedContact = air.SoundSpeed / AirGamma;
        double expectedMassFlux = AirDensity * air.SoundSpeed / (AirGamma + 1f);
        TestContext.Out.WriteLine(
            $"vacuum: S*={contactSpeed:R} (expected {expectedContact:R}), " +
            $"mass flux={flux.Mass:R} (expected {expectedMassFlux:R}), energy={flux.Energy:R}");

        Assert.That(leftSpeed, Is.EqualTo(-air.SoundSpeed));
        Assert.That(rightSpeed, Is.EqualTo(air.SoundSpeed));
        Assert.That(contactSpeed, Is.EqualTo(expectedContact).Within(1e-3));
        Assert.That(flux.Mass, Is.EqualTo(expectedMassFlux).Within(1e-2 * expectedMassFlux));
        Assert.That(flux.Mass, Is.EqualTo(171f).Within(0.01f * 171f));
        Assert.That(flux.Energy, Is.Positive);
        Assert.That(flux.UpwindIsLeft, Is.True);
        Assert.That(flux.SpeciesWeight, Is.EqualTo(flux.Mass * air.InverseMass));
        // The tangential momentum rides along with the mass at the cell's own tangential velocity.
        Assert.That(flux.Tangential1Momentum, Is.EqualTo(flux.Mass * 2f).Within(1e-4f * flux.Mass * 2f));
        Assert.That(flux.Tangential2Momentum, Is.EqualTo(flux.Mass * -3f).Within(1e-4f * flux.Mass * 3f));

        // b = 0 makes the star pressure vanish, and the weight form of the centred term makes it vanish exactly.
        Assert.That(starPressure, Is.Zero);
        Assert.That(MathF.Abs(starPressure), Is.LessThanOrEqualTo(1e-3f));
        // Momentum flux in the left star region with ρu = 0 and p* = 0 reduces to −S*·p/(S_L − S*).
        double expectedMomentum = -(double)contactSpeed * AirPressure / (leftSpeed - (double)contactSpeed);
        Assert.That(flux.NormalMomentum, Is.EqualTo(expectedMomentum).Within(1e-5 * expectedMomentum));
    }

    [Test]
    public void Solve_BothVacuumReturnsZeroWithoutNaN()
    {
        HllcFlux.Solve(default, default, allMach: true, out EulerFaceFlux flux);

        Assert.That(flux.Mass, Is.Zero);
        Assert.That(flux.NormalMomentum, Is.Zero);
        Assert.That(flux.Tangential1Momentum, Is.Zero);
        Assert.That(flux.Tangential2Momentum, Is.Zero);
        Assert.That(flux.Energy, Is.Zero);
        Assert.That(flux.SpeciesWeight, Is.Zero);
        Assert.That(flux.UpwindIsLeft, Is.True);

        HllcFlux.EstimateStarState(
            default, default, out float leftSpeed, out float rightSpeed, out float contact, out float starPressure);
        Assert.That(starPressure, Is.Zero);
        Assert.That(leftSpeed, Is.Zero);
        Assert.That(rightSpeed, Is.Zero);
        Assert.That(contact, Is.Zero);
    }

    [Test]
    public void Solve_VacuumIntoAirIsTheMirroredCase()
    {
        EulerFaceState air = Air(0f);

        HllcFlux.Solve(air, default, allMach: false, out EulerFaceFlux rightward);
        HllcFlux.Solve(default, air, allMach: false, out EulerFaceFlux leftward);

        Assert.That(SameValue(leftward.Mass, -rightward.Mass));
        Assert.That(SameValue(leftward.Energy, -rightward.Energy));
        Assert.That(SameValue(leftward.NormalMomentum, rightward.NormalMomentum));
        Assert.That(leftward.UpwindIsLeft, Is.False);
        Assert.That(SameValue(leftward.SpeciesWeight, -rightward.SpeciesWeight));
    }

    [Test]
    public void Solve_SodStatesGiveRightwardContactInsideTheFan()
    {
        EulerFaceState left = Gas(1f, 0f, 0f, 0f, 1e5f, 1.4f);
        EulerFaceState right = Gas(0.125f, 0f, 0f, 0f, 1e4f, 1.4f);

        HllcFlux.Solve(left, right, allMach: false, out EulerFaceFlux flux);
        HllcFlux.EstimateStarState(
            left, right, out float leftSpeed, out float rightSpeed, out float contactSpeed, out _);

        Assert.That(leftSpeed, Is.Negative);
        Assert.That(rightSpeed, Is.Positive);
        Assert.That(contactSpeed, Is.GreaterThan(leftSpeed).And.LessThan(rightSpeed));
        Assert.That(contactSpeed, Is.Positive);
        Assert.That(flux.Mass, Is.Positive);
        Assert.That(flux.Energy, Is.Positive);
        Assert.That(flux.NormalMomentum, Is.GreaterThan(right.Pressure).And.LessThan(left.Pressure));
        Assert.That(flux.UpwindIsLeft, Is.True);
        Assert.That(flux.SpeciesWeight, Is.EqualTo(flux.Mass * left.InverseMass));
    }

    [Test]
    public void Solve_AllMachIsIdentityWhenEitherSideIsSupersonic()
    {
        EulerFaceState left = Gas(1f, 400f, 1f, 2f, 1e5f, 1.4f);
        EulerFaceState right = Gas(0.5f, 380f, -1f, 0f, 8e4f, 1.4f);
        // Both states subsonic in the sense S_L < 0 < S_R would need a smaller u. Here Ma > 1 on both sides so θ = 1
        // and every component must agree bitwise, including the star branch reached by the lower velocity below.
        EulerFaceState slowRight = Gas(0.5f, 350f, -1f, 0f, 8e4f, 1.4f);

        foreach ((EulerFaceState l, EulerFaceState r) in new[] { (left, right), (left, slowRight), (right, left) })
        {
            HllcFlux.Solve(l, r, allMach: false, out EulerFaceFlux standard);
            HllcFlux.Solve(l, r, allMach: true, out EulerFaceFlux fixedFlux);

            Assert.That(SameBits(fixedFlux.Mass, standard.Mass));
            Assert.That(SameBits(fixedFlux.NormalMomentum, standard.NormalMomentum));
            Assert.That(SameBits(fixedFlux.Tangential1Momentum, standard.Tangential1Momentum));
            Assert.That(SameBits(fixedFlux.Tangential2Momentum, standard.Tangential2Momentum));
            Assert.That(SameBits(fixedFlux.Energy, standard.Energy));
            Assert.That(SameBits(fixedFlux.SpeciesWeight, standard.SpeciesWeight));
        }
    }

    [Test]
    public void Solve_AllMachOnlyRescalesTheMomentumPressureJump()
    {
        EulerFaceState left = Air(1f, 0.3f, -0.2f);
        EulerFaceState right = Gas(1.01f * AirDensity, 0.5f, 0.1f, 0.4f, 1.002f * AirPressure, AirGamma);

        HllcFlux.Solve(left, right, allMach: false, out EulerFaceFlux standard);
        HllcFlux.Solve(left, right, allMach: true, out EulerFaceFlux fixedFlux);
        HllcFlux.EstimateStarState(
            left, right, out float leftSpeed, out float rightSpeed, out float contactSpeed, out _);

        Assert.That(SameBits(fixedFlux.Mass, standard.Mass));
        Assert.That(SameBits(fixedFlux.Tangential1Momentum, standard.Tangential1Momentum));
        Assert.That(SameBits(fixedFlux.Tangential2Momentum, standard.Tangential2Momentum));
        Assert.That(SameBits(fixedFlux.Energy, standard.Energy));
        Assert.That(SameBits(fixedFlux.SpeciesWeight, standard.SpeciesWeight));
        Assert.That(fixedFlux.NormalMomentum, Is.Not.EqualTo(standard.NormalMomentum));

        // The momentum flux in a star region is p*_m + S*·(… + p*_m)/(S_K − S*), so changing p*_m by Δ moves it by
        // Δ·S_K/(S_K − S*), where Δ = (θ − 1)·a·b·(u_R − u_L)/(b − a).
        double a = left.Density * (leftSpeed - left.NormalVelocity);
        double b = right.Density * (rightSpeed - right.NormalVelocity);
        double leftMach = Math.Abs(left.NormalVelocity) / left.SoundSpeed;
        double rightMach = Math.Abs(right.NormalVelocity) / right.SoundSpeed;
        double theta = Math.Min(1d, Math.Max(leftMach, rightMach));
        double jump = a * b * (right.NormalVelocity - left.NormalVelocity) / (b - a);
        double outerSpeed = contactSpeed >= 0f ? leftSpeed : rightSpeed;
        double expectedDelta = (theta - 1d) * jump * outerSpeed / (outerSpeed - contactSpeed);

        Assert.That(theta, Is.LessThan(0.01));
        Assert.That(
            (double)fixedFlux.NormalMomentum - standard.NormalMomentum,
            Is.EqualTo(expectedDelta).Within(1e-3 * Math.Abs(expectedDelta)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Solve_FirstOrderUpdateKeepsDensityAndInternalEnergyPositive(bool allMach)
    {
        var random = new Random(allMach ? 20261022 : 20261023);
        const int trials = 20_000;
        int failures = 0;
        int vacuumNeighbours = 0;
        double worstInternalFraction = double.PositiveInfinity;

        for (int trial = 0; trial < trials; trial++)
        {
            EulerFaceState lower = random.NextSingle() < 0.1f ? default : RandomGas(random);
            EulerFaceState cell = RandomGas(random);
            EulerFaceState upper = random.NextSingle() < 0.1f ? default : RandomGas(random);
            if (lower.Density == 0f || upper.Density == 0f)
                vacuumNeighbours++;

            float maxSignal = MathF.Max(
                MathF.Max(
                    MathF.Abs(lower.NormalVelocity) + lower.SoundSpeed,
                    MathF.Abs(cell.NormalVelocity) + cell.SoundSpeed),
                MathF.Abs(upper.NormalVelocity) + upper.SoundSpeed);
            double courant = 0.5d / maxSignal;

            HllcFlux.Solve(lower, cell, allMach, out EulerFaceFlux leftFlux);
            HllcFlux.Solve(cell, upper, allMach, out EulerFaceFlux rightFlux);

            double density = cell.Density - courant * ((double)rightFlux.Mass - leftFlux.Mass);
            double momentumN = cell.Density * (double)cell.NormalVelocity -
                               courant * ((double)rightFlux.NormalMomentum - leftFlux.NormalMomentum);
            double momentumT1 = cell.Density * (double)cell.TangentialVelocity1 -
                                courant * ((double)rightFlux.Tangential1Momentum - leftFlux.Tangential1Momentum);
            double momentumT2 = cell.Density * (double)cell.TangentialVelocity2 -
                                courant * ((double)rightFlux.Tangential2Momentum - leftFlux.Tangential2Momentum);
            double energy = cell.EnergyDensity - courant * ((double)rightFlux.Energy - leftFlux.Energy);

            bool positive = density > 0d && double.IsFinite(energy);
            if (positive)
            {
                double kinetic = 0.5d * (momentumN * momentumN + momentumT1 * momentumT1 + momentumT2 * momentumT2) /
                                 density;
                double internalEnergy = energy - kinetic;
                positive = internalEnergy > 0d;
                worstInternalFraction = Math.Min(worstInternalFraction, internalEnergy / energy);
            }

            if (!positive)
            {
                failures++;
                TestContext.Out.WriteLine(
                    $"trial {trial}: rho {cell.Density:R}->{density:R} e {cell.EnergyDensity:R}->{energy:R} " +
                    $"lower=({lower.Density:R},{lower.NormalVelocity:R},{lower.Pressure:R}) " +
                    $"cell=({cell.Density:R},{cell.NormalVelocity:R},{cell.Pressure:R}) " +
                    $"upper=({upper.Density:R},{upper.NormalVelocity:R},{upper.Pressure:R})");
            }
        }

        TestContext.Out.WriteLine(
            $"positivity allMach={allMach}: {failures} failures in {trials} trials, " +
            $"{vacuumNeighbours} with a vacuum neighbour, smallest internal fraction {worstInternalFraction:R}");
        Assert.That(failures, Is.Zero);
        Assert.That(vacuumNeighbours, Is.GreaterThan(trials / 10));
    }
}
