using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Tests;

public class GasReactionTests
{
    [Test]
    public void SolverGasMappings_FollowReactionChangesAndGasRegistryReordering()
    {
        var first = new GasProperties { Name = "A", MolarHeatCapacityAtConstantVolume = 10f };
        var second = new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 10f };
        var third = new GasProperties { Name = "C", MolarHeatCapacityAtConstantVolume = 10f };
        var toSecond = new StandardGasReaction(
            new Dictionary<GasProperties, float> { [first] = 1f },
            new Dictionary<GasProperties, float> { [second] = 2f },
            0f,
            0.25f,
            0f,
            new Dictionary<GasProperties, float> { [first] = 1f });

        var toThird = new StandardGasReaction(
            new Dictionary<GasProperties, float> { [first] = 1f },
            new Dictionary<GasProperties, float> { [third] = 3f },
            0f,
            0.5f,
            0f,
            new Dictionary<GasProperties, float> { [first] = 2f });

        AtmosConfigSnapshot[] configurations =
        [
            new AtmosConfig
            {
                GasRegistry = [first, second, third],
                SolverConfigurations = [new GasReactionConfig(standardReactions: [toSecond])]
            }.CreateSnapshot(),
            new AtmosConfig
            {
                GasRegistry = [first, second, third],
                SolverConfigurations = [new GasReactionConfig(standardReactions: [toThird])]
            }.CreateSnapshot(),
            new AtmosConfig
            {
                GasRegistry = [third, first, second],
                SolverConfigurations = [new GasReactionConfig(standardReactions: [toThird, toSecond])]
            }.CreateSnapshot(),
            new AtmosConfig
            {
                GasRegistry = [third, first, second],
                SolverConfigurations = [new GasReactionConfig(standardReactions: [toSecond])]
            }.CreateSnapshot()
        ];

