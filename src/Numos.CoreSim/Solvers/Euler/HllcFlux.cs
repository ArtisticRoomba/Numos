namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Primitive state on one side of a face, with the velocity split into the component normal to the face and
///     the two tangential ones. A vacuum has every field zero.
/// </summary>
internal struct EulerFaceState
{
    public KilogramPerCubicMetre Density;
    public MetrePerSecond NormalVelocity;
    public MetrePerSecond TangentialVelocity1;
    public MetrePerSecond TangentialVelocity2;
    public Pascal Pressure;

    /// <summary>
    ///     Total (internal plus kinetic) energy per unit volume, <c>E / V</c>.
    /// </summary>
    public JoulePerCubicMetre EnergyDensity;

    public MetrePerSecond SoundSpeed;

    /// <summary>
    ///     <c>1 / m</c> of the cell, used to turn a mass flux into the per-mole species weight. It is zero for a vacuum.
    /// </summary>
    public PerKilogram InverseMass;

    /// <summary>
    ///     Projects a cell's primitive state onto a face whose normal is the given grid axis (0 = X, 1 = Y,
    ///     2 = Z). The tangential components follow the cyclic order X → Y → Z → X, so for a Y face the first
    ///     tangential is Z and the second is X. A caller scattering <see cref="EulerFaceFlux" /> tangential momenta
    ///     back onto the grid must use the same permutation.
    /// </summary>
    internal static EulerFaceState FromCell(EulerChunkState cells, int index, int axis)
    {
        if ((uint)axis > 2u)
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "Axis must be 0, 1, or 2.");

        EulerFaceState state;
        state.Density = cells.CellDensity[index];
        state.Pressure = cells.CellPressure[index];
        state.EnergyDensity = cells.CellEnergyDensity[index];
        state.SoundSpeed = cells.CellSoundSpeed[index];
        state.InverseMass = cells.CellInverseMass[index];
        state.NormalVelocity = cells.CellVelocity(axis)[index];
        state.TangentialVelocity1 = cells.CellVelocity((axis + 1) % 3)[index];
        state.TangentialVelocity2 = cells.CellVelocity((axis + 2) % 3)[index];
        return state;
    }

    /// <summary>
    ///     Same as <see cref="FromCell(EulerChunkState, int, int)" />, from a cell value already read out.
    /// </summary>
    internal static EulerFaceState FromCell(in EulerCellState cell, int axis)
    {
        EulerFaceState state;
        state.Density = cell.Density;
        state.Pressure = cell.Pressure;
        state.EnergyDensity = cell.EnergyDensity;
        state.SoundSpeed = cell.SoundSpeed;
        state.InverseMass = cell.InverseMass;
        switch (axis)
        {
            case 0:
                state.NormalVelocity = cell.VelocityX;
                state.TangentialVelocity1 = cell.VelocityY;
                state.TangentialVelocity2 = cell.VelocityZ;
                break;
            case 1:
                state.NormalVelocity = cell.VelocityY;
                state.TangentialVelocity1 = cell.VelocityZ;
                state.TangentialVelocity2 = cell.VelocityX;
                break;
            case 2:
                state.NormalVelocity = cell.VelocityZ;
                state.TangentialVelocity1 = cell.VelocityX;
                state.TangentialVelocity2 = cell.VelocityY;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis), axis, "Axis must be 0, 1, or 2.");
        }

        return state;
    }
}

/// <summary>
///     Flux through a face per unit area, positive from the lower-coordinate (left) cell to the higher one.
///     The caller multiplies by the face area and the time step.
/// </summary>
internal struct EulerFaceFlux
{
    public KilogramPerSquareMetreSecond Mass;
    public Pascal NormalMomentum;
    public Pascal Tangential1Momentum;
    public Pascal Tangential2Momentum;
    public WattPerSquareMetre Energy;

    /// <summary>
    ///     Which cell the mass flux is drawn from. Species are transported from this side.
    /// </summary>
    public bool UpwindIsLeft;

