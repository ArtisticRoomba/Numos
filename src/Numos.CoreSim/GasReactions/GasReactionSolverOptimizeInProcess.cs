using System.Runtime.CompilerServices;

namespace Numos.CoreSim;

/// <summary>
///     A shared container to solve gas reactions within a given mixture.
///     Holds the cache for all necessary calculations. needs to be notified of any change to atmos config.
/// </summary>
public class GasReactionSolverOptimizeInProcess
{
    private AtmosConfig _config = new();

    /// <summary>
    ///     All Linear gas reactions dumped into one big list of floats for fast lookup/processing.
    ///     structure is one line for the reaction body followed by each factor.
    ///     [0] [TempGraph]
    ///     [1 if used, 0 if unused] [MolarityGraph]
    ///     ... all gases as a factor
    ///     [Number of Factors] ... Next reaction
    /// </summary>
    private float[] linearReactionSpeedValues = [];

    /// <summary>
    ///     all linear reactions dumped into one master record regarding their change in moles and energy balance.
    ///     strcuture is:
    ///     [Gas 0] [Gas 1] [Gas n] [EnergyBalance] of Reaction 1
    ///     [Gas 0] [Gas 1] [Gas n] [EnergyBalance] of Reaction 2
    ///     all in succession for quick lookup.
    /// </summary>
    private float[] linearReactionStoichiometricMatrix = [];

    internal void UpdateConfig(AtmosConfig config)
    {
        _config = config;
        //update cached master matrices
        FillLinearMasterLookups(config);
    }

    private void FillLinearMasterLookups(AtmosConfig config)
    {
        var speedMatrix = new float[config.LinearReactionRegistry.Length * (config.GasRegistry.Count + 1) * 7];
        //build the master table for speed factors.

        Parallel.For(0, config.LinearReactionRegistry.Length, i =>
        {
            var index = i * (config.GasRegistry.Count + 1);
            var reaction = config.LinearReactionRegistry[i];
            speedMatrix[index] = 0;
            speedMatrix[index + 1] = reaction.LowTemperatureBound;
            speedMatrix[index + 2] = reaction.LowTempSpeed;
            speedMatrix[index + 3] = reaction.LowStrict ? 1f : 0f;
            speedMatrix[index + 4] = reaction.HighTemperatureBound;
            speedMatrix[index + 5] = reaction.HighTempSpeed;
            speedMatrix[index + 6] = reaction.HighStrict ? 1f : 0f;
            Parallel.ForEach(reaction.SpeedFactors, factor =>
            {
                var factorIndex = index + 7 + 7 * config.GasRegistry.IndexOf(factor.Gas);
                speedMatrix[factorIndex] = config.GasRegistry.IndexOf(factor.Gas);
                speedMatrix[factorIndex + 1] = factor.LowMolarityBound;
                speedMatrix[factorIndex + 2] = factor.LowMolaritySpeed;
                speedMatrix[factorIndex + 3] = factor.LowStrict ? 1f : 0f;
                speedMatrix[factorIndex + 4] = factor.HighMolarityBound;
                speedMatrix[factorIndex + 5] = factor.HighMolaritySpeed;
                speedMatrix[factorIndex + 6] = factor.HighStrict ? 1f : 0f;
            });
        });


        linearReactionSpeedValues = speedMatrix;
        var idx = 0;
        var reactionMatrix = new float[config.LinearReactionRegistry.Length * (config.GasRegistry.Count + 1)];
        //build the master table for reaction process.
        foreach (var reaction in config.LinearReactionRegistry)
        {
            foreach (var gas in config.GasRegistry)
            {
                var molBalance = 0f;
                if (reaction.Input.TryGetValue(gas, out var cost)) molBalance -= cost;

                if (reaction.Output.TryGetValue(gas, out var yield)) molBalance += yield;

                reactionMatrix[idx++] = molBalance;
            }

            reactionMatrix[idx++] = reaction.EnergyBalance;
        }

        linearReactionStoichiometricMatrix = reactionMatrix;
    }


    /// <summary>
    /// </summary>
    /// <param name="chunk"></param>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    internal void ProcessChunk(AtmosChunk chunk, float deltaTime)
    {
        //process each voxel in parallel
        var voxelCount = chunk.Depth * chunk.Width * chunk.Height;
        Parallel.For(0, voxelCount, voxelIndex => ProcessVoxel(chunk, deltaTime, voxelIndex));
    }


