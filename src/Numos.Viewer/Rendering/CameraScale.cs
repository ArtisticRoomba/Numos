namespace Numos.Viewer.Rendering;

/// <summary>
///     Scale-dependent camera math for the 3D viewport: the near and far clip planes, and the wheel zoom step.
/// </summary>
/// <remarks>
///     Raylib clips at fixed planes (0.05 and 1000 by default), which blanks a scene thousands of voxels wide.
///     These helpers derive the planes and the zoom step from how far the camera sits from the scene, so small
///     and huge worlds both stay visible and zoomable.
/// </remarks>
internal static class CameraScale
{
    /// <summary>
    ///     Smallest near plane distance, equal to the Raylib default.
    /// </summary>
    public const double MinimumNear = 0.05;

    /// <summary>
    ///     Smallest far plane distance, equal to the Raylib default.
    /// </summary>
    public const double MinimumFar = 1000.0;

    /// <summary>
    ///     Fraction of the camera-to-target distance used as the near plane.
    /// </summary>
    public const double NearFraction = 0.01;

    /// <summary>
    ///     Fraction of the distance the camera moves toward the target per wheel notch.
    /// </summary>
    public const float ZoomFraction = 0.12f;

    /// <summary>
    ///     Smallest camera-to-target distance the wheel zoom reaches.
    /// </summary>
    public const float MinimumZoomDistance = 0.5f;

    private const double FarMargin = 1.05;

    /// <summary>
    ///     Computes the near and far clip planes for a perspective camera.
    /// </summary>
    /// <param name="distanceToTarget">Distance from the camera to its orbit target.</param>
    /// <param name="distanceToSceneCenter">Distance from the camera to the center of the scene bounds.</param>
    /// <param name="sceneRadius">Radius of a sphere around the scene center that contains the scene.</param>
    /// <returns>
    ///     The near plane and the far plane. The near plane is <see cref="NearFraction" /> of
    ///     <paramref name="distanceToTarget" />, at least <see cref="MinimumNear" />. The far plane reaches the far side
    ///     of the scene sphere plus a 5 % margin, at least <see cref="MinimumFar" />.
    /// </returns>
    /// <remarks>
    ///     Every scene point lies within <c>distanceToSceneCenter + sceneRadius</c> of the camera, so that distance
    ///     is the tightest far plane that keeps the whole scene. Non-finite or negative inputs count as zero.
    /// </remarks>
    public static (double Near, double Far) GetClipPlanes(
        float distanceToTarget,
        float distanceToSceneCenter,
        float sceneRadius)
    {
        double target = Sanitize(distanceToTarget);
        double center = Sanitize(distanceToSceneCenter);
        double radius = Sanitize(sceneRadius);

        double far = Math.Max(MinimumFar, (center + radius) * FarMargin);
        double near = Math.Min(Math.Max(MinimumNear, target * NearFraction), far * 0.1);
        return (near, far);
    }

    /// <summary>
    ///     Computes the camera-to-target distance after a mouse wheel move.
    /// </summary>
    /// <param name="distance">Current camera-to-target distance.</param>
    /// <param name="wheel">Wheel notches. Positive values zoom in.</param>
    /// <returns>
    ///     <c>distance × (1 − ZoomFraction)^wheel</c>, never below <see cref="MinimumZoomDistance" />. A distance
    ///     that is already below the minimum is left unchanged when zooming in.
    /// </returns>
    public static float GetZoomedDistance(float distance, float wheel)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(wheel) || wheel == 0f)
            return distance;

        float scaled = distance * MathF.Pow(1f - ZoomFraction, wheel);
        return wheel > 0f
            ? Math.Min(distance, Math.Max(scaled, MinimumZoomDistance))
            : scaled;
    }

    private static double Sanitize(float value)
    {
        return float.IsFinite(value) && value > 0f ? value : 0.0;
    }
}
