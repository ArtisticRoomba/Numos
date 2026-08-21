using System.Collections.Immutable;
using Numos.CoreSim.GasReactions;

namespace Numos.CoreSim.Tests;

public class GasReactionTests
{
    /// <summary>
    /// Create a random reaction and gas set.
    /// Fill a chunk with random gas.
    /// Run reaction over time.
    /// </summary>
    [Test]
    public void RandomReactionMatrix()
    {
        Random random = new(42);
        //create random gases
        List<GasProperties> gases = [];
        for (var i = 0; i < 16; i++)
        {
            var bp = 10 + random.NextSingle() * 100;
            gases.Add(new()
            {
                BoilingPoint = bp,
                CondensationPoint = bp * random.NextSingle() - 1,
                LatentHeatOfVaporization = random.NextSingle(),
                Name = ((char)('a' + i)).ToString(),
                SpecificHeatCapacity = random.NextSingle() * 10000,
            });
        }

        //create random reactions.
        var gasArray = gases.ToArray();
        List<StandardGasReaction> standardReactions = [];
        for (var i = 0; i < 16 * 2; i++)
        {
            Dictionary<GasProperties, float> input = [];
            var inputCounts = random.Next(3) + 2;
            while (inputCounts > 0)
            {
                if (input.TryAdd(random.GetItems(gasArray, 1)[0], random.NextSingle()))
                    inputCounts--;
            }

            Dictionary<GasProperties, float> output = [];
            var outputCounts = random.Next(1) + 1;

            while (outputCounts > 0)
            {
                if (output.TryAdd(random.GetItems(gasArray, 1)[0], random.NextSingle()))
                    outputCounts--;
            }

            float energyBalance = random.Next(200) - 100;
            float arrheniusFactor = 1;
            float activationEnergy = random.Next(10);
            Dictionary<GasProperties, float> speedFactors = [];

            foreach (var gas in input.Keys)
            {
                speedFactors.Add(gas, random.NextSingle() - 0.5f + random.Next(5) - 2);
            }

            standardReactions.Add(new(input, output, energyBalance, arrheniusFactor, activationEnergy, speedFactors));
        }

        //setup simulation
        var config = new AtmosConfig()
        {
            GasRegistry = gases,
            StandardReactionRegistry = standardReactions.ToImmutableArray(),
            MaxDeltaForReactionSteps = 0.25f,
        };

        var solver = new ReactionSolver();
        solver.SetAtmosConfig(config);

        var chunk = new AtmosChunk(16, 16, 16);
//setup voxel with random shit.
        for (ushort i = 0; i < chunk.VoxelCount && i < chunk.MaxActiveRooms; i++)
        {
            chunk.VoxelRoomMap[i] = i;
            chunk.WakeRoom(i);
            for (int j = 0; j < gases.Count; j++)
            {
                chunk.InjectGasToVoxel(i, j, Math.Max(0, random.NextSingle() * 10 - 3), random.Next(500));
            }
        }

        //run reactions. wheeeeee
        var totalReactions = new float[standardReactions.Count];
        solver.ProcessChunk(chunk, 10, totalReactions);
        //sum reaction amounts
        foreach (var gc in chunk.ActiveGases)
        {
            var x = gc.Moles.Where(e => !float.IsNormal(e) && e != 0).ToArray();
            Assert.That(x.Length==0);
        }

        Assert.That(totalReactions.All(e => float.IsNormal(e) || e == 0));
        var totalReactionSum = totalReactions.Sum();
        Assert.That(totalReactionSum > 0);
    }

    [Test]
    public void MixingWater()
    {
        var hydrogen = new GasProperties()
        {
            BoilingPoint = 20.271f,
            CondensationPoint = 13.99f,
            LatentHeatOfVaporization = 0.904f,
            Name = "Hydrogen",
            SpecificHeatCapacity = 14303.571f,
        };

        var oxygen = new GasProperties()
        {
            BoilingPoint = 90.188f,
            CondensationPoint = 54.36f,
            LatentHeatOfVaporization = 6.82f,
            Name = "Oxygen",
            SpecificHeatCapacity = 918.12f,
        };

        var water = new GasProperties()
        {
            BoilingPoint = 373.13f,
            CondensationPoint = 273.15f,
            LatentHeatOfVaporization = 40.65f,
            Name = "Oxygen",
            SpecificHeatCapacity = 36500f,
        };

        var waterSynthesis = new StandardGasReaction(new Dictionary<GasProperties, float>()
                { { hydrogen, 2 }, { oxygen, 1 } }, new Dictionary<GasProperties, float>()
            {
                { water, 2 }
            }, 285.8f,
            1.8f * MathF.Pow(10, 13),
            146.4f,
            new Dictionary<GasProperties, float>()
            {
                { hydrogen, 1 },
                { oxygen, 0.5f }
            }
        );

        var config = new AtmosConfig()
        {
            GasRegistry = [hydrogen, oxygen, water],
            StandardReactionRegistry = [waterSynthesis]
        };

        var solver = new ReactionSolver();
        solver.SetAtmosConfig(config);

        var chunk = new AtmosChunk(16, 16, 16);
        for (ushort i = 0; i < 16 * 16 * 16 && i < chunk.MaxActiveRooms; i++)
        {
            chunk.VoxelRoomMap[i] = i;
            chunk.WakeRoom(i);
            chunk.InjectGasToVoxel(i, 0, 0.001f, 1);
            chunk.InjectGasToVoxel(i, 1, 0.002f, 1);
            Assert.That(chunk.ActiveGasCount == 2);
        }

        float[] feedback = [0];
        for (var r = 0; r < 100; r++)
        {
            solver.ProcessChunk(chunk, 1, feedback);
            for (ushort i = 0; i < 16 * 16 * 16 && i < chunk.MaxActiveRooms; i++)
            {
                chunk.InjectGasToVoxel(i, 0, 0.000005f, 1);
                chunk.InjectGasToVoxel(i, 1, 0.000002f, 1);
            }
        }

        Assert.That(chunk.ActiveGasCount == 3);
        Assert.That(feedback[0] > 0);
    }
}