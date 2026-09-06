using Numos.CoreSim.Replay;

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

        internal (int, int, int, int, bool, bool) OrderKey => (
            BitConverter.SingleToInt32Bits(LowMolarityBound), BitConverter.SingleToInt32Bits(HighMolarityBound),
            BitConverter.SingleToInt32Bits(LowMolaritySpeed), BitConverter.SingleToInt32Bits(HighMolaritySpeed), LowStrict, HighStrict);

        internal void AppendHash(ref AtmosStateHasher hash)
        {
            hash.Add(Gas);
            hash.Add(LowMolarityBound);
            hash.Add(HighMolarityBound);
            hash.Add(LowMolaritySpeed);
            hash.Add(HighMolaritySpeed);
            hash.Add(LowStrict);
            hash.Add(HighStrict);
        }

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