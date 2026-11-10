namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class BuiltInStageScheduleTests
{
    [TestCase("euler-flow", 20, 1)]
    [TestCase("euler-flow", 960, 16)]
    [TestCase("euler-flow", 480, 8)]
    [TestCase("euler-flow", 100, 1)]
    [TestCase("advection", 20, 1)]
    [TestCase("advection", 960, 48)]
    [TestCase("derived-refresh", 960, 48)]
    [TestCase("derived-refresh", 20, 1)]
    [TestCase("vacuum-cleanup", 960, 48)]
    [TestCase("vacuum-cleanup", 20, 1)]
    [TestCase("explicit-gas-transport", 40, 2)]
    [TestCase("boundary-flow", 960, 48)]
    [TestCase("gas-reactions", 100_000, 5000)]
    [TestCase("thermodynamics", 20, 2)]
    [TestCase("explicit-thermal-transport", 960, 96)]
    [TestCase("thermal-boundary", 40, 4)]
    [TestCase("my-mod/custom", 960, 1)]
    public void GetInterval_ConvertsTheRunRateToWholeTicks(string name, int tickRate, int expected)
    {
        Assert.That(BuiltInStageSchedule.GetInterval(name, tickRate), Is.EqualTo(expected));
    }

    [Test]
    public void GapTime_UsesASingleDivisionSoEquivalentGapsAgreeBitwise()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.SingleToInt32Bits(BuiltInStageSchedule.GapTime(48, 960)),
                Is.EqualTo(BitConverter.SingleToInt32Bits(BuiltInStageSchedule.GapTime(1, 20))));
            Assert.That(BitConverter.SingleToInt32Bits(BuiltInStageSchedule.GapTime(96, 960)),
                Is.EqualTo(BitConverter.SingleToInt32Bits(BuiltInStageSchedule.GapTime(2, 20))));
            Assert.That(BuiltInStageSchedule.GapTime(1, 20), Is.EqualTo(1f / 20f));
        });
    }
}
