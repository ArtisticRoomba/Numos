using System.Numerics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     One side of a batch of faces, a lane per face. The fields match <see cref="EulerFaceState" />.
/// </summary>
internal readonly struct HllcLanes
{
    public readonly Vector<float> Density;
    public readonly Vector<float> NormalVelocity;
    public readonly Vector<float> TangentialVelocity1;
    public readonly Vector<float> TangentialVelocity2;
    public readonly Vector<float> Pressure;
    public readonly Vector<float> EnergyDensity;
    public readonly Vector<float> SoundSpeed;
    public readonly Vector<float> InverseMass;

    public HllcLanes(
        Vector<float> density, Vector<float> normalVelocity, Vector<float> tangentialVelocity1,
        Vector<float> tangentialVelocity2, Vector<float> pressure, Vector<float> energyDensity,
        Vector<float> soundSpeed, Vector<float> inverseMass)
    {
        Density = density;
        NormalVelocity = normalVelocity;
        TangentialVelocity1 = tangentialVelocity1;
        TangentialVelocity2 = tangentialVelocity2;
        Pressure = pressure;
        EnergyDensity = energyDensity;
        SoundSpeed = soundSpeed;
        InverseMass = inverseMass;
    }
}

/// <summary>
///     The flux of a batch of faces, a lane per face. The fields match <see cref="EulerFaceFlux" />.
/// </summary>
internal readonly struct HllcLaneFlux
{
    public readonly Vector<float> Mass;
    public readonly Vector<float> NormalMomentum;
    public readonly Vector<float> Tangential1Momentum;
    public readonly Vector<float> Tangential2Momentum;
    public readonly Vector<float> Energy;
    public readonly Vector<float> SpeciesWeight;

    /// <summary>
    ///     All bits set in the lanes whose upwind cell is the left one.
    /// </summary>
    public readonly Vector<int> UpwindIsLeft;

    public HllcLaneFlux(
        Vector<float> mass, Vector<float> normalMomentum, Vector<float> tangential1Momentum,
        Vector<float> tangential2Momentum, Vector<float> energy, Vector<float> speciesWeight,
        Vector<int> upwindIsLeft)
    {
        Mass = mass;
        NormalMomentum = normalMomentum;
        Tangential1Momentum = tangential1Momentum;
        Tangential2Momentum = tangential2Momentum;
        Energy = energy;
        SpeciesWeight = speciesWeight;
        UpwindIsLeft = upwindIsLeft;
    }
}

/// <summary>
///     <see cref="HllcFlux.Solve" /> for a vector of faces at once, without the low-Mach fix.
/// </summary>
/// <remarks>
///     <para>
///         Each lane does what <see cref="HllcFlux.Solve" /> does for one face, with the same IEEE operations on the
///         same operands in the same order, so a lane gives the scalar result bit for bit. The kernel evaluates all
///         four regions in every lane and picks one with masks, in the same order as the scalar branches. A
///         division the scalar code skips (<c>impedanceGap == 0</c>, <c>a == b</c>) still runs here and a mask
///         discards it, because a float division never traps. The kernel uses no fused multiply-add and no
///         reciprocal. A vector therefore gives the same bits with or without hardware support.
///     </para>
///     <para>
///         <see cref="Vector.Min{T}" /> and <see cref="Vector.Max{T}" /> match <see cref="MathF.Min" /> and
///         <see cref="MathF.Max" /> for NaN and for signed zeros. A compare with a NaN operand is false, as in
///         scalar code. <c>HllcFluxVectorTests</c> checks this with random bits.
///     </para>
/// </remarks>
internal static class HllcFluxVector
{
    /// <summary>
    ///     Number of faces in one vector.
    /// </summary>
    internal static int Width => Vector<float>.Count;

    /// <summary>
    ///     Whether the lane kernel runs on vector hardware. When it is <see langword="false" /> the sweep keeps every
    ///     face on the scalar solver. Tests set it to compare both paths.
    /// </summary>
    internal static bool Enabled = Vector.IsHardwareAccelerated && Vector<float>.Count >= 4;