    /// <summary>
    ///     <see cref="Mass" /> divided by the upwind cell's mass, and zero when the mass flux is zero. Multiplying
    ///     by the face area and the upwind cell's <c>n_k</c> gives species <c>k</c>'s mole flux, so every species
    ///     moves in the proportion it has in the upwind cell.
    /// </summary>
    public PerSquareMetreSecond SpeciesWeight;
}

/// <summary>
///     HLLC approximate Riemann solver for the compressible Euler equations on one face, with Davis wave-speed
///     estimates, a reflective wall variant, and an optional low-Mach pressure fix.
/// </summary>
/// <remarks>
///     <para>
///         The conserved state is <c>U = (ρ, ρu, ρv, ρw, e)</c> with <c>e</c> the total energy density, and the
///         physical flux normal to the face is <c>F(U) = (ρu, ρu² + p, ρuv, ρuw, u(e + p))</c>. The solver picks
///         the outer wave speeds <c>S_L = min(u_L − c_L, u_R − c_R)</c> and <c>S_R = max(u_L + c_L, u_R + c_R)</c>,
///         then the contact speed <c>S*</c> and star pressure <c>p*</c> from the Rankine–Hugoniot conditions across
///         both outer waves, and returns whichever of the four regions sits on the face.
///     </para>
///     <para>
///         Everything is scalar float using only multiplies, adds, divides, and min/max, with no square roots or
///         transcendental calls, so results are reproducible across platforms. The formulas are arranged so that
///         reflecting the problem (swapping the cells and negating the normal velocities) negates every odd
///         quantity bitwise, which makes a face's flux independent of which chunk owns it. The remarks on
///         <see cref="Solve" /> say which operand orders that depends on. See
///         docs/atmospherics_technical_documentation.md section 5 for the wider model.
///     </para>
/// </remarks>
internal static class HllcFlux
{
    /// <summary>
    ///     Solves the face between <paramref name="left" /> (lower coordinate) and <paramref name="right" />.
    /// </summary>
    /// <param name="left">State of the lower-coordinate cell. The face normal points away from it.</param>
    /// <param name="right">State of the higher-coordinate cell. Either side may be a vacuum (all zeros).</param>
    /// <param name="allMach">
    ///     Scales the velocity-jump term of the star pressure used in the momentum flux by
    ///     <c>θ = min(1, max(Ma_L, Ma_R))</c>. At low Mach the standard HLLC pressure dissipation scales like
    ///     <c>ρ·c·Δu</c>, which swamps the <c>O(Ma²)</c> pressure fluctuations of nearly incompressible flow and
    ///     smears it out. The fix reduces that term to the acoustic scale. The energy flux keeps the standard star
    ///     pressure, so with <c>θ = 1</c> every component is bitwise identical to plain HLLC.
    /// </param>
    /// <param name="flux">The per-area flux, with the upwind side and species weight filled in.</param>
    /// <remarks>
    ///     <para>
    ///         With <c>a = ρ_L·(S_L − u_L) ≤ 0</c> and <c>b = ρ_R·(S_R − u_R) ≥ 0</c>, equating the momentum flux on
    ///         both sides of the contact gives
    ///         <c>S* = ((p_R − p_L) + (a·u_L − b·u_R)) / (a − b)</c> and
    ///         <c>p* = (b·p_L − a·p_R + a·b·(u_R − u_L)) / (b − a)</c>. The star pressure is evaluated in the
    ///         algebraically equal form <c>½(p_L + p_R) + ½·w·(p_L − p_R) + a·b·(u_R − u_L)/(b − a)</c> with
    ///         <c>w = (a + b)/(b − a)</c>, and the momentum flux in a star region as
    ///         <c>p* + S*·(S_K·ρu − (ρu·u + p_K) + p*)/(S_K − S*)</c> rather than the usual
    ///         <c>(S*·(S_K·U − F) + S_K·p*·D*)/(S_K − S*)</c>. The two rewrites are what make a gas at rest exactly
    ///         static: equal pressures give <c>p* == p</c> bitwise and <c>S* = ±0</c> zeroes every other term, so a
    ///         uniform region or a stationary contact feels the same pressure on both faces of each cell with no
    ///         round-off force.
    ///     </para>
    ///     <para>
    ///         Mirror symmetry rests on keeping the <c>a</c>/<c>b</c> operand form above. Reflecting the face maps
    ///         <c>(L, R, u) → (R, L, −u)</c>, under which <c>S_L' = −S_R</c>, <c>a' = −b</c>, <c>b' = −a</c>. IEEE
    ///         rounding is sign-symmetric and addition commutes, so every term of <c>S*</c> becomes its exact
    ///         negation and every term of <c>p*</c> maps to itself: <c>S*' = −S*</c> and <c>p*' = p*</c> bitwise.
    ///         The form <c>p* = p_L + a·(S* − u_L)</c> would break this because it is exact only on one side. The
    ///         star flux helper is covered by the same argument, since each of its factors is even or odd under the
    ///         reflection.
    ///     </para>
    ///     <para>
    ///         Region selection is symmetric too: the two supersonic cases are tested before the sign of
    ///         <c>S*</c>, so a reflected problem cannot land in a different region through a rounding difference in
    ///         <c>S*</c>. The one remaining asymmetry is <c>S* == 0</c> exactly, where both frames report the left
    ///         cell as upwind. The mass flux is exactly zero there, so nothing is transported either way.
    ///     </para>
    ///     <para>
    ///         Worked example, 1 atm air at rest (ρ = 1.205 kg/m³, c = 343 m/s, γ = 1.4) next to a vacuum:
    ///         <c>S_L = −c</c>, <c>S_R = c</c>, <c>a = −ρc</c>, <c>b = 0</c>, so <c>S* = p/(ρc) = c/γ ≈ 245 m/s</c>,
    ///         <c>p* = 0</c>, and the mass flux is <c>ρ·c/(γ + 1) ≈ 172 kg/(m²·s)</c>.
    ///     </para>
    /// </remarks>
    internal static void Solve(in EulerFaceState left, in EulerFaceState right, bool allMach, out EulerFaceFlux flux)
    {
        flux = default;
        flux.UpwindIsLeft = true;
        if (left.Density == 0f && right.Density == 0f)
            return;

        EstimateWaves(
            left, right,
            out MetrePerSecond leftSpeed, out MetrePerSecond rightSpeed,
            out KilogramPerSquareMetreSecond a, out KilogramPerSquareMetreSecond b,
            out MetrePerSecond contactSpeed);

        if (leftSpeed >= 0f)
        {
            PhysicalFlux(left, ref flux);
        }
        else if (rightSpeed <= 0f)
        {
            PhysicalFlux(right, ref flux);
            flux.UpwindIsLeft = false;
        }
        else
        {
            StarPressures(
                left, right, a, b, allMach,
                out Pascal starPressure, out Pascal momentumStarPressure);

            if (contactSpeed >= 0f)
            {
                StarFlux(left, leftSpeed, contactSpeed, starPressure, momentumStarPressure, ref flux);
            }
            else
            {
                StarFlux(right, rightSpeed, contactSpeed, starPressure, momentumStarPressure, ref flux);
                flux.UpwindIsLeft = false;
            }
        }

        flux.SpeciesWeight = flux.Mass == 0f
            ? 0f
            : flux.Mass * (flux.UpwindIsLeft ? left.InverseMass : right.InverseMass);
    }

