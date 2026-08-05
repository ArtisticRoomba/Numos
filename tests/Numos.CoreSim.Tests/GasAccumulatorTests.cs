namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class GasAccumulatorTests
{
    [Test]
    public void AddGas_AccumulatesMolesTicksAndMoleWeightedTemperature()
    {
        var accumulator = new GasAccumulator { GasId = 4 };

        accumulator.AddGas(2f, 300f);
        accumulator.AddGas(1f, 600f);

        Assert.Multiple(() =>
        {
            Assert.That(accumulator.GasId, Is.EqualTo(4));
            Assert.That(accumulator.AccumulatedMoles, Is.EqualTo(3f));
            Assert.That(accumulator.OutputTemperature, Is.EqualTo(400f).Within(0.0001f));
            Assert.That(accumulator.TicksAlive, Is.EqualTo(2));
        });
    }

    [Test]
    public void EvaluateState_BelowWakeThresholdBeforeTimeout_Holds()
    {
        var accumulator = CreateAccumulatorWithTicks(19);

        var state = accumulator.EvaluateState(0.5f, 15f, 20);

        Assert.That(state, Is.EqualTo(AccumulatorState.Hold));
    }

    [Test]
    public void EvaluateState_BelowWakeThresholdAtTimeout_Diffuses()
    {
        var accumulator = CreateAccumulatorWithTicks(20);

        var state = accumulator.EvaluateState(5f, 15f, 20);

        Assert.That(state, Is.EqualTo(AccumulatorState.Diffuse));
    }

    [Test]
    public void EvaluateState_AboveWakeThresholdBeforeTimeout_Injects()
    {
        var accumulator = CreateAccumulatorWithTicks(1);

        var state = accumulator.EvaluateState(150f, 15f, 20);

        Assert.That(state, Is.EqualTo(AccumulatorState.Inject));
    }

    [Test]
    public void EvaluateState_AboveWakeThresholdAtTimeout_PrefersInjection()
    {
        var accumulator = CreateAccumulatorWithTicks(20);

        var state = accumulator.EvaluateState(150f, 15f, 20);

        Assert.That(state, Is.EqualTo(AccumulatorState.Inject));
    }

    [Test]
    public void Reset_ClearsAccumulatedStateAndPreservesGasIdentity()
    {
        var accumulator = new GasAccumulator { GasId = 8 };
        accumulator.AddGas(2f, 350f);

        accumulator.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(accumulator.GasId, Is.EqualTo(8));
            Assert.That(accumulator.AccumulatedMoles, Is.Zero);
            Assert.That(accumulator.OutputTemperature, Is.Zero);
            Assert.That(accumulator.TicksAlive, Is.Zero);
        });
    }

    private static GasAccumulator CreateAccumulatorWithTicks(int ticks)
    {
        var accumulator = new GasAccumulator { GasId = 1 };
        for (var tick = 0; tick < ticks; tick++)
            accumulator.AddGas(0.25f, 300f);

        return accumulator;
    }
}