        var tickConfig = new AtmosSolverConfigSnapshot();
        var solver = new ReactionSolver();
        foreach (var config in configurations)
        {
            tickConfig.Capture(config);
            // Repeat each configuration to exercise both creation and reuse of its gas attachments.
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var chunk = new AtmosChunk(1, 1, 1);
                chunk.VoxelRoomMap[0] = 1;
                chunk.WakeRoom(1);
                int sourceId = config.GasRegistry.GasIdToIndex("A");
                chunk.InjectGasToVoxel(0, sourceId, 10f, 300f, 10f, 1f);
                solver.Solve(new AtmosSolverExecutionContext(null!, [chunk], tickConfig, iteration + 1));

                var reactions = GasReactionConfig.Get(config);
                float[] expectedProducts = new float[config.GasPropertyCount];
                if (reactions.StandardReactions.Contains(toSecond))
                    expectedProducts[config.GasRegistry.GasIdToIndex("B")] = 2f * (0.25f * 10f * AtmosSolverConstants.FixedTimeStep);

                if (reactions.StandardReactions.Contains(toThird))
                    expectedProducts[config.GasRegistry.GasIdToIndex("C")] = 3f * (0.5f * 100f * AtmosSolverConstants.FixedTimeStep);

                for (int gasId = 0; gasId < expectedProducts.Length; gasId++)
                {
                    if (gasId == sourceId)
                        continue;

                    var channel = chunk.ActiveGases.Take(chunk.ActiveGasCount).SingleOrDefault(gas => gas.GasId == gasId);
                    Assert.That(channel.Moles?[0] ?? 0f, Is.EqualTo(expectedProducts[gasId]));
                }

                chunk.Release();
            }
        }
    }

    [Test]
    public void FeedbackReduction_UsesVoxelCountInsteadOfPooledArrayCapacity()
    {
        var config = new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "A" }],
            SolverConfigurations =
            [
                new GasReactionConfig(
                    standardReactions:
                    [
                        new StandardGasReaction(
                            new Dictionary<GasProperties, float>(),
                            new Dictionary<GasProperties, float>(),
                            0f,
                            0f,
                            0f,
                            new Dictionary<GasProperties, float>())
                    ])
            ]
        }.CreateSnapshot();

        var chunk = new AtmosChunk(3, 1, 1);
        float[] feedback = [0f];
        var solver = new ReactionSolver();
        Assert.That(() => solver.ProcessChunk(chunk, 0.1f, config, feedback), Throws.Nothing);
        Assert.That(feedback, Is.EqualTo(new[] { 0f }));
    }

    [Test]
    public void RateFactors_HaveStableOrderAcrossMappingsAndWorkerScheduling()
    {
        GasProperties[] gases = Enumerable.Range(0, 8)
            .Select(index => new GasProperties { Name = $"Gas{index}" }).ToArray();

        float[] molarity = [0.77f, 1.3f, 0.91f, 2.7f, 1.11f, 0.49f, 1.03f, 3.01f];
        KeyValuePair<GasProperties, float>[] forward = gases.Select((gas, index) => KeyValuePair.Create(gas, 0.1f + index * 0.13f))
            .ToArray();

        LinearGasReaction.LinearSpeedFactor[] factors = gases.Select((gas, index) => new LinearGasReaction.LinearSpeedFactor(
            gas,
            0f,
            10f,
            0.9f + index * 0.07f,
            1.01f + index * 0.08f,
            false,
            false)).ToArray();

        factors = [.. factors, new LinearGasReaction.LinearSpeedFactor(gases[0], 0f, 5f, 0.8f, 1.2f, false, false)];

        var first = new AtmosConfig
        {
            GasRegistry = [.. gases],
            SolverConfigurations =
            [
                new GasReactionConfig(
                    [
                        new LinearGasReaction(
                            new Dictionary<GasProperties, float>(),
                            new Dictionary<GasProperties, float>(),
                            0f,
                            200f,
                            500f,
                            0.3f,
                            0.9f,
                            false,
                            false,
                            factors.ToHashSet())
                    ],
                    [
                        new StandardGasReaction(
                            new Dictionary<GasProperties, float>(),
                            new Dictionary<GasProperties, float>(),
                            0f,
                            1.3f,
                            5f,
                            forward.ToDictionary())
                    ])
            ],
        }.CreateSnapshot();

        var second = new AtmosConfig
        {
            GasRegistry = [.. gases.Reverse()],
            SolverConfigurations =
            [
                new GasReactionConfig(
                    [
                        new LinearGasReaction(
                            new Dictionary<GasProperties, float>(),
                            new Dictionary<GasProperties, float>(),
                            0f,
                            200f,
                            500f,
                            0.3f,
                            0.9f,
                            false,
                            false,
                            factors.Reverse().ToHashSet())
                    ],
                    [
                        new StandardGasReaction(
                            new Dictionary<GasProperties, float>(),
                            new Dictionary<GasProperties, float>(),
                            0f,
                            1.3f,
                            5f,
                            forward.Reverse().ToDictionary())
                    ])
            ]
        }.CreateSnapshot();

        Dictionary<GasProperties, float> gasMolarities =
            gases.Select((gas, index) => KeyValuePair.Create(gas, molarity[index])).ToDictionary();

        var definitions = GasReactionConfig.Get(first);
        float[] expected =
        [
            definitions.LinearReactions[0].GetReactionSpeed(gasMolarities, 320f),
            definitions.StandardReactions[0].GetReactionSpeed(gasMolarities, 320f)
        ];

        Parallel.For(
            0,
            100,
            _ =>
            {
                float temperature = 320f;
                float[] feedback = new float[2];
                new ReactionSolver().ProcessVoxel(1f, molarity.Reverse().ToArray(), ref temperature, feedback, second, gases.Length);
                Assert.That(
                    feedback.Select(BitConverter.SingleToInt32Bits),
                    Is.EqualTo(expected.Select(BitConverter.SingleToInt32Bits)));
            });
    }

    /// <summary>
    ///     Create a random reaction and gas set.
    ///     Fill a chunk with random gas.
    ///     Run reaction over time.
    /// </summary>
    [Test]
    public void RandomReactionMatrix()
    {
        Random random = new(42);
        //create random gases
        List<GasProperties> gases = [];
        for (int i = 0; i < 16; i++)
        {
            float bp = 10 + random.NextSingle() * 100;
            gases.Add(
                new GasProperties
                {
                    BoilingPoint = bp,
                    MolarEnthalpyOfVaporization = random.NextSingle(),
                    Name = ((char)('a' + i)).ToString(),
                    MolarHeatCapacityAtConstantVolume = random.NextSingle() * 10000
                });
        }

        //create random reactions.
        GasProperties[] gasArray = gases.ToArray();
        List<StandardGasReaction> standardReactions = [];
        for (int i = 0; i < 16 * 2; i++)
        {
            Dictionary<GasProperties, float> input = [];
            int inputCounts = random.Next(3) + 2;
            while (inputCounts > 0)
            {
                if (input.TryAdd(random.GetItems(gasArray, 1)[0], random.NextSingle()))
                    inputCounts--;
            }

            Dictionary<GasProperties, float> output = [];
            int outputCounts = random.Next(1) + 1;

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

            standardReactions.Add(
                new StandardGasReaction(input, output, energyBalance, arrheniusFactor, activationEnergy, speedFactors));
        }

        //setup simulation
        var config = new AtmosConfig
        {
            GasRegistry = [.. gases],
            SolverConfigurations = [new GasReactionConfig(standardReactions: standardReactions)]
        };

        var snapshotConfig = new AtmosSolverConfigSnapshot();
        snapshotConfig.Capture(config);

        var solver = new ReactionSolver();

        var chunk = new AtmosChunk();
        //setup voxel with random shit.
        for (ushort i = 0; i < chunk.VoxelCount && i < chunk.MaxActiveRooms; i++)
        {
            chunk.VoxelRoomMap[i] = i;
            chunk.WakeRoom(i);
            for (int j = 0; j < gases.Count; j++)
            {
                chunk.InjectGasToVoxel(i, j, Math.Max(0, random.NextSingle() * 10 - 3), random.Next(500), 1, 1);
            }
        }

        //run reactions. wheeeeee
        float[] totalReactions = new float[standardReactions.Count];
        for (float i = 0; i < 10; i += 0.125f)
            solver.ProcessChunk(chunk, 0.125f, snapshotConfig, totalReactions);


        Assert.That(totalReactions.All(e => !float.IsNaN(e)));
        float totalReactionSum = totalReactions.Sum();

        //sum reaction amounts
        foreach (var gc in chunk.ActiveGases)
        {
            float[] x = gc.Moles.Where(e => float.IsNaN(e)).ToArray();
            Assert.That(x.Length == 0);
        }

        Assert.That(totalReactionSum > 0);
    }

    [Test]
    public void MixingWater()
    {
        var hydrogen = new GasProperties
        {
            BoilingPoint = 20.271f,
            MolarEnthalpyOfVaporization = 0.904f,
            Name = "Hydrogen",
            MolarHeatCapacityAtConstantVolume = 14303.571f
        };

        var oxygen = new GasProperties
        {
            BoilingPoint = 90.188f,
            MolarEnthalpyOfVaporization = 6.82f,
            Name = "Oxygen",
            MolarHeatCapacityAtConstantVolume = 918.12f
        };

        var water = new GasProperties
        {
            BoilingPoint = 373.13f,
            MolarEnthalpyOfVaporization = 40.65f,
            Name = "Water",
            MolarHeatCapacityAtConstantVolume = 36500f
        };

        var waterSynthesis = new StandardGasReaction(
            new Dictionary<GasProperties, float>
                { { hydrogen, 2 }, { oxygen, 1 } },
            new Dictionary<GasProperties, float>
            {
                { water, 2 }
            },
            285.8f,
            1.8e13f,
            146.4f,
            new Dictionary<GasProperties, float>
            {
                { hydrogen, 1 },
                { oxygen, 0.5f }
            });

        var config = new AtmosConfig
        {
            GasRegistry = [hydrogen, oxygen, water],
            SolverConfigurations = [new GasReactionConfig(standardReactions: [waterSynthesis])]
        };

        var solver = new ReactionSolver();

        var chunk = new AtmosChunk();
        for (ushort i = 0; i < 16 * 16 * 16 && i < chunk.MaxActiveRooms; i++)
        {
            chunk.VoxelRoomMap[i] = i;
            chunk.WakeRoom(i);
            chunk.InjectGasToVoxel(i, 0, 0.001f, 1, 1, 1);
            chunk.InjectGasToVoxel(i, 1, 0.002f, 1, 1, 1);
            Assert.That(chunk.ActiveGasCount == 2);
        }

        float[] feedback = [0];
        for (int r = 0; r < 100; r++)
        {
            solver.ProcessChunk(chunk, 1, config, feedback);
            for (ushort i = 0; i < 16 * 16 * 16 && i < chunk.MaxActiveRooms; i++)
            {
                chunk.InjectGasToVoxel(i, 0, 0.000005f, 1, 1, 1);
                chunk.InjectGasToVoxel(i, 1, 0.000002f, 1, 1, 1);
            }
        }

        Assert.That(chunk.ActiveGasCount == 3);
        Assert.That(feedback[0] > 0);
    }
}