    /// <summary>
    ///     Flux through a face between a gas cell and a solid wall: no mass, tangential momentum, or energy
    ///     crosses, only the wall pressure.
    /// </summary>
    /// <param name="air">State of the gas cell.</param>
    /// <param name="wallIsOnRight">
    ///     Whether the wall sits on the higher-coordinate side of the face. Only the sign of the normal velocity
    ///     depends on it. The pressure flux is the same either way.
    /// </param>
    /// <param name="flux">The per-area flux, <c>(0, p_wall, 0, 0, 0)</c>, with the gas cell marked as upwind.</param>
    /// <remarks>
    ///     <para>
    ///         A wall is the Riemann problem between the gas and its mirror image (same ρ and p, reversed normal
    ///         velocity). Writing <c>u_n</c> for the velocity component toward the wall, the Davis speeds are
    ///         <c>S = −(|u_n| + c)</c> and <c>−S</c>, the impedances are <c>a = ρ(S − u_n)</c> and <c>b = −a</c>, and
    ///         <c>S* = (a·u_n − b·(−u_n))/(a − b) = (a + b)·u_n/(2a) = 0</c> exactly, which is why the mass and
    ///         energy fluxes vanish without being computed. The star pressure collapses to
    ///         <c>p_wall = p + ρ·u_n·(u_n − S)</c>.
    ///     </para>
    ///     <para>
    ///         For gas receding from the wall (<c>u_n &lt; 0</c>) that is <c>p − ρ·c·|u_n|</c>, the linear rarefaction
    ///         pressure, clamped at zero once the gas pulls away faster than <c>c/γ</c>. For gas running into the
    ///         wall it is <c>p + ρ·u_n·(2u_n + c)</c>, the linearised reflected-shock pressure, above <c>p</c>. At
    ///         rest it is exactly <c>p</c>, matching the interior flux of a uniform gas bitwise.
    ///     </para>
    /// </remarks>
    internal static void SolveWall(in EulerFaceState air, bool wallIsOnRight, out EulerFaceFlux flux)
    {
        MetrePerSecond towardWall = wallIsOnRight ? air.NormalVelocity : -air.NormalVelocity;
        MetrePerSecond outerSpeed = -(MathF.Abs(towardWall) + air.SoundSpeed);
        Pascal wallPressure = MathF.Max(0f, air.Pressure + air.Density * towardWall * (towardWall - outerSpeed));

        flux = default;
        flux.NormalMomentum = wallPressure;
        flux.UpwindIsLeft = !wallIsOnRight;
    }

