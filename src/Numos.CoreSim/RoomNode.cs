namespace Numos.CoreSim;

/// <summary>
///     Represents the "Macro Layer" (Equilibrium) of the simulation.
/// </summary>
internal struct RoomNode
{
    public int RoomId;
    public bool IsAsleep;
    public int TotalVoxelVolume;

    public float EquilibriumPressure;
    public float AverageTemperature;

    public float[] GasMoles;

    public void AddGas(int gasId, float addedMoles, float incomingTemp)
    {
        var currentTotalMoles = 0f;
        for (var i = 0; i < GasMoles.Length; i++)
            currentTotalMoles += GasMoles[i];

        if (currentTotalMoles + addedMoles > 0)
        {
            AverageTemperature = (currentTotalMoles * AverageTemperature + addedMoles * incomingTemp) /
                                 (currentTotalMoles + addedMoles);

            GasMoles[gasId] += addedMoles;
            EquilibriumPressure = (currentTotalMoles + addedMoles) * AverageTemperature / TotalVoxelVolume;
        }
    }

    public void RemoveGas(int gasId, float removedMoles)
    {
        var currentTotalMoles = 0f;
        for (var i = 0; i < GasMoles.Length; i++)
            currentTotalMoles += GasMoles[i];

        float actualRemoved = removedMoles;
        if (GasMoles[gasId] < actualRemoved)
            actualRemoved = GasMoles[gasId];

        float newTotalMoles = currentTotalMoles - actualRemoved;
        GasMoles[gasId] -= actualRemoved;

        if (newTotalMoles > 0)
            EquilibriumPressure = newTotalMoles * AverageTemperature / TotalVoxelVolume;
        else
            EquilibriumPressure = 0;
    }
}