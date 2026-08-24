namespace Numos.CoreSim;

internal enum AccumulatorState
{
    Hold,
    Diffuse,
    Inject
}

/// <summary>
///     Solves the "Leaky Faucet Problem" (Sustained Event Trap).
///     <para>
///         Small leaks below the "Threshold of Violence" are accumulated here instead of waking up the voxel grid
///         immediately.
///         Once the accumulator breaches the threshold (or times out), it triggers an injection event.
///     </para>
/// </summary>
internal struct GasAccumulator
{
    public int GasId;

    /// <summary>Accumulated amount, in moles (mol).</summary>
    public float AccumulatedMoles;

    /// <summary>Mole-weighted output temperature, in kelvins (K).</summary>
    public float OutputTemperature;
    public int TicksAlive;

    /// <summary>Adds one sample of this accumulator's gas species.</summary>
    /// <param name="moles">Amount to add, in moles (mol).</param>
    /// <param name="temperature">Sample temperature, in kelvins (K).</param>
    public void AddGas(float moles, float temperature)
    {
        if (AccumulatedMoles + moles > 0)
        {
            OutputTemperature = (AccumulatedMoles * OutputTemperature + moles * temperature) /
                                (AccumulatedMoles + moles);
        }
        else
        {
            OutputTemperature = temperature;
        }

        AccumulatedMoles += moles;
        TicksAlive++;
    }

    public void Reset()
    {
        AccumulatedMoles = 0f;
        OutputTemperature = 0f;
        TicksAlive = 0;
    }

    /// <summary>
    ///     Determines if the accumulated gas should trigger a violent Micro Injection (wake chunk)
    ///     or a passive Macro Diffusion (add to room node).
    /// </summary>
    /// <param name="currentPressureDelta">The calculated local pressure spike |P_spike - P_room|, in pascals.</param>
    /// <param name="wakeThreshold">The pressure threshold for waking the micro solver, in pascals.</param>
    /// <param name="maxAliveTicks">Maximum ticks before the accumulator times out and diffuses.</param>
    public AccumulatorState EvaluateState(float currentPressureDelta, float wakeThreshold, int maxAliveTicks)
    {
        if (currentPressureDelta > wakeThreshold)
        {
            return AccumulatorState.Inject;
        }

        if (TicksAlive >= maxAliveTicks)
        {
            return AccumulatorState.Diffuse;
        }

        return AccumulatorState.Hold;
    }
}
