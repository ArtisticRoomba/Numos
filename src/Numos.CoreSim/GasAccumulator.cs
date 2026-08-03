namespace Numos;

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
    public float AccumulatedMoles;
    public float OutputTemperature;
    public int TicksAlive;

    public void AddGas(float moles, float temp)
    {
        if (AccumulatedMoles + moles > 0)
        {
            OutputTemperature = (AccumulatedMoles * OutputTemperature + moles * temp) / (AccumulatedMoles + moles);
        }
        else
        {
            OutputTemperature = temp;
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
    /// <param name="currentPressureDelta">The calculated local pressure spike |P_spike - P_room|.</param>
    /// <param name="wakeThreshold">The constant 'Threshold of Violence' (tau_wake).</param>
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