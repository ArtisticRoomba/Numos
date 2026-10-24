using Magnitudo;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
/// Derives mass density, its gradient, and numerical schlieren from detached snapshot data.
/// </summary>
/// <remarks>
/// <para>
/// Every front end that shows these quantities calls this class, so the formulas live in one place. The Workbench
/// exports and the SimDrawer visualizations both use it.
/// </para>
/// <para>
/// A density array holds NaN for a voxel that has no air (solid, void, or outside every chunk). The gradient treats
/// NaN as "unavailable" and falls back to a one-sided difference.
/// </para>
/// <para>
/// All methods read only their arguments and never change the simulation.
/// </para>
/// </remarks>
public static class FlowFieldMath
{
    /// <summary>
    /// The default steepness <c>k</c> of the numerical schlieren transform.
    /// </summary>
    public const float DefaultSchlierenSteepness = 15f;

    /// <summary>
    /// Gets the edge length of a voxel.
    /// </summary>
    /// <param name="config">The configuration that supplies the voxel volume.</param>
    /// <returns>The cube root of the voxel volume, the same edge length the Euler solver uses.</returns>
    public static Metre GetVoxelEdge(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return MathF.Cbrt(config.GetVoxelVolume());
    }

    /// <summary>
    /// Computes the mass density of one voxel.
    /// </summary>
    /// <param name="gases">The gas channels of a snapshot.</param>
    /// <param name="localIndex">The voxel index inside the chunk.</param>
    /// <param name="config">The configuration that supplies the molar masses and the voxel volume.</param>
    /// <returns>
    /// The density <c>sum(n_k * M_k) / V</c>. Gases with a non-finite or negative amount do not count. The result is
    /// zero for a voxel with no gas.
    /// </returns>
    public static KilogramPerCubicMetre GetDensity(GasSnapshot[] gases, int localIndex, AtmosConfig config)
    {
        return GetDensity(gases, localIndex, config, config.GetVoxelVolume());
    }

    /// <summary>
    /// Computes the mass density of every voxel of a chunk.
    /// </summary>
    /// <param name="snapshot">A snapshot that holds the voxel classification and the gas channels.</param>
    /// <param name="config">The configuration that supplies the molar masses and the voxel volume.</param>
    /// <param name="destination">Receives one value per voxel. It must hold at least that many entries.</param>
    /// <remarks>
    /// A solid or void voxel gets NaN. A snapshot without gas channels gives NaN everywhere.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public static void ComputeDensity(
        in AtmosChunkSnapshot snapshot,
        AtmosConfig config,
        [ElementQuantity("density")] Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(config);
        int voxelCount = snapshot.Dimensions.X * snapshot.Dimensions.Y * snapshot.Dimensions.Z;
        if (destination.Length < voxelCount)
            throw new ArgumentException("The destination is too short.", nameof(destination));

        GasSnapshot[]? gases = snapshot.Gases;
        if (gases == null || snapshot.VoxelRoomMap == null || snapshot.VoxelRoomMap.Length != voxelCount)
        {
            destination[..voxelCount].Fill(float.NaN);
            return;
        }

