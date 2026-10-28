using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.Workbench;

/// <summary>
///     Totals over the gases of one voxel.
/// </summary>
/// <param name="TotalMoles">Sum of the valid, positive amounts.</param>
/// <param name="PrimaryGasId">The gas with the most moles, or -1 when the voxel holds none.</param>
/// <param name="GasCount">Number of gases with a positive amount.</param>
/// <param name="InvalidCount">Number of gases with a non-finite or negative amount.</param>
public readonly record struct GasSummary(float TotalMoles, int PrimaryGasId, int GasCount, int InvalidCount);

/// <summary>
///     Helpers for reading gas amounts from voxel snapshots.
/// </summary>
public static class GasAmounts
{
    /// <summary>
    ///     Returns whether <paramref name="moles" /> is finite and not negative.
    /// </summary>
    public static bool IsValidGasAmount(float moles)
    {
        return float.IsFinite(moles) && moles >= 0f;
    }

    /// <summary>
    ///     Summarizes the gases of one voxel.
    /// </summary>
    /// <remarks>
    ///     Invalid amounts are counted but excluded from every other total. Ties for the primary gas go to the lowest ID.
    /// </remarks>
    public static GasSummary GetGasSummary(IReadOnlyList<VoxelGasSnapshot> gases)
    {
        float totalMoles = 0f;
        int primaryGasId = -1;
        int gasCount = 0;
        int invalidCount = 0;
        float maximumMoles = 0f;
        foreach (var gas in gases)
        {
            float moles = gas.Moles;
            if (!IsValidGasAmount(moles))
            {
                invalidCount++;
                continue;
            }

            if (moles > 0f)
            {
                totalMoles += moles;
                gasCount++;
            }

            if (moles > maximumMoles ||
                moles == maximumMoles && moles > 0f && (primaryGasId < 0 || gas.GasId < primaryGasId))
            {
                maximumMoles = moles;
                primaryGasId = gas.GasId;
            }
        }

        return new GasSummary(totalMoles, primaryGasId, gasCount, invalidCount);
    }
}
