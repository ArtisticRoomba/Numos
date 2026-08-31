namespace Numos.CoreSim;

/// <summary>
///     Represents the "Macro Layer" (Equilibrium) of the simulation.
/// </summary>
internal struct RoomNode
{
    /// <summary>Logical room identifier.</summary>
    public int RoomId;

    /// <summary>Whether the room is represented by its aggregate macro state.</summary>
    public bool IsAsleep;

    /// <summary>Number of voxels represented by this room.</summary>
    public int VoxelCount;

    /// <summary>Physical volume of each voxel, in cubic metres (m³).</summary>
    /// <remarks>Non-finite and nonpositive values are normalized to 1 m³.</remarks>
    public CubicMetre VoxelVolume;

    /// <summary>Aggregate ideal-gas pressure, in pascals (Pa).</summary>
    public Pascal EquilibriumPressure;

    /// <summary>Heat-capacity-weighted average temperature, in kelvins (K).</summary>
    public Kelvin AverageTemperature;

    /// <summary>Aggregate constant-volume heat capacity, in joules per kelvin (J/K).</summary>
    public JoulePerKelvin TotalHeatCapacity;

    /// <summary>Total gas amount in the room, in moles (mol).</summary>
    public Mole TotalMoles;

    /// <summary>Mole amount for each gas ID, in moles (mol).</summary>
    public Mole[] GasMoles;

    public void AddGas(
        int gasId, Mole addedMoles, Kelvin incomingTemp,
        JoulePerMoleKelvin molarHeatCapacityAtConstantVolume)
    {
        JoulePerKelvin incomingHeatCapacity = addedMoles * molarHeatCapacityAtConstantVolume;
        JoulePerKelvin newHeatCapacity = TotalHeatCapacity + incomingHeatCapacity;
        Mole newTotalMoles = TotalMoles + addedMoles;
        if (newTotalMoles > 0f && newHeatCapacity > 0f)
        {
            AverageTemperature = TotalHeatCapacity > 0f && AverageTemperature == incomingTemp
                ? AverageTemperature
                : AverageTemperature +
                  (incomingTemp - AverageTemperature) * incomingHeatCapacity / newHeatCapacity;

            GasMoles[gasId] += addedMoles;
            TotalHeatCapacity = newHeatCapacity;
            TotalMoles = newTotalMoles;
            EquilibriumPressure = CalculatePressure(TotalMoles);
        }
    }

    public void RemoveGas(int gasId, Mole removedMoles, JoulePerMoleKelvin molarHeatCapacityAtConstantVolume)
    {
        Mole actualRemoved = removedMoles;
        if (GasMoles[gasId] < actualRemoved)
            actualRemoved = GasMoles[gasId];

        Mole newTotalMoles = MathF.Max(0f, TotalMoles - actualRemoved);
        GasMoles[gasId] -= actualRemoved;
        TotalHeatCapacity = MathF.Max(
            0f,
            TotalHeatCapacity - actualRemoved * molarHeatCapacityAtConstantVolume);

        TotalMoles = newTotalMoles;

        if (newTotalMoles > 0)
            EquilibriumPressure = CalculatePressure(newTotalMoles);
        else
            EquilibriumPressure = 0;
    }

    private readonly Pascal CalculatePressure(Mole totalMoles)
    {
        if (!float.IsFinite(totalMoles) ||
            totalMoles <= 0f ||
            !float.IsFinite(AverageTemperature) ||
            AverageTemperature <= 0f ||
            VoxelCount <= 0)
            return 0f;

        CubicMetre voxelVolume = float.IsFinite(VoxelVolume) && VoxelVolume > 0f
            ? VoxelVolume
            : AtmosConfigDefaults.VoxelVolume;

        return totalMoles /
               VoxelCount /
               voxelVolume *
               AtmosPhysicalConstants.MolarGasConstant *
               AverageTemperature;
    }
}