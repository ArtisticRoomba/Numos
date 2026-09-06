using System.Numerics;
using ImGuiNET;

namespace Numos.Viewer.Ui;

internal static class ImGuiExtensions
{
    private const ImGuiWindowFlags DefaultModalFlags =
        ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoSavedSettings;

    public static WindowScope BeginWindow(
        string title,
        ref bool isOpen,
        Vector2 firstUsePosition,
        Vector2 firstUseSize,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.SetNextWindowPos(firstUsePosition, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(firstUseSize, ImGuiCond.FirstUseEver);
        bool isVisible = ImGui.Begin(title, ref isOpen, flags);
        if (isVisible)
            ImGui.PushTextWrapPos(0f);

        return new WindowScope(isVisible);
    }

    public static PopupScope BeginPopupModal(
        string popupId,
        ref bool isOpen,
        ImGuiWindowFlags flags = DefaultModalFlags)
    {
        return new PopupScope(ImGui.BeginPopupModal(popupId, ref isOpen, flags));
    }

    public static void OpenPopupWhenRequested(string popupId, ref bool isRequested)
    {
        if (!isRequested)
            return;

        ImGui.OpenPopup(popupId);
        isRequested = false;
    }

    public static void TextCentered(
        string text,
        Action<string>? renderText = null)
    {
        float textWidth = ImGui.CalcTextSize(text).X;
        float centeredX = (ImGui.GetWindowSize().X - textWidth) * 0.5f;

        ImGui.SetCursorPosX(centeredX);
        (renderText ?? ImGui.TextUnformatted)(text);
    }

    public static void StatusField(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(value);
    }

    public static void QuestionTooltip(string tooltip)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    public readonly struct WindowScope : IDisposable
    {
        internal WindowScope(bool isVisible)
        {
            IsVisible = isVisible;
        }

        public bool IsVisible { get; }

        public void Dispose()
        {
            if (IsVisible)
                ImGui.PopTextWrapPos();

            ImGui.End();
        }
    }

    public readonly struct PopupScope : IDisposable
    {
        internal PopupScope(bool isVisible)
        {
            IsVisible = isVisible;
        }

        public bool IsVisible { get; }

        public void Dispose()
        {
            if (IsVisible)
                ImGui.EndPopup();
        }
    }
}