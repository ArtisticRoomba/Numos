using System.Numerics;
using Numos.SimDrawer;
using Raylib_cs;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Draws a small, non-interactive world-axis indicator over a viewport.
/// </summary>
public static class NavigationGizmo
{
    private const float Radius = 27f;
    private const float Margin = 20f;
    private const float LineThickness = 3f;
    private const int LabelFontSize = 14;

    private readonly static Color XColor = new(0.92f, 0.25f, 0.25f, 1f);
    private readonly static Color YColor = new(0.35f, 0.82f, 0.35f, 1f);
    private readonly static Color ZColor = new(0.3f, 0.55f, 1f, 1f);
    private readonly static Color BackdropColor = new(0.02f, 0.02f, 0.025f, 0.7f);

    public static void Draw3D(Camera3D camera, int viewportWidth, int viewportHeight)
    {
        var forward = camera.Target - camera.Position;
        if (forward.LengthSquared() < 0.0001f)
            forward = -Vector3.UnitZ;
        forward = Vector3.Normalize(forward);

        var right = Vector3.Cross(forward, camera.Up);
        if (right.LengthSquared() < 0.0001f)
            right = Vector3.Cross(forward, Vector3.UnitZ);
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        Draw(
            viewportWidth,
            viewportHeight,
            ProjectAxis(Vector3.UnitX, right, up),
            ProjectAxis(Vector3.UnitY, right, up),
            ProjectAxis(Vector3.UnitZ, right, up));
    }

    public static void Draw2D(SliceAxis sliceAxis, int viewportWidth, int viewportHeight)
    {
        // U points right and V points up in the rendered slice. Keep the sliced axis
        // visible as an oblique third spoke so all three world axes remain identifiable.
        var right = Vector2.UnitX;
        var up = -Vector2.UnitY;
        var outOfPlane = Vector2.Normalize(new Vector2(-0.7f, 0.7f));

        var (x, y, z) = sliceAxis switch
        {
            SliceAxis.X => (outOfPlane, up, right),
            SliceAxis.Y => (right, outOfPlane, up),
            SliceAxis.Z => (right, up, outOfPlane),
            _ => throw new ArgumentOutOfRangeException(nameof(sliceAxis), sliceAxis, null)
        };

        Draw(viewportWidth, viewportHeight, x, y, z);
    }

    private static Vector2 ProjectAxis(Vector3 axis, Vector3 right, Vector3 up)
    {
        var projected = new Vector2(Vector3.Dot(axis, right), -Vector3.Dot(axis, up));
        float length = projected.Length();
        return length < 0.12f ? Vector2.Normalize(new Vector2(-0.7f, 0.7f)) * 0.35f : projected;
    }

    private static void Draw(
        int viewportWidth,
        int viewportHeight,
        Vector2 xDirection,
        Vector2 yDirection,
        Vector2 zDirection)
    {
        var origin = new Vector2(
            Math.Max(Margin + Radius, viewportWidth - Margin - Radius),
            Math.Max(Margin + Radius, viewportHeight - Margin - Radius));

        Raylib.DrawCircleV(origin, Radius + 9f, BackdropColor);
        DrawAxis(origin, xDirection, "X", XColor);
        DrawAxis(origin, yDirection, "Y", YColor);
        DrawAxis(origin, zDirection, "Z", ZColor);
        Raylib.DrawCircleV(origin, 3.5f, Color.White);
    }

    private static void DrawAxis(Vector2 origin, Vector2 direction, string label, Color color)
    {
        var end = origin + direction * Radius;
        Raylib.DrawLineEx(origin, end, LineThickness, color);

        var labelPosition = end + Vector2.Normalize(direction) * 11f;
        int labelWidth = Raylib.MeasureText(label, LabelFontSize);
        Raylib.DrawText(
            label,
            (int)(labelPosition.X - labelWidth * 0.5f),
            (int)(labelPosition.Y - LabelFontSize * 0.5f),
            LabelFontSize,
            color);
    }
}