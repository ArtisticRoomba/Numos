namespace Numos.CoreSim.Datatypes.Snapshots;

/// <param name="Volume">Volume in cubic metres (m³).</param>
/// <param name="Temperature">Stored temperature in kelvins (K).</param>
/// <param name="Pressure">Ideal-gas pressure in pascals (Pa).</param>
/// <param name="TotalMoles">Total amount in moles (mol).</param>
/// <param name="Gases">Positive gas amounts ordered by gas ID.</param>
public readonly record struct GasMixtureSnapshot(
    float Volume,
    float Temperature,
    float Pressure,
    float TotalMoles,
    KeyValuePair<int, float>[] Gases)
{
    public float GetMoles(int gasId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gasId);
        foreach (var (id, moles) in Gases)
        {
            if (id == gasId)
                return moles;
        }

        return 0f;
    }
}
