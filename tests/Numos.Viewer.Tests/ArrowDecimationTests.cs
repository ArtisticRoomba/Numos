using Numos.Viewer.Rendering;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class ArrowDecimationTests
{
    [TestCase(0, 4000, 45, 1)]
    [TestCase(4000, 4000, 45, 1)]
    [TestCase(4001, 4000, 1, 2)]
    [TestCase(1_300_000, 4000, 1, 325)]
    public void GetSampleStride_StepsOverCellsToStayUnderTheLimit(int candidates, int maximum, int rowLength, int expected)
    {
        int stride = VelocityArrows.GetSampleStride(candidates, maximum, rowLength);

        Assert.That(stride, Is.EqualTo(expected));
        Assert.That(candidates / (double)stride, Is.LessThanOrEqualTo(maximum + 1));
    }

    [Test]
    public void GetSampleStride_AvoidsSharingAFactorWithTheRowLength()
    {
        // A stride of 45 over rows of 45 cells would draw every arrow in the same column.
        int stride = VelocityArrows.GetSampleStride(180_000, 4000, 45);

        Assert.That(stride, Is.Not.EqualTo(45));
        Assert.That(GreatestCommonDivisor(stride, 45), Is.EqualTo(1));
    }

    [Test]
    public void GetSampleStride_TreatsNonPositiveLimitAsOne()
    {
        Assert.That(VelocityArrows.GetSampleStride(10, 0, 1), Is.EqualTo(10));
    }

    [TestCase(10, 10, 1)]
    [TestCase(80, 50, 1)]
    [TestCase(200, 200, 4)]
    [TestCase(1500, 1500, 24)]
    [TestCase(0, 100, 1)]
    public void GetArrowStep_KeepsArrowsUnderTheLimitOnBothAxes(int columns, int rows, int expected)
    {
        int step = SliceRenderer.GetArrowStep(columns, rows);

        Assert.That(step, Is.EqualTo(expected));
        long arrows = (long)Math.Ceiling(columns / (double)step) * (long)Math.Ceiling(rows / (double)step);
        Assert.That(arrows, Is.LessThanOrEqualTo(SliceRenderer.MaxVelocityArrows));
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);

        return left;
    }
}