    /// <summary>
    ///     The wave speeds and standard (<c>θ = 1</c>) star pressure <see cref="Solve" /> would use, for tests.
    /// </summary>
    internal static void EstimateStarState(
        in EulerFaceState left, in EulerFaceState right,
        out MetrePerSecond leftSpeed, out MetrePerSecond rightSpeed,
        out MetrePerSecond contactSpeed, out Pascal starPressure)
    {
        EstimateWaves(
            left, right,
            out leftSpeed, out rightSpeed,
            out KilogramPerSquareMetreSecond a, out KilogramPerSquareMetreSecond b,
            out contactSpeed);
        StarPressures(left, right, a, b, allMach: false, out starPressure, out _);
    }

    private static void EstimateWaves(
        in EulerFaceState left, in EulerFaceState right,
        out MetrePerSecond leftSpeed, out MetrePerSecond rightSpeed,
        out KilogramPerSquareMetreSecond a, out KilogramPerSquareMetreSecond b,
        out MetrePerSecond contactSpeed)
    {
        // A vacuum side has u = c = 0 and so contributes the pair (0, 0) to each estimate on its own.
        leftSpeed = MathF.Min(left.NormalVelocity - left.SoundSpeed, right.NormalVelocity - right.SoundSpeed);
        rightSpeed = MathF.Max(left.NormalVelocity + left.SoundSpeed, right.NormalVelocity + right.SoundSpeed);

        a = left.Density * (leftSpeed - left.NormalVelocity);
        b = right.Density * (rightSpeed - right.NormalVelocity);

        KilogramPerSquareMetreSecond impedanceGap = a - b;
        contactSpeed = impedanceGap == 0f
            ? 0f
            : ((right.Pressure - left.Pressure) +
               (a * left.NormalVelocity - b * right.NormalVelocity)) / impedanceGap;
    }

