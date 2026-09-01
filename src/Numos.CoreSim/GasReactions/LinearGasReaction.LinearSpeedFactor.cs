namespace Numos.CoreSim.GasReactions;

public readonly partial record struct LinearGasReaction
{
    public readonly record struct LinearSpeedFactor(
        GasProperties Gas,
        float LowMolarityBound,
        float HighMolarityBound,
        float LowMolaritySpeed,
        float HighMolaritySpeed,
        bool LowStrict,
        bool HighStrict)
    {
        public GasProperties Gas { get; } = Gas;

        /// <summary>
        ///     Can the reaction occur below low molarity bound (extending linear graph)
        /// </summary>
        public bool LowStrict { get; } = LowStrict;

        /// <summary>
        ///     Can the reaction occur above high molarity bound (extending linear graph)
        /// </summary>
        public bool HighStrict { get; } = HighStrict;

        private float BoundaryRange { get; } = HighMolarityBound - LowMolarityBound;

        private float FactorRange { get; } = HighMolaritySpeed - LowMolaritySpeed;
        private float HighMolaritySpeed { get; } = HighMolaritySpeed;
        private float HighMolarityBound { get; } = HighMolarityBound;

        public float GetFactor(float molarity)
        {
            return EvalLinear(
                molarity,
                BoundaryRange,
                LowMolarityBound,
                LowStrict,
                HighStrict,
                LowMolaritySpeed,
                FactorRange);
        }
    }
}