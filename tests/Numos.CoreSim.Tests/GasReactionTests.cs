using Numos.CoreSim.GasReactions;

namespace Numos.CoreSim.Tests;

public class GasReactionTests
{
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
        
        var chunk = new AtmosChunk(1, 1, 1);
        chunk.IsAwake = true;
        chunk.InjectGasToVoxel(0,0,100,5000);
        chunk.InjectGasToVoxel(0,1,100,5000);
        Assert.That(chunk.ActiveGasCount == 2);
        solver.ProcessChunk(chunk,50);
        Assert.That(chunk.ActiveGasCount == 3);
    }
}