        CubicMetre volume = config.GetVoxelVolume();
        for (int index = 0; index < voxelCount; index++)
        {
            int room = snapshot.VoxelRoomMap[index];
            destination[index] = room == VoxelClassification.RoomSolid || room == VoxelClassification.RoomVoid
                ? float.NaN
                : GetDensity(gases, index, config, volume);
        }
    }

    /// <summary>
    /// Computes the density gradient magnitude from a density grid that has a one-voxel halo.
    /// </summary>
    /// <param name="padded">
    /// The densities with x changing fastest. The grid is one voxel larger than the interior on every side. NaN marks
    /// a voxel without air.
    /// </param>
    /// <param name="interior">The size of the interior in voxels. The grid holds <c>interior + 2</c> voxels per axis.</param>
    /// <param name="edge">The voxel edge length.</param>
    /// <param name="destination">Receives the interior values with x changing fastest.</param>
    /// <remarks>
    /// The destination is NaN where the interior density is NaN. The corner and edge cells of the halo are never read,
    /// so a caller can leave them NaN.
    /// </remarks>
    /// <exception cref="ArgumentException">A span is too short or the interior is not positive.</exception>
    public static void ComputeGradientMagnitude(
        [ElementQuantity("density")] ReadOnlySpan<float> padded,
        Int3 interior,
        Metre edge,
        [ElementQuantity("densityGradient")] Span<float> destination)
    {
        if (interior.X <= 0 || interior.Y <= 0 || interior.Z <= 0)
            throw new ArgumentException("The interior size must be positive.", nameof(interior));

        int paddedX = interior.X + 2;
        int paddedXY = paddedX * (interior.Y + 2);
        if (padded.Length < paddedXY * (interior.Z + 2))
            throw new ArgumentException("The padded grid is too short.", nameof(padded));

        if (destination.Length < interior.X * interior.Y * interior.Z)
            throw new ArgumentException("The destination is too short.", nameof(destination));

        int output = 0;
        for (int z = 0; z < interior.Z; z++)
        for (int y = 0; y < interior.Y; y++)
        {
            int row = (z + 1) * paddedXY + (y + 1) * paddedX + 1;
            for (int x = 0; x < interior.X; x++, output++)
            {
                int center = row + x;
                KilogramPerQuarticMetre gradient = GradientMagnitude(
                    padded[center],
                    padded[center - 1],
                    padded[center + 1],
                    padded[center - paddedX],
                    padded[center + paddedX],
                    padded[center - paddedXY],
                    padded[center + paddedXY],
                    edge);

                destination[output] = gradient;
            }
        }
    }

    /// <summary>
    /// Computes the density gradient magnitude at one voxel from its six face neighbors.
    /// </summary>
    /// <param name="center">The density of the voxel, or NaN when it has no air.</param>
    /// <param name="minusX">The density of the neighbor at -x, or NaN when it has no air.</param>
    /// <param name="plusX">The density of the neighbor at +x, or NaN when it has no air.</param>
    /// <param name="minusY">The density of the neighbor at -y, or NaN when it has no air.</param>
    /// <param name="plusY">The density of the neighbor at +y, or NaN when it has no air.</param>
    /// <param name="minusZ">The density of the neighbor at -z, or NaN when it has no air.</param>
    /// <param name="plusZ">The density of the neighbor at +z, or NaN when it has no air.</param>
    /// <param name="edge">The voxel edge length.</param>
    /// <returns>
    /// <c>|grad rho|</c>, or NaN when <paramref name="center" /> is NaN.
    /// </returns>
    /// <remarks>
    /// Each axis uses a central difference <c>(plus - minus) / (2 h)</c> when both neighbors exist. It uses a one-sided
    /// difference <c>(plus - center) / h</c> or <c>(center - minus) / h</c> when only one exists. It contributes zero
    /// when neither exists.
    /// </remarks>
    [return: Quantity("densityGradient")]
    public static KilogramPerQuarticMetre GradientMagnitude(
        [Quantity("density")] KilogramPerCubicMetre center,
        [Quantity("density")] KilogramPerCubicMetre minusX,
        [Quantity("density")] KilogramPerCubicMetre plusX,
        [Quantity("density")] KilogramPerCubicMetre minusY,
        [Quantity("density")] KilogramPerCubicMetre plusY,
        [Quantity("density")] KilogramPerCubicMetre minusZ,
        [Quantity("density")] KilogramPerCubicMetre plusZ,
        [Quantity("length")] Metre edge)
    {
        if (!float.IsFinite(center))
            return float.NaN;

        float dx = Derivative(minusX, center, plusX, edge);
        float dy = Derivative(minusY, center, plusY, edge);
        float dz = Derivative(minusZ, center, plusZ, edge);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Maps a density gradient magnitude to a numerical schlieren brightness.
    /// </summary>
    /// <param name="gradient">The gradient magnitude <c>|grad rho|</c>.</param>
    /// <param name="reference">The gradient magnitude that maps to the darkest value, usually the maximum in view.</param>
    /// <param name="steepness">The dimensionless steepness <c>k</c>. Values from 10 to 20 are typical.</param>
    /// <returns>
    /// <c>exp(-k * gradient / reference)</c> in (0, 1]. The result is 1 when <paramref name="reference" /> is not
    /// positive and finite, and NaN when <paramref name="gradient" /> is NaN.
    /// </returns>
    /// <example>
    /// A shock with the steepest gradient in the picture maps to <c>exp(-15)</c>, which is nearly black. A uniform
    /// field maps to 1.
    /// </example>
    public static float Schlieren(
        [Quantity("densityGradient")] KilogramPerQuarticMetre gradient,
        [Quantity("densityGradient")] KilogramPerQuarticMetre reference,
        Scalar steepness)
    {
        if (float.IsNaN(gradient))
            return float.NaN;

        if (!float.IsFinite(reference) || reference <= 0f)
            return 1f;

        return MathF.Exp(-steepness * (float)gradient / (float)reference);
    }

    /// <summary>
    /// Finds the largest finite value of a span.
    /// </summary>
    /// <param name="values">The values to scan.</param>
    /// <returns>The largest finite value, or 0 when none is finite.</returns>
    public static float MaximumFinite(ReadOnlySpan<float> values)
    {
        float maximum = 0f;
        foreach (float value in values)
        {
            if (float.IsFinite(value) && value > maximum)
                maximum = value;
        }

        return maximum;
    }

    private static KilogramPerCubicMetre GetDensity(
        GasSnapshot[] gases,
        int localIndex,
        AtmosConfig config,
        CubicMetre volume)
    {
        Kilogram mass = 0f;
        foreach (var gas in gases)
        {
            Mole moles = gas.Moles[localIndex];
            if (float.IsFinite(moles) && moles > 0f)
                mass += moles * config.GetMolarMass(gas.GasId);
        }

        return mass / volume;
    }

    private static float Derivative(float minus, float center, float plus, float edge)
    {
        bool hasMinus = float.IsFinite(minus);
        bool hasPlus = float.IsFinite(plus);
        if (hasMinus && hasPlus)
            return (plus - minus) / (2f * edge);

        if (hasPlus)
            return (plus - center) / edge;

        return hasMinus ? (center - minus) / edge : 0f;
    }
}
