using Numos.CoreSim.Replay;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct LinearGasReaction
{
    /// <summary>
    ///     Multiplies a linear reaction's rate by a piecewise-linear function of one gas's amount.
    /// </summary>
    /// <param name="Gas">The gas whose amount drives the factor.</param>
    /// <param name="LowMolarityBound">Gas amount at the line's lower point.</param>
    /// <param name="HighMolarityBound">Gas amount at the line's upper point.</param>
    /// <param name="LowMolaritySpeed">Factor at <paramref name="LowMolarityBound" />.</param>
    /// <param name="HighMolaritySpeed">Factor at <paramref name="HighMolarityBound" />.</param>
    /// <param name="LowStrict">Whether the factor is zero below <paramref name="LowMolarityBound" /> instead of extrapolated.</param>
    /// <param name="HighStrict">Whether the factor is zero above <paramref name="HighMolarityBound" /> instead of extrapolated.</param>
    /// <remarks>
    ///     The built-in reaction solver evaluates this against the voxel's amount of <paramref name="Gas" /> in moles,
    ///     despite the "molarity" naming, and skips the reaction for that voxel when the factor is zero, negative, or
    ///     not a normal float.
    /// </remarks>
    public readonly record struct LinearSpeedFactor(
        GasProperties Gas,
        float LowMolarityBound,
        float HighMolarityBound,
        float LowMolaritySpeed,
        float HighMolaritySpeed,
        bool LowStrict,
        bool HighStrict)
    {
        /// <summary>
        ///     Gets the gas whose amount drives the factor.
        /// </summary>
        public GasProperties Gas { get; } = Gas;

        /// <summary>
        ///     Gets whether the factor is zero below the low bound instead of extrapolated.
        /// </summary>
        public bool LowStrict { get; } = LowStrict;

        /// <summary>
        ///     Gets whether the factor is zero above the high bound instead of extrapolated.
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

        /// <summary>
        ///     Evaluates the factor for a gas amount.
        /// </summary>
        /// <param name="molarity">Amount of <see cref="Gas" />, in the same units as the bounds.</param>
        /// <returns>The rate multiplier, or zero outside a strict bound.</returns>
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