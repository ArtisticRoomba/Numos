using System.Numerics;
using ImGuiNET;

namespace Numos.Viewer.Ui;

internal static class ImGuiExtensions
{
    private const ImGuiWindowFlags DefaultModalFlags =
        ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoSavedSettings;

    /// <summary>
    ///     Flags for compact readout tables: columns fit their content and rows are separated by light rules.
    /// </summary>
    public const ImGuiTableFlags ReadoutTableFlags =
        ImGuiTableFlags.SizingFixedFit |
        ImGuiTableFlags.BordersInnerH |
        ImGuiTableFlags.NoSavedSettings;

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

    /// <summary>
    ///     Draws a <c>(?)</c> help affordance on the current line that shows <paramref name="tooltip" /> on hover or
    ///     when keyboard navigation lands on it.
    /// </summary>
    /// <remarks>
    ///     The affordance is a real item so it gets a usable hit target and a place in the navigation order. Its ID is
    ///     scoped by the tooltip text, so repeated helpers in one window only collide if their text is identical.
    /// </remarks>
    public static void QuestionTooltip(string tooltip)
    {
        const string glyph = "(?)";
        const float horizontalPadding = 4f;

        ImGui.SameLine();
        ImGui.PushID(tooltip);

        var glyphSize = ImGui.CalcTextSize(glyph);
        var buttonSize = new Vector2(
            glyphSize.X + horizontalPadding * 2f,
            MathF.Max(glyphSize.Y, ImGui.GetFrameHeight()));

        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##question-tooltip", buttonSize);

        ImGui.GetWindowDrawList().AddText(
            origin + (buttonSize - glyphSize) * 0.5f,
            ImGui.ColorConvertFloat4ToU32(ViewerTheme.SecondaryText),
            glyph);

        // IsItemFocused stays true after a mouse click, so only treat focus as a request while the nav cursor is
        // actually showing.
        bool keyboardFocused = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip) || keyboardFocused)
            ImGui.SetTooltip(tooltip);

        ImGui.PopID();
    }

    /// <summary>
    ///     Begins a table with text wrapping turned off until the scope is disposed.
    /// </summary>
    /// <remarks>
    ///     <see cref="BeginWindow" /> wraps text at the content edge, which inside a table is the cell edge. Auto-fit
    ///     columns measure their wrapped content, so they would shrink to their widest word. A cell that should wrap can
    ///     push its own wrap position.
    /// </remarks>
    /// <param name="id">Table ID, unique within the current window.</param>
    /// <param name="columns">Number of columns.</param>
    /// <param name="flags">Table flags, passed through to <c>ImGui.BeginTable</c>.</param>
    /// <param name="outerSize">Outer size, passed through to <c>ImGui.BeginTable</c>.</param>
    public static TableScope BeginTable(
        string id,
        int columns,
        ImGuiTableFlags flags,
        Vector2 outerSize = default)
    {
        ImGui.PushTextWrapPos(-1f);
        return new TableScope(ImGui.BeginTable(id, columns, flags, outerSize));
    }

    /// <summary>
    ///     Begins a two-column label/value table for independent readouts. Fill it with
    ///     <see cref="DefinitionRow(string, string)" />.
    /// </summary>
    /// <param name="id">Table ID, unique within the current window.</param>
    public static TableScope BeginDefinitionTable(string id)
    {
        var table = BeginTable(id, 2, ReadoutTableFlags);
        if (table.IsVisible)
        {
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        }

        return table;
    }

    public static void DefinitionRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value);
    }

    public static void DefinitionRow(string label, string value, Vector4 valueColor)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.PushStyleColor(ImGuiCol.Text, valueColor);
        ImGui.TextUnformatted(value);
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     Draws <paramref name="text" /> against the right edge of the current table cell, for numeric columns.
    /// </summary>
    public static void TextRightAligned(string text)
    {
        float offset = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (offset > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        ImGui.TextUnformatted(text);
    }

    /// <summary>
    ///     Draws a square colour swatch with a control outline, so dark colours stay visible against the panel face.
    /// </summary>
    /// <remarks>
    ///     The swatch is drawn rather than a <c>ColorButton</c>: it is not a control, so it takes no navigation stop.
    /// </remarks>
    public static void ColorSwatch(Vector4 color, float size)
    {
        var minimum = ImGui.GetCursorScreenPos();
        var maximum = minimum + new Vector2(size, size);
        ImGui.Dummy(new Vector2(size, size));

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(minimum, maximum, ImGui.ColorConvertFloat4ToU32(color));
        draw.AddRect(minimum, maximum, ImGui.ColorConvertFloat4ToU32(ViewerTheme.ControlOutline));
    }

    /// <summary>
    ///     Shows the result of the last local action beside the control that caused it. Nothing is drawn for an empty
    ///     message.
    /// </summary>
    /// <remarks>
    ///     Colour is supplementary here, so the message text itself has to say whether the action failed.
    /// </remarks>
    public static void Feedback(string? message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, isError ? ViewerTheme.Error : ViewerTheme.Running);
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     Shared treatment for a panel that is showing state it cannot edit, such as a replay inspection.
    /// </summary>
    public static void ReadOnlyNotice(string message)
    {
        ImGui.TextWrapped(message);
        ImGui.Separator();
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

    /// <summary>
    ///     Ends the table if it began, then restores the wrap position pushed by <see cref="BeginTable" /> either way.
    /// </summary>
    public readonly struct TableScope : IDisposable
    {
        internal TableScope(bool isVisible)
        {
            IsVisible = isVisible;
        }

        public bool IsVisible { get; }

        public void Dispose()
        {
            if (IsVisible)
                ImGui.EndTable();

            ImGui.PopTextWrapPos();
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