    internal void ProcessVoxel(AtmosChunk chunk, float deltaTime, int voxelIndex)
    {
        //calculate total molVolume

        var mixtureVector = new float[_config.GasRegistry.Count + 1];

        for (var i = 0; i < chunk.ActiveAirCount; i++)
            mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];

        //TODO Get the energy of the mixture. yes i know dirty, but what can you do.
        mixtureVector[^1] = 0;

        ProcessMixture(ref mixtureVector, chunk.Temperature[voxelIndex], deltaTime);

        //adjust moles from the mixture vector

        //adjust temperature of the voxel.

        //report reaction count as events.
    }

    internal void ProcessMixture(IDictionary<GasProperties, float> gasMixtureMoles, float deltaTime,
        ref float temperature, float volume = 1f)
    {
        var mixtureVector = new float[_config.GasRegistry.Count + 1];

        foreach (var content in gasMixtureMoles)
            mixtureVector[_config.GasRegistry.IndexOf(content.Key)] = content.Value / volume;

        //TODO Get the energy of the mixture. yes i know dirty, but what can you do.
        mixtureVector[^1] = 0;

        ProcessMixture(ref mixtureVector, temperature, deltaTime);

        //adjust temperature value

        //put back values from the vector into the dictionary.
        for (var i = 0; i < mixtureVector.Length - 1; i++)
        {
            if (mixtureVector[i] == 0)
                gasMixtureMoles.Remove(_config.GasRegistry[i]);
            gasMixtureMoles[_config.GasRegistry[i]] = mixtureVector[i];
        }
    }

    /// <summary>
    ///     General reaction processor function. Used in updates or direct api checks.
    /// </summary>
    /// <param name="mixture"></param>
    /// <param name="temperature"></param>
    /// <param name="deltaTime"></param>
    private void ProcessMixture(ref float[] mixture, float temperature, float deltaTime)
    {
        //calculate reaction speed vector and multiply with delta time to get change.


        CalculateLinearReactionSpeeds(mixture, temperature);


        //check which reactions occur aka have a speed greater than 0 -> for notification process.

        //TODO: create notifications about reactions if necessary

        //multiply with cached master matrix to get actual changes to voxel per gas id.


        //map each line back to the appropriate gas channel.
    }

    private float[] CalculateLinearReactionSpeeds(float[] mixture, float temperature)
    {
        var result = new float[_config.LinearReactionRegistry.Length];
        Parallel.For((long)0, result.Length, i =>
        {
            var topLineIdx = i * (_config.GasRegistry.Count + 1);
            var tempFactor = EvaluateLinearEntry(temperature, topLineIdx);
            if (linearReactionSpeedValues[topLineIdx] == 0)
            {
                result[i] = tempFactor;
                return;
            }

            var factors = new float[_config.GasRegistry.Count + 1];
            Array.Fill(factors, 1);
            factors[^1] = tempFactor;
            Parallel.For((long)1, _config.GasRegistry.Count + 1,
                j => { factors[j] = EvaluateLinearEntry(mixture[j], i * factors.Length + j); });

            result[i] = 1;
            foreach (var f in factors) result[i] *= f;
        });
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float EvaluateLinearEntry(float value, long indexOfLine)
    {
        if (linearReactionSpeedValues[indexOfLine] == 0)
            return 1;
        var lowBound = linearReactionSpeedValues[indexOfLine + 1];
        var lowBoundValue = linearReactionSpeedValues[indexOfLine + 2];
        var lowStrict = linearReactionSpeedValues[indexOfLine + 3] == 1f;
        var highBound = linearReactionSpeedValues[indexOfLine + 4];
        var highBoundValue = linearReactionSpeedValues[indexOfLine + 5];
        var highStrict = linearReactionSpeedValues[indexOfLine + 6] == 1f;
        var range = highBound - lowBound; //TODO: We can cache this but that extends line by 1.
        // Normalize value into [0, 1].
        var t = (value - lowBound) / range;
        // Clamp to boundary range.
        if (float.IsNaN(t)) return 0;
        if (t < 0f)
        {
            if (lowStrict)
                return 0;
        }
        else if (t > 1f)
        {
            if (highStrict)
                return 0;
        }

        return lowBoundValue + (highBoundValue - lowBoundValue) * t;
    }
}