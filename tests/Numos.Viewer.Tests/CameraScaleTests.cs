using Numos.Viewer.Rendering;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class CameraScaleTests
{
    [Test]
    public void GetClipPlanes_SmallSceneKeepsTheRaylibDefaults()
    {
        var (near, far) = CameraScale.GetClipPlanes(24f, 24f, 10f);

        Assert.That(near, Is.EqualTo(0.24).Within(1e-9));
        Assert.That(far, Is.EqualTo(CameraScale.MinimumFar));
    }

    [Test]
    public void GetClipPlanes_HugeSceneReachesPastTheFarCorner()
    {
        // 2700 x 600 voxels framed at 2.5 times the extent, like FocusCameraOnScene does.
        float radius = MathF.Sqrt(2700f * 2700f + 600f * 600f) * 0.5f;
        float distance = 2700f * 2.5f;

        var (near, far) = CameraScale.GetClipPlanes(distance, distance, radius);

        Assert.That(far, Is.GreaterThanOrEqualTo(distance + radius));
        Assert.That(far, Is.GreaterThan(CameraScale.MinimumFar));
        Assert.That(near, Is.EqualTo(distance * CameraScale.NearFraction).Within(1e-3));
    }

    [Test]
    public void GetClipPlanes_CloseCameraKeepsTheMinimumNearPlane()
    {
        var (near, _) = CameraScale.GetClipPlanes(0.5f, 0.5f, 3000f);

        Assert.That(near, Is.EqualTo(CameraScale.MinimumNear));
    }

    [TestCase(0f, 0f, 0f)]
    [TestCase(float.NaN, float.PositiveInfinity, -5f)]
    public void GetClipPlanes_BadInputsStillGiveAnOrderedPair(float target, float center, float radius)
    {
        var (near, far) = CameraScale.GetClipPlanes(target, center, radius);

        Assert.That(near, Is.GreaterThan(0.0));
        Assert.That(far, Is.GreaterThan(near));
    }

    [Test]
    public void GetClipPlanes_FarPlaneNeverFallsBelowTenTimesNear()
    {
        var (near, far) = CameraScale.GetClipPlanes(1_000_000f, 0f, 0f);

        Assert.That(far, Is.GreaterThanOrEqualTo(near * 10.0));
    }

    [Test]
    public void GetZoomedDistance_StepIsProportionalToDistance()
    {
        float small = CameraScale.GetZoomedDistance(24f, 1f);
        float large = CameraScale.GetZoomedDistance(13_000f, 1f);

        Assert.That(small, Is.EqualTo(24f * (1f - CameraScale.ZoomFraction)).Within(1e-4));
        Assert.That(large / 13_000f, Is.EqualTo(small / 24f).Within(1e-5));
    }

    [Test]
    public void GetZoomedDistance_ZoomsOutWithNegativeWheel()
    {
        Assert.That(CameraScale.GetZoomedDistance(100f, -1f), Is.GreaterThan(100f));
    }

    [Test]
    public void GetZoomedDistance_StopsAtTheMinimumDistance()
    {
        float distance = 1f;
        for (int i = 0; i < 50; i++)
            distance = CameraScale.GetZoomedDistance(distance, 1f);

        Assert.That(distance, Is.EqualTo(CameraScale.MinimumZoomDistance));
        Assert.That(CameraScale.GetZoomedDistance(0.1f, 1f), Is.EqualTo(0.1f));
    }

    [Test]
    public void GetZoomedDistance_IgnoresZeroAndNonFiniteWheel()
    {
        Assert.That(CameraScale.GetZoomedDistance(50f, 0f), Is.EqualTo(50f));
        Assert.That(CameraScale.GetZoomedDistance(50f, float.NaN), Is.EqualTo(50f));
    }
}
