using System.Text;

namespace Numos.API;

/// <summary>
///     A small SplitMix64 random generator whose start state comes from the world seed, the tick, the stage, and a
///     stream number.
/// </summary>
/// <remarks>
///     <para>
///         Get one from <see cref="AtmosWorldSolverContext.Random" /> inside a stage. The generator stores nothing
///         outside the struct, so a replay or a restored world draws the same numbers without a checkpoint entry. The
///         same seed, tick, stage name, and stream always give the same sequence. A stage that draws for many voxels
///         or decisions in one tick gives each one its own stream.
///     </para>
///     <para>
///         Every member uses integer arithmetic, and the float results are exact conversions of integers, so the
///         sequence is the same on every platform and runtime.
///     </para>
///     <para>
///         The struct is mutable: each draw advances it. A copy continues from the same position as the original, so
///         pass it by <see langword="ref" /> to a helper that should advance the caller's generator.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     for (int voxel = 0; voxel &lt; temperature.Length; voxel++)
///     {
///         AtmosRandom random = context.Random(stream: (ulong)voxel);
///         if (temperature[voxel] > IgnitionTemperature &amp;&amp; random.NextBoolean(SpreadChance))
///             spread.Add(voxel);
///     }
///     </code>
/// </example>
public struct AtmosRandom
{
    // The SplitMix64 increment, 2^64 divided by the golden ratio.
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    // Keeps a zero seed from starting at Mix(0), which is zero.
    private const ulong SeedOffset = 0x6A09E667F3BCC909UL;

    private ulong _state;

    /// <summary>
    ///     Creates the generator that <see cref="AtmosWorldSolverContext.Random" /> returns for these inputs.
    /// </summary>
    /// <param name="seed">The world seed, <see cref="Numos.CoreSim.AtmosConfig.RandomSeed" />.</param>
    /// <param name="tick">The one-based tick, <see cref="AtmosWorldSolverContext.TickCount" />.</param>
    /// <param name="stageName">The name of the stage that draws. Numos hashes its UTF-8 bytes with FNV-1a.</param>
    /// <param name="stream">The stream number that tells draws in one stage and tick apart.</param>
    /// <remarks>
    ///     Use this to reproduce a stage's draws outside the world, for example in a unit test of the stage's logic.
    ///     Inputs that differ only in the seed, only in the tick, or only in the stream never give the same start
    ///     state.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="stageName" /> is <see langword="null" />.</exception>
    public AtmosRandom(ulong seed, long tick, string stageName, ulong stream)
    {
        ArgumentNullException.ThrowIfNull(stageName);
        _state = StartState(seed, tick, HashStageName(stageName), stream);
    }

    private AtmosRandom(ulong state)
    {
        _state = state;
    }

    /// <summary>
    ///     Builds a generator from a stage name hash that the caller already computed with <see cref="HashStageName" />.
    /// </summary>
    internal static AtmosRandom Create(ulong seed, long tick, ulong stageNameHash, ulong stream)
    {
        return new AtmosRandom(StartState(seed, tick, stageNameHash, stream));
    }

    /// <summary>
    ///     Hashes a stage name with FNV-1a 64 over its UTF-8 bytes.
    /// </summary>
    /// <remarks>
    ///     The hash depends only on the characters, never on the process, so it is stable across runs and platforms.
    /// </remarks>
    internal static ulong HashStageName(string name)
    {
        ulong hash = 14695981039346656037UL;
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in name.EnumerateRunes())
        {
            int length = rune.EncodeToUtf8(utf8);
            for (int index = 0; index < length; index++)
                hash = unchecked((hash ^ utf8[index]) * 1099511628211UL);
        }

        return hash;
    }

    /// <summary>
    ///     Returns the next 64 random bits.
    /// </summary>
    public ulong NextUInt64()
    {
        _state = unchecked(_state + Gamma);
        return Mix(_state);
    }

    /// <summary>
    ///     Returns the next 32 random bits.
    /// </summary>
    /// <returns>The high half of <see cref="NextUInt64" />, which mixes better than the low half.</returns>
    public uint NextUInt32()
    {
        return (uint)(NextUInt64() >> 32);
    }

    /// <summary>
    ///     Returns a uniform integer in [0, <paramref name="exclusiveMax" />).
    /// </summary>
    /// <param name="exclusiveMax">The bound. It must be positive.</param>
    /// <returns>An integer at least 0 and below <paramref name="exclusiveMax" />.</returns>
    /// <remarks>
    ///     Lemire's multiply-and-reject method keeps the result unbiased. It draws again only when the first draw
    ///     falls in the biased remainder, which happens with probability below <c>exclusiveMax / 2^32</c>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveMax" /> is zero or negative.</exception>
    public int NextInt32(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
        uint bound = (uint)exclusiveMax;
        ulong product = (ulong)NextUInt32() * bound;
        uint low = (uint)product;
        if (low < bound)
        {
            uint threshold = unchecked(0u - bound) % bound;
            while (low < threshold)
            {
                product = (ulong)NextUInt32() * bound;
                low = (uint)product;
            }
        }

        return (int)(product >> 32);
    }

    /// <summary>
    ///     Returns a uniform float in [0, 1).
    /// </summary>
    /// <returns>A multiple of <c>2^-24</c>, so every result converts exactly and 1 never appears.</returns>
    public float NextSingle()
    {
        return (NextUInt64() >> 40) * (1f / (1 << 24));
    }

    /// <summary>
    ///     Returns a uniform double in [0, 1).
    /// </summary>
    /// <returns>A multiple of <c>2^-53</c>, so every result converts exactly and 1 never appears.</returns>
    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>
    ///     Returns <see langword="true" /> with the given probability.
    /// </summary>
    /// <param name="probability">
    ///     The chance of <see langword="true" />. Zero, negative values, and NaN always give
    ///     <see langword="false" />. One and above always give <see langword="true" />.
    /// </param>
    /// <returns>Whether <see cref="NextSingle" /> fell below <paramref name="probability" />.</returns>
    /// <remarks>The call draws once, also when the result is certain, so later draws do not depend on the value.</remarks>
    public bool NextBoolean(float probability)
    {
        return NextSingle() < probability;
    }

    // Each input passes through the bijective SplitMix64 finalizer, so changing any one input changes the state. Two
    // stage names can still collide in FNV-1a.
    private static ulong StartState(ulong seed, long tick, ulong stageNameHash, ulong stream)
    {
        ulong state = Mix(seed ^ SeedOffset);
        state = Mix(state ^ unchecked((ulong)tick));
        state = Mix(state ^ stageNameHash);
        return Mix(state ^ stream);
    }

    // The SplitMix64 output function (Stafford's "Mix13" variant). It is a bijection on 64-bit values.
    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }
}
