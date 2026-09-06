using System.Numerics;
using ImGuiNET;

namespace Numos.Viewer.Ui;

internal static class ViewerTheme
{
    public readonly static Vector4 PrimaryText = new(0.88f, 0.90f, 0.92f, 1f);
    public readonly static Vector4 SecondaryText = new(0.62f, 0.66f, 0.70f, 1f);
    public readonly static Vector4 Selection = new(0.16f, 0.43f, 0.62f, 1f);
    public readonly static Vector4 Running = new(0.34f, 0.72f, 0.43f, 1f);
    public readonly static Vector4 Caution = new(0.90f, 0.66f, 0.24f, 1f);
    public readonly static Vector4 Error = new(0.90f, 0.30f, 0.30f, 1f);
    public readonly static Vector4 RecessedSurface = new(0.095f, 0.11f, 0.13f, 1f);
    public readonly static Vector4 StructuralLine = new(0.25f, 0.28f, 0.31f, 1f);
    public readonly static Vector4[] GasPalette =
    [
        new(0.24f, 0.67f, 0.90f, 1f),
        new(0.91f, 0.63f, 0.22f, 1f),
        new(0.42f, 0.76f, 0.45f, 1f),
        new(0.76f, 0.43f, 0.82f, 1f),
        new(0.88f, 0.40f, 0.42f, 1f),
        new(0.28f, 0.75f, 0.70f, 1f)
    ];

    public static void Apply()
    {
        var style = ImGui.GetStyle();
        style.WindowPadding = new Vector2(8f, 8f);
        style.FramePadding = new Vector2(7f, 4f);
        style.CellPadding = new Vector2(6f, 4f);
        style.ItemSpacing = new Vector2(7f, 5f);
        style.ItemInnerSpacing = new Vector2(5f, 4f);
        style.IndentSpacing = 18f;
        style.ScrollbarSize = 15f;
        style.GrabMinSize = 9f;

        style.WindowBorderSize = 1f;
        style.ChildBorderSize = 1f;
        style.PopupBorderSize = 1f;
        style.FrameBorderSize = 1f;
        style.TabBorderSize = 1f;

        style.WindowRounding = 0f;
        style.ChildRounding = 0f;
        style.FrameRounding = 0f;
        style.PopupRounding = 0f;
        style.ScrollbarRounding = 0f;
        style.GrabRounding = 0f;
        style.TabRounding = 0f;

        RangeAccessor<Vector4> colors = style.Colors;
        colors[(int)ImGuiCol.Text] = PrimaryText;
        colors[(int)ImGuiCol.TextDisabled] = SecondaryText;
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.145f, 0.16f, 0.18f, 1f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.115f, 0.13f, 0.15f, 1f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.16f, 0.175f, 0.195f, 1f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.34f, 0.37f, 0.40f, 1f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0.035f, 0.04f, 0.05f, 0.9f);
        colors[(int)ImGuiCol.FrameBg] = RecessedSurface;
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.13f, 0.18f, 0.22f, 1f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.11f, 0.25f, 0.34f, 1f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.12f, 0.135f, 0.15f, 1f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.18f, 0.22f, 0.25f, 1f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.10f, 0.115f, 0.13f, 1f);
        colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.18f, 0.195f, 0.21f, 1f);
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.08f, 0.09f, 0.105f, 1f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.30f, 0.33f, 0.36f, 1f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.38f, 0.41f, 0.44f, 1f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.22f, 0.45f, 0.60f, 1f);
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.54f, 0.80f, 0.96f, 1f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.46f, 0.62f, 0.72f, 1f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.60f, 0.82f, 0.95f, 1f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.27f, 0.30f, 0.33f, 1f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.34f, 0.38f, 0.41f, 1f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.13f, 0.31f, 0.43f, 1f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.20f, 0.25f, 0.29f, 1f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.22f, 0.39f, 0.50f, 1f);
        colors[(int)ImGuiCol.HeaderActive] = Selection;
        colors[(int)ImGuiCol.Separator] = new Vector4(0.34f, 0.37f, 0.40f, 1f);
        colors[(int)ImGuiCol.SeparatorHovered] = new Vector4(0.35f, 0.62f, 0.78f, 1f);
        colors[(int)ImGuiCol.SeparatorActive] = new Vector4(0.48f, 0.74f, 0.90f, 1f);
        colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.28f, 0.36f, 0.41f, 0.7f);
        colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.34f, 0.62f, 0.78f, 0.9f);
        colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.48f, 0.74f, 0.90f, 1f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.17f, 0.19f, 0.21f, 1f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.25f, 0.42f, 0.53f, 1f);
        colors[(int)ImGuiCol.TabSelected] = new Vector4(0.23f, 0.35f, 0.43f, 1f);
        colors[(int)ImGuiCol.TabDimmed] = new Vector4(0.13f, 0.145f, 0.16f, 1f);
        colors[(int)ImGuiCol.TabDimmedSelected] = new Vector4(0.18f, 0.25f, 0.30f, 1f);
        colors[(int)ImGuiCol.DockingPreview] = new Vector4(0.25f, 0.58f, 0.78f, 0.7f);
        colors[(int)ImGuiCol.DockingEmptyBg] = new Vector4(0.045f, 0.05f, 0.06f, 1f);
        colors[(int)ImGuiCol.TableHeaderBg] = new Vector4(0.20f, 0.22f, 0.24f, 1f);
        colors[(int)ImGuiCol.TableBorderStrong] = new Vector4(0.36f, 0.39f, 0.42f, 1f);
        colors[(int)ImGuiCol.TableBorderLight] = new Vector4(0.25f, 0.28f, 0.31f, 1f);
        colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(0.19f, 0.205f, 0.22f, 0.45f);
        colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.16f, 0.43f, 0.62f, 0.65f);
        colors[(int)ImGuiCol.NavCursor] = new Vector4(0.52f, 0.80f, 0.96f, 1f);
    }
}