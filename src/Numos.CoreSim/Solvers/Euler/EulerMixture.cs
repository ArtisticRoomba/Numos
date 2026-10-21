namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Primitive description of one voxel's gas mixture, derived from its conserved variables by
///     <see cref="EulerMixture.ToPrimitive(Kilogram, Mole, JoulePerKelvin64, KilogramMetrePerSecond,
///     KilogramMetrePerSecond, KilogramMetrePerSecond, Joule64, CubicMetre, PascalPerMoleKelvin,
///     out EulerCellState)" />.
/// </summary>
/// <remarks>
///     Velocities are along the grid axes; <see cref="EulerFaceState.FromCell" /> picks the normal and tangential
///     components for a particular face. A vacuum cell has every field zero, including
///     <see cref="InverseMass" />, so a caller that finds <see cref="Density" /> zero must not divide by it.
/// </remarks>
internal struct EulerCellState
{
    public KilogramPerCubicMetre Density;
    public MetrePerSecond VelocityX;
    public MetrePerSecond VelocityY;
    public MetrePerSecond VelocityZ;
    public Pascal Pressure;

    /// <summary>
    ///     Total (internal plus kinetic) energy per unit volume, <c>E / V</c>.
    /// </summary>
    public JoulePerCubicMetre EnergyDensity;

    public MetrePerSecond SoundSpeed;
    public PerKilogram InverseMass;
    public Kelvin Temperature;

    /// <summary>
    ///     Fastest wave the cell can emit along any axis, <c>max(|u|, |v|, |w|) + c</c>. The sweep's time-step
    ///     bound is the maximum of this over every cell.
    /// </summary>
    public MetrePerSecond SignalSpeed;
}

/// <summary>
///     What <see cref="EulerMixture.ToPrimitive(Kilogram, Mole, JoulePerKelvin64, KilogramMetrePerSecond,
///     KilogramMetrePerSecond, KilogramMetrePerSecond, Joule64, CubicMetre, PascalPerMoleKelvin,
///     out EulerCellState)" /> made of a voxel.
/// </summary>
internal enum EulerCellKind : byte
{
    /// <summary>
    ///     No gas: mass, moles, or heat capacity is not positive. The state is all zeros.
    /// </summary>
    Empty,

    /// <summary>
    ///     A finite primitive state the flux can use.
    /// </summary>
    Gas,

    /// <summary>
    ///     Gas whose primitive state does not fit in <see langword="float" />: the energy density, temperature,
    ///     pressure, or sound speed came out infinite or NaN. The state is all zeros, so the voxel transports like a
    ///     vacuum; its conserved variables are left to the caller.
    /// </summary>
    NonFinite
}

