using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Solvers;
using Numos.Units.Generated;

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
        var sharedData = new SolverDataStorage();
        foreach (var config in configurations)
        {
            tickConfig.Capture(config);
            // Repeat each configuration to exercise both creation and reuse of its gas attachments.
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var chunk = new AtmosChunk(1, 1, 1);
                chunk.VoxelRoomMap[0] = 1;
                chunk.Wake();
                int sourceId = config.GasRegistry.GasIdToIndex("A");
                chunk.InjectGasToVoxel(0, sourceId, 10f, 300f, 10f, 1f);
                solver.Solve(new AtmosSolverExecutionContext(null!, [chunk], tickConfig, iteration + 1, sharedData));

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
        for (ushort i = 0; i < chunk.VoxelCount && i < 64; i++)
        {
            chunk.VoxelRoomMap[i] = i;
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

        // Golden baseline captured from the current implementation: pins the exact numeric outcome of
        // this deterministic random scenario (fixed seed, sequential per-voxel writeback) so a change to
        // the material-limiter or the reaction-application math that alters results -- not just one that
        // introduces NaNs -- shows up here, even if the new result still "looks" plausible.
        Assert.That(totalReactionSum, Is.EqualTo(54699.562f));

        float[] expectedGasSums =
        [
            214.18805f, 165.98767f, 175.9431f, 342.46832f, 153.91702f, 144.54675f, 3798.6301f, 1537.6663f,
            727.5402f, 1548.2734f, 547.46625f, 175.2266f, 164.27475f, 1126.8424f, 210.39911f, 173.7023f
        ];
        foreach (var gc in chunk.ActiveGases.Take(chunk.ActiveGasCount).OrderBy(g => g.GasId))
            Assert.That(gc.Moles.Sum(), Is.EqualTo(expectedGasSums[gc.GasId]));

        Assert.That(chunk.Temperature[0], Is.EqualTo(108564.51f));
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
            UnitConversions.FromKilojoulePerMole(146.4f),
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
        for (ushort i = 0; i < 64; i++)
        {
            chunk.VoxelRoomMap[i] = i;
            // Seed an ignition-temperature spark: the reaction has a real activation energy (146.4 kJ/mol),
            // so at room temperature it won't proceed at any meaningful rate.
            chunk.InjectGasToVoxel(i, 0, 0.001f, 900, 1, 1);
            chunk.InjectGasToVoxel(i, 1, 0.002f, 900, 1, 1);
            Assert.That(chunk.ActiveGasCount == 2);
        }

        float[] feedback = [0];
        for (int r = 0; r < 100; r++)
        {
            solver.ProcessChunk(chunk, 1, config, feedback);
            for (ushort i = 0; i < 64; i++)
            {
                chunk.InjectGasToVoxel(i, 0, 0.000005f, 293, 1, 1);
                chunk.InjectGasToVoxel(i, 1, 0.000002f, 293, 1, 1);
            }
        }

        Assert.That(chunk.ActiveGasCount == 3);
        Assert.That(feedback[0] > 0);
        // Regression guard: a broken energy<->temperature round trip previously sent this to ~7.9e23 K
        // and then NaN the moment the reaction fired, instead of settling on a plausible flame temperature.
        Assert.That(chunk.Temperature[0], Is.InRange(1f, 5000f));
    }

    [Test]
    public void MaterialLimiter_DoesNotThrottleAReactionThatOnlyProducesTheScarceGas()
    {
        var a = new GasProperties { Name = "A", MolarHeatCapacityAtConstantVolume = 10f };
        var b = new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 10f };
        var c = new GasProperties { Name = "C", MolarHeatCapacityAtConstantVolume = 10f };
        var d = new GasProperties { Name = "D", MolarHeatCapacityAtConstantVolume = 10f };

        // Consumes the scarce gas A (alongside abundant B).
        var consumesA = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { a, 1 }, { b, 1 } },
            new Dictionary<GasProperties, float> { { d, 1 } },
            0f,
            10f,
            0f,
            new Dictionary<GasProperties, float> { { a, 1 }, { b, 1 } });

        // Only produces A (consumes abundant C). Must not be throttled just because A is scarce elsewhere.
        var producesA = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { c, 1 } },
            new Dictionary<GasProperties, float> { { a, 1 } },
            0f,
            10f,
            0f,
            new Dictionary<GasProperties, float> { { c, 1 } });

        var config = new AtmosConfig
        {
            GasRegistry = [a, b, c, d],
            SolverConfigurations = [new GasReactionConfig(standardReactions: [consumesA, producesA])]
        };

        var solver = new ReactionSolver();
        var chunk = new AtmosChunk();
        chunk.VoxelRoomMap[0] = 0;
        chunk.InjectGasToVoxel(0, 0, 0.001f, 293, 1, 1); // A: scarce
        chunk.InjectGasToVoxel(0, 1, 1000f, 293, 1, 1); // B: abundant
        chunk.InjectGasToVoxel(0, 2, 1000f, 293, 1, 1); // C: abundant

        float[] feedback = [0, 0];
        solver.ProcessChunk(chunk, 1, config, feedback);

        // A's scarcity must only throttle the reaction that consumes it (consumesA), never producesA,
        // which only ever adds A to the mixture and so can't be responsible for depleting it.
        Assert.That(feedback[1], Is.GreaterThan(10f));
    }

    [Test]
    public void MaterialLimiter_ConvergesAcrossIterationsForIndependentlyScarceGases()
    {
        var a = new GasProperties { Name = "A", MolarHeatCapacityAtConstantVolume = 10f };
        var b = new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 10f };
        var d = new GasProperties { Name = "D", MolarHeatCapacityAtConstantVolume = 10f };
        var e = new GasProperties { Name = "E", MolarHeatCapacityAtConstantVolume = 10f };

        // Would consume 20 mol of A (only 5 available) and produce D. Unrelated to B/R2.
        var consumesA = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { a, 1f } },
            new Dictionary<GasProperties, float> { { d, 1f } },
            0f, 20f, 0f,
            new Dictionary<GasProperties, float>());

        // Would consume 8 mol of B (only 2 available) and produce E. Unrelated to A/R1.
        var consumesB = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { b, 1f } },
            new Dictionary<GasProperties, float> { { e, 1f } },
            0f, 8f, 0f,
            new Dictionary<GasProperties, float>());

        var config = new AtmosConfig
        {
            GasRegistry = [a, b, d, e],
            SolverConfigurations = [new GasReactionConfig(standardReactions: [consumesA, consumesB])]
        }.CreateSnapshot();

        // A's overdraw (5 - 20 = -15) is worse than B's (2 - 8 = -6), so the limiter clamps A first,
        // leaving B still overdrawn -- it only resolves B on the following iteration, once A's fix no
        // longer masks it. This scenario requires that second pass to reach the values asserted below;
        // a limiter that only ever ran once would leave B unclamped.
        float[] mixture = [5f, 2f, 0f, 0f];
        float temperature = 300f;
        float[] feedback = [0f, 0f];

        var solver = new ReactionSolver();
        solver.ProcessVoxel(1f, mixture, ref temperature, feedback, config, 4);

        Assert.Multiple(() =>
        {
            // Every value here is an exact power-of-two fraction (0.25) of exact integers, so this is
            // bit-exact, not approximate.
            Assert.That(mixture[0], Is.EqualTo(0f), "A should be exactly exhausted, not overdrawn");
            Assert.That(mixture[1], Is.EqualTo(0f), "B should be exactly exhausted, not overdrawn");
            Assert.That(mixture[2], Is.EqualTo(5f), "D should reflect R1's clamped speed (20 -> 5)");
            Assert.That(mixture[3], Is.EqualTo(2f), "E should reflect R2's clamped speed (8 -> 2)");
            Assert.That(feedback[0], Is.EqualTo(5f), "R1 should be scaled from 20 to 5 by A's scarcity");
            Assert.That(feedback[1], Is.EqualTo(2f), "R2 should be scaled from 8 to 2 by B's scarcity");
            // No reaction here carries an energy balance, and total heat capacity is unchanged (same
            // total moles, uniform per-gas heat capacity), so temperature must be exactly conserved.
            Assert.That(temperature, Is.EqualTo(300f));
        });
    }

    [Test]
    public void MaterialLimiter_AccumulatesGrossConsumptionInDoublePrecision()
    {
        // A single reaction wants to consume far more of A than is available, so the limiter must clamp
        // it. Three more reactions each nibble an additional 0.03 mol of A. At this magnitude (~1e6),
        // float32's ULP is 0.0625, so summing three 0.03 terms sequentially in float32 rounds every one
        // of them away to nothing (each individual addition is below half a ULP) -- a naive float-only
        // rewrite of the consumption accumulator would see exactly 1,000,000 consumed against exactly
        // 1,000,000 available, conclude nothing is overdrawn, and skip clamping the big reaction entirely.
        // Accumulating in double (the documented contract) correctly sees 1,000,000.09-ish consumed
        // against 1,000,000 available, and clamps it.
        var a = new GasProperties { Name = "A", MolarHeatCapacityAtConstantVolume = 10f };

        var bigConsumer = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { a, 1f } },
            new Dictionary<GasProperties, float>(),
            0f, 1_000_000f, 0f,
            new Dictionary<GasProperties, float>());

        StandardGasReaction MakeTinyConsumer() =>
            new(
                new Dictionary<GasProperties, float> { { a, 0.03f } },
                new Dictionary<GasProperties, float>(),
                0f, 1f, 0f,
                new Dictionary<GasProperties, float>());

        var config = new AtmosConfig
        {
            GasRegistry = [a],
            SolverConfigurations =
                [new GasReactionConfig(standardReactions: [bigConsumer, MakeTinyConsumer(), MakeTinyConsumer(), MakeTinyConsumer()])]
        }.CreateSnapshot();

        float[] mixture = [1_000_000f];
        float temperature = 300f;
        float[] feedback = [0f, 0f, 0f, 0f];

        var solver = new ReactionSolver();
        solver.ProcessVoxel(1f, mixture, ref temperature, feedback, config, 1);

        Assert.Multiple(() =>
        {
            // The decisive check: the big reaction must actually get throttled. A float-only accumulator
            // would leave it at exactly 1,000,000 (unclamped) because it can't see the three 0.03 terms.
            Assert.That(feedback[0], Is.LessThan(1_000_000f).And.GreaterThan(0f));
            // Secondary sanity check: A shouldn't be driven meaningfully negative by the clamp.
            Assert.That(mixture[0], Is.GreaterThanOrEqualTo(-0.1f).And.LessThanOrEqualTo(0.1f));
        });
    }
}