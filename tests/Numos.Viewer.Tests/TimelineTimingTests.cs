using Numos.API;
using Numos.CoreSim.Replay;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class TimelineTimingTests
{
    [TestCase(10d, 20, 200)]
    [TestCase(10d, 960, 9600)]
    [TestCase(0.01d, 20, 10)]
    [TestCase(0.01d, 100000, 1000)]
    public void VisibleTicksScaleWithRate(double seconds, int rate, int expected)
    {
        Assert.That(TimelineTiming.VisibleTicks(seconds, rate), Is.EqualTo(expected));
    }

    [Test]
    public void VisibleTicksPastIntRangeDoNotOverflow()
    {
        long ticks = TimelineTiming.VisibleTicks(3_000_000d, 960);

        Assert.That(ticks, Is.EqualTo(2_880_000_000L));
        Assert.That(ticks, Is.GreaterThan(int.MaxValue));
    }

    [TestCase(50, 20, 1)]
    [TestCase(50, 960, 48)]
    [TestCase(1, 20, 1)]
    [TestCase(1000, 20, 20)]
    [TestCase(75, 20, 2)]
    public void StepTicksRoundToWholeTicksOfAtLeastOne(int milliseconds, int rate, int expected)
    {
        Assert.That(TimelineTiming.StepTicks(milliseconds, rate), Is.EqualTo(expected));
    }

    [TestCase(20, true)]
    [TestCase(960, true)]
    [TestCase(100000, true)]
    [TestCase(0, false)]
    [TestCase(10, false)]
    [TestCase(30, false)]
    [TestCase(100020, false)]
    [TestCase(-20, false)]
    public void ValidatesTickRates(int rate, bool valid)
    {
        Assert.That(TimelineTiming.IsValidTickRate(rate), Is.EqualTo(valid));
        Assert.That(TimelineTiming.DescribeTickRateError(rate) is null, Is.EqualTo(valid));
    }
}