/// <summary>
///     Converts a voxel's conserved mixture variables (mass, moles, heat capacity, momentum, total energy) into the
///     primitive state the HLLC flux works with, and back from temperature to total energy.
/// </summary>
/// <remarks>
///     <para>
///         The mixture is an ideal gas with constant per-species heat capacities, so its internal energy is
///         <c>U = C·T</c> with <c>C = Σ n_k·Cv_k</c>, its pressure is <c>p = N·R·T / V</c>, and its adiabatic index is
///         <c>γ = Cp / Cv = (C + N·R) / C</c>. The sound speed follows from <c>c² = γ·p / ρ</c>; substituting
///         <c>pV = NRT</c> and <c>ρ = m / V</c> gives <c>c² = γ·N·R·T / m</c>, the form used here because it reuses the
///         <c>1/m</c> already computed for the velocities and never needs the volume.
///     </para>
///     <para>
///         Sanity numbers: air at 293 K (Cv = 5R/2 per mole, M = 0.02897 kg/mol) has γ = 1.4 and c ≈ 343 m/s.
///     </para>
///     <para>
///         The heat capacity, the kinetic energy, and the internal-energy subtraction are double. A warm voxel's
///         total energy is dominated by <c>C·T</c> (about 250 kJ for a cubic metre of room air, against 60 J of
///         kinetic energy at 10 m/s), and a float subtraction would throw away the thermal part's low bits every
///         tick; <c>C</c> itself is a sum of <c>n·Cv</c> products that a host-configured heat capacity near the
///         float maximum pushes past float range while the temperature it implies is ordinary. Everything else is
///         scalar float with only multiplies, adds, divides, <see cref="MathF.Sqrt" />, and min/max, so the results
///         are reproducible across platforms.
///     </para>
/// </remarks>
internal static class EulerMixture
{
    /// <summary>
    ///     Derives the primitive state of a voxel from its conserved variables.
    /// </summary>
    /// <param name="mass">Total gas mass in the voxel.</param>
    /// <param name="moles">Total moles in the voxel.</param>
    /// <param name="heatCapacity">Total heat capacity at constant volume, <c>Σ n_k·Cv_k</c>.</param>
    /// <param name="momentumX">Momentum along X.</param>
    /// <param name="momentumY">Momentum along Y.</param>
    /// <param name="momentumZ">Momentum along Z.</param>
    /// <param name="totalEnergy">Internal plus kinetic energy.</param>
    /// <param name="volume">Voxel volume.</param>
    /// <param name="pressurePerMoleKelvin">
    ///     <c>R / V</c>, so the pressure is the same expression as
    ///     <see cref="AtmosSolverMath.CalculatePressure" /> and agrees bitwise with the rest of the kernel.
    /// </param>
    /// <param name="state">
    ///     The primitive state, or all zeros unless the result is <see cref="EulerCellKind.Gas" />.
    /// </param>
    /// <returns>
    ///     <see cref="EulerCellKind.Empty" /> when the voxel holds no gas (<paramref name="mass" />,
    ///     <paramref name="moles" />, or <paramref name="heatCapacity" /> is not positive),
    ///     <see cref="EulerCellKind.NonFinite" /> when it does but its primitive state does not fit in float. The
    ///     caller is expected to zero the voxel's momentum in either case; nothing here does it.
    /// </returns>
    /// <remarks>
    ///     Internal energy is clamped at zero before dividing by the heat capacity: a voxel whose kinetic energy
    ///     exceeds its total energy (possible after a lossy transfer rounded the wrong way) comes out at 0 K and zero
    ///     pressure rather than with a negative temperature.
    /// </remarks>
    internal static EulerCellKind ToPrimitive(
        Kilogram mass, Mole moles, JoulePerKelvin64 heatCapacity,
        KilogramMetrePerSecond momentumX, KilogramMetrePerSecond momentumY, KilogramMetrePerSecond momentumZ,
        Joule64 totalEnergy, CubicMetre volume, PascalPerMoleKelvin pressurePerMoleKelvin,
        out EulerCellState state)
    {
        if (mass <= 0f || moles <= 0f || heatCapacity <= 0d)
        {
            state = default;
            return EulerCellKind.Empty;
        }

        PerKilogram inverseMass = 1f / mass;
        MetrePerSecond velocityX = momentumX * inverseMass;
        MetrePerSecond velocityY = momentumY * inverseMass;
        MetrePerSecond velocityZ = momentumZ * inverseMass;

        Joule64 kineticEnergy = KineticEnergy(momentumX, momentumY, momentumZ, velocityX, velocityY, velocityZ);
        Joule64 internalEnergy = Math.Max(0d, totalEnergy - kineticEnergy);
        Kelvin temperature = (float)(internalEnergy / heatCapacity);

        Pascal pressure = moles * temperature * pressurePerMoleKelvin;

        // N·R is the Cp − Cv gap of the whole mixture, so γ = (C + NR)/C, and c² = γ·NRT/m = γ·pV/m = γ·p/ρ.
        JoulePerKelvin molarGasTerm = moles * AtmosPhysicalConstants.MolarGasConstant;
        float gamma = (float)((heatCapacity + molarGasTerm) / heatCapacity);
        MetrePerSecond soundSpeed = MathF.Sqrt(gamma * molarGasTerm * temperature * inverseMass);
        JoulePerCubicMetre energyDensity = (float)(totalEnergy / volume);

        // The energy density is the usual casualty: C·T/V overflows float long before C does, and one infinity
        // in a face state turns the whole pencil to NaN within a sweep.
        if (!float.IsFinite(temperature) || !float.IsFinite(pressure) || !float.IsFinite(soundSpeed) ||
            !float.IsFinite(energyDensity) || !float.IsFinite(inverseMass))
        {
            state = default;
            return EulerCellKind.NonFinite;
        }

        state.Density = mass / volume;
        state.VelocityX = velocityX;
        state.VelocityY = velocityY;
        state.VelocityZ = velocityZ;
        state.Pressure = pressure;
        state.EnergyDensity = energyDensity;
        state.SoundSpeed = soundSpeed;
        state.InverseMass = inverseMass;
        state.Temperature = temperature;
        state.SignalSpeed =
            MathF.Max(MathF.Max(MathF.Abs(velocityX), MathF.Abs(velocityY)), MathF.Abs(velocityZ)) + soundSpeed;
        return EulerCellKind.Gas;
    }