    /// <summary>
    ///     Star pressure <c>(b·p_L − a·p_R + a·b·(u_R − u_L)) / (b − a)</c>, evaluated as a convex combination of
    ///     the two pressures with weight <c>w = (a + b)/(b − a) ∈ [−1, 1]</c> plus the velocity-jump term. Forming
    ///     <c>w</c> first makes the two cases that must be exact come out exact: equal pressures give
    ///     <c>½(p + p) + ½·w·0 = p</c>, and a vacuum side gives <c>w = ±1</c> so the centred part cancels to zero.
    ///     <paramref name="momentumStarPressure" /> has the jump term scaled by the low-Mach <c>θ</c> and is
    ///     bitwise equal to <paramref name="starPressure" /> whenever <c>θ = 1</c>.
    /// </summary>
    private static void StarPressures(
        in EulerFaceState left, in EulerFaceState right,
        KilogramPerSquareMetreSecond a, KilogramPerSquareMetreSecond b, bool allMach,
        out Pascal starPressure, out Pascal momentumStarPressure)
    {
        if (a == b)
        {
            // Both are zero: pressureless gas on both sides drifting apart, so the star region is empty and the
            // expressions below would be 0/0.
            starPressure = 0f;
            momentumStarPressure = 0f;
            return;
        }

        float impedanceWeight = (a + b) / (b - a);
        Pascal centredPressure =
            0.5f * (left.Pressure + right.Pressure) +
            0.5f * impedanceWeight * (left.Pressure - right.Pressure);
        Pascal jumpPressure = a * b * (right.NormalVelocity - left.NormalVelocity) / (b - a);
        starPressure = centredPressure + jumpPressure;

        float theta = 1f;
        if (allMach)
        {
            // A vacuum side has c = 0 and counts as sonic. The fix is only meant for smooth low-Mach flow.
            float leftMach = left.SoundSpeed > 0f ? MathF.Abs(left.NormalVelocity) / left.SoundSpeed : 1f;
            float rightMach = right.SoundSpeed > 0f ? MathF.Abs(right.NormalVelocity) / right.SoundSpeed : 1f;
            theta = MathF.Min(1f, MathF.Max(leftMach, rightMach));
        }

        // 1f * x is x bitwise, so θ = 1 reproduces starPressure exactly.
        momentumStarPressure = centredPressure + theta * jumpPressure;
    }

    /// <summary>
    ///     <c>F(U)</c> for a supersonic face: the whole fan lies on one side, so the face sees that cell's state.
    /// </summary>
    private static void PhysicalFlux(in EulerFaceState state, ref EulerFaceFlux flux)
    {
        KilogramPerSquareMetreSecond massFlux = state.Density * state.NormalVelocity;
        flux.Mass = massFlux;
        flux.NormalMomentum = massFlux * state.NormalVelocity + state.Pressure;
        flux.Tangential1Momentum = massFlux * state.TangentialVelocity1;
        flux.Tangential2Momentum = massFlux * state.TangentialVelocity2;
        flux.Energy = state.NormalVelocity * (state.EnergyDensity + state.Pressure);
    }

    /// <summary>
    ///     Flux in the star region adjacent to <paramref name="state" />, whose outer wave moves at
    ///     <paramref name="outerSpeed" />. Mass, tangential momentum, and energy carry the factor <c>S*</c> on the
    ///     outside so a zero contact speed gives exactly zero. The normal momentum is <c>p*</c> plus a correction
    ///     with the same factor.
    /// </summary>
    private static void StarFlux(
        in EulerFaceState state, MetrePerSecond outerSpeed, MetrePerSecond contactSpeed,
        Pascal starPressure, Pascal momentumStarPressure, ref EulerFaceFlux flux)
    {
        KilogramPerSquareMetreSecond massFlux = state.Density * state.NormalVelocity;
        // Negative on the left, positive on the right, never zero: the star region only exists strictly between
        // the outer waves.
        MetrePerSecond gap = outerSpeed - contactSpeed;

        flux.Mass = contactSpeed * (outerSpeed * state.Density - massFlux) / gap;
        flux.NormalMomentum = momentumStarPressure +
                              contactSpeed *
                              (outerSpeed * massFlux - (massFlux * state.NormalVelocity + state.Pressure) +
                               momentumStarPressure) / gap;
        flux.Tangential1Momentum = contactSpeed *
                                   (outerSpeed * (state.Density * state.TangentialVelocity1) -
                                    massFlux * state.TangentialVelocity1) / gap;
        flux.Tangential2Momentum = contactSpeed *
                                   (outerSpeed * (state.Density * state.TangentialVelocity2) -
                                    massFlux * state.TangentialVelocity2) / gap;
        flux.Energy = contactSpeed *
                      (outerSpeed * state.EnergyDensity -
                       state.NormalVelocity * (state.EnergyDensity + state.Pressure) +
                       outerSpeed * starPressure) / gap;
    }
}