    /// <summary>
    ///     Solves one face per lane.
    /// </summary>
    /// <param name="left">The lower-coordinate cell of each face. A vacuum lane has every field zero.</param>
    /// <param name="right">The higher-coordinate cell of each face.</param>
    /// <returns>The per-area flux of each face.</returns>
    internal static HllcLaneFlux Solve(in HllcLanes left, in HllcLanes right)
    {
        Vector<float> zero = Vector<float>.Zero;

        Vector<int> bothVacuum = Vector.Equals(left.Density, zero) & Vector.Equals(right.Density, zero);

        // EstimateWaves.
        Vector<float> leftSpeed = Vector.Min(
            left.NormalVelocity - left.SoundSpeed, right.NormalVelocity - right.SoundSpeed);
        Vector<float> rightSpeed = Vector.Max(
            left.NormalVelocity + left.SoundSpeed, right.NormalVelocity + right.SoundSpeed);
        Vector<float> a = left.Density * (leftSpeed - left.NormalVelocity);
        Vector<float> b = right.Density * (rightSpeed - right.NormalVelocity);
        Vector<float> impedanceGap = a - b;
        Vector<float> contactQuotient =
            ((right.Pressure - left.Pressure) + (a * left.NormalVelocity - b * right.NormalVelocity)) / impedanceGap;
        Vector<float> contactSpeed = Vector.ConditionalSelect(Vector.Equals(impedanceGap, zero), zero, contactQuotient);

        // StarPressures. The momentum star pressure equals the energy one because theta is exactly 1.
        Vector<float> impedanceWeight = (a + b) / (b - a);
        Vector<float> half = new Vector<float>(0.5f);
        Vector<float> centredPressure =
            half * (left.Pressure + right.Pressure) +
            half * impedanceWeight * (left.Pressure - right.Pressure);
        Vector<float> jumpPressure = a * b * (right.NormalVelocity - left.NormalVelocity) / (b - a);
        Vector<float> starPressure = Vector.ConditionalSelect(Vector.Equals(a, b), zero, centredPressure + jumpPressure);

        // Region selection, in the order of the scalar branches.
        Vector<int> physicalLeft = Vector.GreaterThanOrEqual(leftSpeed, zero);
        Vector<int> physicalRight = Vector.AndNot(Vector.LessThanOrEqual(rightSpeed, zero), physicalLeft);
        Vector<int> starLeft = Vector.GreaterThanOrEqual(contactSpeed, zero);

        // Physical flux of the cell whose state the face sees in a supersonic region.
        Vector<float> pDensity = Vector.ConditionalSelect(physicalLeft, left.Density, right.Density);
        Vector<float> pNormalVelocity = Vector.ConditionalSelect(physicalLeft, left.NormalVelocity, right.NormalVelocity);
        Vector<float> pTangential1 =
            Vector.ConditionalSelect(physicalLeft, left.TangentialVelocity1, right.TangentialVelocity1);
        Vector<float> pTangential2 =
            Vector.ConditionalSelect(physicalLeft, left.TangentialVelocity2, right.TangentialVelocity2);
        Vector<float> pPressure = Vector.ConditionalSelect(physicalLeft, left.Pressure, right.Pressure);
        Vector<float> pEnergyDensity = Vector.ConditionalSelect(physicalLeft, left.EnergyDensity, right.EnergyDensity);
        Vector<float> physicalMass = pDensity * pNormalVelocity;
        Vector<float> physicalNormal = physicalMass * pNormalVelocity + pPressure;
        Vector<float> physicalTangential1 = physicalMass * pTangential1;
        Vector<float> physicalTangential2 = physicalMass * pTangential2;
        Vector<float> physicalEnergy = pNormalVelocity * (pEnergyDensity + pPressure);

        // Star flux of the cell on the side of the contact. The scalar code divides by this side's gap alone.
        Vector<float> sDensity = Vector.ConditionalSelect(starLeft, left.Density, right.Density);
        Vector<float> sNormalVelocity = Vector.ConditionalSelect(starLeft, left.NormalVelocity, right.NormalVelocity);
        Vector<float> sTangential1 =
            Vector.ConditionalSelect(starLeft, left.TangentialVelocity1, right.TangentialVelocity1);
        Vector<float> sTangential2 =
            Vector.ConditionalSelect(starLeft, left.TangentialVelocity2, right.TangentialVelocity2);
        Vector<float> sPressure = Vector.ConditionalSelect(starLeft, left.Pressure, right.Pressure);
        Vector<float> sEnergyDensity = Vector.ConditionalSelect(starLeft, left.EnergyDensity, right.EnergyDensity);
        Vector<float> outerSpeed = Vector.ConditionalSelect(starLeft, leftSpeed, rightSpeed);
        Vector<float> starMassFlux = sDensity * sNormalVelocity;
        Vector<float> gap = outerSpeed - contactSpeed;
        Vector<float> starMass = contactSpeed * (outerSpeed * sDensity - starMassFlux) / gap;
        Vector<float> starNormal = starPressure +
                                   contactSpeed *
                                   (outerSpeed * starMassFlux - (starMassFlux * sNormalVelocity + sPressure) +
                                    starPressure) / gap;
        Vector<float> starTangential1 = contactSpeed *
                                        (outerSpeed * (sDensity * sTangential1) - starMassFlux * sTangential1) / gap;
        Vector<float> starTangential2 = contactSpeed *
                                        (outerSpeed * (sDensity * sTangential2) - starMassFlux * sTangential2) / gap;
        Vector<float> starEnergy = contactSpeed *
                                   (outerSpeed * sEnergyDensity -
                                    sNormalVelocity * (sEnergyDensity + sPressure) +
                                    outerSpeed * starPressure) / gap;

        Vector<int> physical = physicalLeft | physicalRight;
        Vector<float> mass = Vector.ConditionalSelect(physical, physicalMass, starMass);
        Vector<float> normal = Vector.ConditionalSelect(physical, physicalNormal, starNormal);
        Vector<float> tangential1 = Vector.ConditionalSelect(physical, physicalTangential1, starTangential1);
        Vector<float> tangential2 = Vector.ConditionalSelect(physical, physicalTangential2, starTangential2);
        Vector<float> energy = Vector.ConditionalSelect(physical, physicalEnergy, starEnergy);

        Vector<int> upwindIsLeft = physicalLeft | Vector.AndNot(starLeft, physicalRight);
        Vector<float> upwindInverseMass = Vector.ConditionalSelect(
            upwindIsLeft, left.InverseMass, right.InverseMass);
        Vector<float> speciesWeight = Vector.ConditionalSelect(
            Vector.Equals(mass, zero), zero, mass * upwindInverseMass);

        // A vacuum on both sides has no flux, and the left cell is the upwind one.
        Vector<float> keepBits = Vector.AsVectorSingle(~bothVacuum);
        return new HllcLaneFlux(
            Vector.BitwiseAnd(mass, keepBits),
            Vector.BitwiseAnd(normal, keepBits),
            Vector.BitwiseAnd(tangential1, keepBits),
            Vector.BitwiseAnd(tangential2, keepBits),
            Vector.BitwiseAnd(energy, keepBits),
            Vector.BitwiseAnd(speciesWeight, keepBits),
            upwindIsLeft | bothVacuum);
    }
}