    /// <summary>
    ///     Same as the nine-argument overload, computing <c>R / V</c> from <paramref name="volume" />. Prefer the
    ///     overload that takes the config snapshot's precomputed coefficient inside a sweep; this one exists for
    ///     callers that only have a volume.
    /// </summary>
    internal static EulerCellKind ToPrimitive(
        Kilogram mass, Mole moles, JoulePerKelvin64 heatCapacity,
        KilogramMetrePerSecond momentumX, KilogramMetrePerSecond momentumY, KilogramMetrePerSecond momentumZ,
        Joule64 totalEnergy, CubicMetre volume,
        out EulerCellState state)
    {
        PascalPerMoleKelvin pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / volume;
        return ToPrimitive(
            mass, moles, heatCapacity,
            momentumX, momentumY, momentumZ,
            totalEnergy, volume, pressurePerMoleKelvin,
            out state);
    }

    /// <summary>
    ///     Total energy <c>E = C·T + K</c> of a voxel from its temperature and momentum, the inverse of the
    ///     temperature computed by
    ///     <see cref="ToPrimitive(Kilogram, Mole, JoulePerKelvin64, KilogramMetrePerSecond, KilogramMetrePerSecond,
    ///     KilogramMetrePerSecond, Joule64, CubicMetre, PascalPerMoleKelvin, out EulerCellState)" />.
    /// </summary>
    /// <remarks>
    ///     The kinetic energy is formed from the same <c>1/m</c> and the same operand order as the forward
    ///     conversion, so <c>E − K</c> there recovers <c>C·T</c> to double precision and the temperature round-trips
    ///     to within a float ulp. A massless voxel is assumed to carry no momentum and contributes only <c>C·T</c>.
    /// </remarks>
    internal static Joule64 TotalEnergy(
        JoulePerKelvin64 heatCapacity, Kelvin temperature, Kilogram mass,
        KilogramMetrePerSecond momentumX, KilogramMetrePerSecond momentumY, KilogramMetrePerSecond momentumZ)
    {
        Joule64 internalEnergy = heatCapacity * temperature;
        return mass <= 0f
            ? internalEnergy
            : internalEnergy + KineticEnergy(mass, momentumX, momentumY, momentumZ);
    }

    /// <summary>
    ///     Kinetic energy of a voxel of mass <paramref name="mass" /> with the given momentum, formed from the same
    ///     <c>1/m</c> and operand order as the forward conversion so that <c>E − K</c> recovers <c>C·T</c> exactly
    ///     as <see cref="TotalEnergy" /> built it. <paramref name="mass" /> must be positive.
    /// </summary>
    internal static Joule64 KineticEnergy(
        Kilogram mass,
        KilogramMetrePerSecond momentumX, KilogramMetrePerSecond momentumY, KilogramMetrePerSecond momentumZ)
    {
        PerKilogram inverseMass = 1f / mass;
        return KineticEnergy(
            momentumX, momentumY, momentumZ,
            momentumX * inverseMass, momentumY * inverseMass, momentumZ * inverseMass);
    }

    /// <summary>
    ///     Kinetic energy <c>½·(Px·u + Py·v + Pz·w)</c>, with the velocities already divided out of the momentum.
    ///     Written as momentum times velocity rather than <c>|P|²/(2m)</c> so it costs no second divide, and summed
    ///     in double so the subtraction from the total energy does not lose the thermal part.
    /// </summary>
    private static Joule64 KineticEnergy(
        KilogramMetrePerSecond momentumX, KilogramMetrePerSecond momentumY, KilogramMetrePerSecond momentumZ,
        MetrePerSecond velocityX, MetrePerSecond velocityY, MetrePerSecond velocityZ)
    {
        return 0.5d * ((double)momentumX * velocityX + (double)momentumY * velocityY + (double)momentumZ * velocityZ);
    }
}
