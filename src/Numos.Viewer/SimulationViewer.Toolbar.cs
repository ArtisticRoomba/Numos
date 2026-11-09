using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const float ToolbarGroupSpacing = 12f;

    /// <summary>
    ///     The number of icon buttons that <see cref="RenderToolbarRightGroup" /> draws. The group width uses it.
    /// </summary>
    private const int ToolbarRightIconButtons = 2;

    /// <summary>
    ///     Space on each side of an icon in a square toolbar button. The 13 px icon cell plus this on both sides gives
    ///     an odd button size, so the icon lands on whole pixels.
    /// </summary>
    private const float ToolbarIconPadding = 5f;

    private readonly static Vector2 ToolbarPadding = new(8f, 4f);

    /// <summary>
    ///     Draws the fixed toolbar under the main menu bar. Call it after the menu bar, because each side bar takes the
    ///     work area left by the ones before it.
    /// </summary>
    private void RenderToolbar()
    {
        var viewport = ImGui.GetMainViewport();
        float buttonSize = Icons.Advance + ToolbarIconPadding * 2f;
        float height = buttonSize + ToolbarPadding.Y * 2f;

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoCollapse;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, ToolbarPadding);
        bool open;
        unsafe
        {
            open = DockBuilderNative.BeginViewportSideBar(
                "###workspace-toolbar",
                viewport.NativePtr,
                ImGuiDir.Up,
                height,
                flags);
        }

        ImGui.PopStyleVar();

        if (open)
            RenderToolbarContents(buttonSize);

        ImGui.End();
    }

    private void RenderToolbarContents(float buttonSize)
    {
        var style = ImGui.GetStyle();

        // Puts the text baseline of this row in the middle of the buttons, which are taller than a text line.
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Vector2(style.FramePadding.X, (buttonSize - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.AlignTextToFramePadding();
        ImGui.PopStyleVar();

        bool hasProject = _frame.HasProject;
        bool inspecting = IsInspecting;
        bool busy = IsBusy;
        bool hasSimulation = _simulation != null;

        if (ToolbarIconButton(Icons.FilePlus, "toolbar-new", Locale.GetString("toolbar-new-tooltip"), buttonSize))
            RequestCreateProject();

        ImGui.SameLine();
        if (ToolbarIconButton(Icons.FolderOpen, "toolbar-open", Locale.GetString("toolbar-open-tooltip"), buttonSize))
            RequestOpenReplay();

        ImGui.SameLine();
        ImGui.BeginDisabled(!hasProject || busy);
        if (ToolbarIconButton(Icons.Save, "toolbar-save", Locale.GetString("toolbar-save-tooltip"), buttonSize))
            RequestSaveReplay();

        ImGui.EndDisabled();

        ToolbarSeparator(buttonSize);
        RenderToolbarRunControls(buttonSize, hasSimulation && !inspecting);

        ImGui.SameLine(0f, ToolbarGroupSpacing);
        RenderToolbarReadouts(hasProject, inspecting);

        ToolbarSeparator(buttonSize);
        bool editingBlocked = IsEditingBlocked;
        for (int index = 0; index < VoxelTools.All.Count; index++)
        {
            if (index > 0)
                ImGui.SameLine();

            var tool = VoxelTools.All[index];
            ImGui.BeginDisabled(!VoxelTools.IsEnabled(tool.Tool, hasSimulation, editingBlocked));
            ToolbarToolButton(tool.Icon, tool.LabelKey, tool.Tool, buttonSize);
            ImGui.EndDisabled();
        }

        ImGui.BeginDisabled(!VoxelTools.IsEnabled(_voxelEditTool, hasSimulation, editingBlocked));
        RenderToolbarToolOptions(buttonSize);
        ImGui.EndDisabled();

        RenderToolbarRightGroup(buttonSize);
    }

    /// <summary>
    ///     Draws Run/Pause and Step. Step ticks the live world, so both stay disabled while the Timeline inspects
    ///     history. The Timeline has its own transport for that. While the host steps many ticks, the button shows
    ///     Pause so the step can be stopped. Other busy jobs disable both.
    /// </summary>
    private void RenderToolbarRunControls(float buttonSize, bool canRun)
    {
        var state = _frame.RunState;
        bool advancing = state == WorkbenchRunState.Busy && _frame.BusyLabel == WorkbenchHost.AdvanceJobLabel;
        bool paused = state == WorkbenchRunState.Paused;
        canRun &= paused || state == WorkbenchRunState.Running || advancing;
        var style = ImGui.GetStyle();
        string runText = $"{Icons.Play} {Locale.GetString("run")}";
        string pauseText = $"{Icons.Pause} {Locale.GetString("common-pause")}";

        // One width for both labels, so the controls after it do not move when the state flips.
        float runWidth = Math.Max(ImGui.CalcTextSize(runText).X, ImGui.CalcTextSize(pauseText).X) +
                         style.FramePadding.X * 2f;

        ImGui.BeginDisabled(!canRun);
        if (ImGui.Button($"{(paused ? runText : pauseText)}##toolbar-run", new Vector2(runWidth, buttonSize)))
        {
            if (paused)
                RunHost();
            else
                PauseHost();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!paused);
        if (ImGui.Button($"{Icons.StepForward} {Locale.GetString("step")}##toolbar-step", new Vector2(0f, buttonSize)))
        {
            _host.Step(1, result =>
            {
                if (result.Succeeded)
                    _document.SteppedToTick = result.Value;
            });

            RequestNextFrame();
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
    }

    /// <summary>
    ///     Draws the STATE, TICK, and SPEED readouts. Each value gets a fixed width, so a longer state or tick does not
    ///     shift the tool buttons. A busy host shows its job and progress as the state.
    /// </summary>
    private void RenderToolbarReadouts(bool hasProject, bool inspecting)
    {
        string noValue = Locale.GetString("toolbar-no-value");
        string inspectingText = Locale.GetString("common-inspecting-history");
        string pausedText = Locale.GetString("paused");
        string runningText = Locale.GetString("running");
        float stateWidth = Math.Max(
            ImGui.CalcTextSize(inspectingText).X,
            Math.Max(ImGui.CalcTextSize(pausedText).X, ImGui.CalcTextSize(runningText).X));

        foreach (string key in HostJobLabels.LocaleKeys)
        {
            string busyText = FormatBusyState(Locale.GetString(key), 1d);
            stateWidth = Math.Max(stateWidth, ImGui.CalcTextSize(busyText).X);
        }

        ImGui.TextDisabled(Locale.GetString("common-state-heading"));
        ImGui.SameLine();
        float stateX = ImGui.GetCursorPosX();
        if (!hasProject)
            ImGui.TextDisabled(noValue);
        else if (IsBusy)
            ImGui.TextColored(ViewerTheme.Caution, FormatBusyState(Locale.GetString(HostJobLabels.GetLocaleKey(_frame.BusyLabel)), _frame.BusyProgress));
        else if (inspecting)
            ImGui.TextUnformatted(inspectingText);
        else if (IsPaused)
            ImGui.TextUnformatted(pausedText);
        else
            ImGui.TextColored(ViewerTheme.Running, runningText);

        ImGui.SameLine(stateX + stateWidth + ToolbarGroupSpacing);
        ImGui.TextDisabled(Locale.GetString("toolbar-tick"));
        ImGui.SameLine();
        float tickX = ImGui.GetCursorPosX();
        if (hasProject)
            ImGui.TextUnformatted(_frame.TickCount.ToString(CultureInfo.InvariantCulture));
        else
            ImGui.TextDisabled(noValue);

        // Room for nine digits, which is about 12 days at 960 Hz.
        ImGui.SameLine(tickX + ImGui.CalcTextSize("000000000").X);

        // The speed field only shows while running, but its space is always kept so the tools do not jump.
        float speedX = ImGui.GetCursorPosX();
        float speedWidth = ImGui.CalcTextSize(Locale.GetString("toolbar-realtime")).X +
                           ImGui.GetStyle().ItemSpacing.X +
                           ImGui.CalcTextSize("000.00x").X;

        if (_frame.RunState == WorkbenchRunState.Running)
        {
            RenderToolbarRealtimeFactor(noValue);
            ImGui.SameLine(speedX + speedWidth);
            ImGui.Dummy(Vector2.Zero);
        }
        else
        {
            ImGui.Dummy(new Vector2(speedWidth, 0f));
        }
    }

    /// <summary>
    ///     Draws the measured simulated seconds per wall-clock second. The host needs about half a second to measure
    ///     it after Run, so the value starts empty.
    /// </summary>
    private void RenderToolbarRealtimeFactor(string noValue)
    {
        ImGui.TextDisabled(Locale.GetString("toolbar-realtime"));
        ImGuiExtensions.ItemTooltip(Locale.GetString("toolbar-realtime-tooltip"));
        ImGui.SameLine();
        double factor = _frame.RealtimeFactor;
        if (factor > 0d)
        {
            string text = factor.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            if (factor < 1d)
                ImGui.TextColored(ViewerTheme.Caution, text);
            else
                ImGui.TextUnformatted(text);
        }
        else
        {
            ImGui.TextDisabled(noValue);
        }

        ImGuiExtensions.ItemTooltip(Locale.GetString("toolbar-realtime-tooltip"));
    }

    private static string FormatBusyState(string label, double? progress)
    {
        return progress is { } fraction
            ? $"{label} {(int)Math.Round(fraction * 100d)}%"
            : label;
    }

    /// <summary>
    ///     Draws the right-aligned group: the unseen-messages indicator, the Messages toggle, Reset layout, and
    ///     Settings. The group falls in after the tools when the window is too narrow to right-align it.
    /// </summary>
    private void RenderToolbarRightGroup(float buttonSize)
    {
        var style = ImGui.GetStyle();
        string? unseenLabel = GetUnseenMessagesLabel(out bool unseenErrors);
        string unseenText = unseenLabel == null
            ? string.Empty
            : $"{(unseenErrors ? Icons.CircleX : Icons.TriangleAlert)} {unseenLabel}";

        float groupWidth = buttonSize * ToolbarRightIconButtons + style.ItemSpacing.X * (ToolbarRightIconButtons - 1);
        if (unseenLabel != null)
            groupWidth += ImGui.CalcTextSize(unseenText).X + style.FramePadding.X * 2f + style.ItemSpacing.X;

        ImGui.SameLine();
        float cursorX = ImGui.GetCursorPosX();
        float rightX = cursorX + ImGui.GetContentRegionAvail().X - groupWidth;
        ImGui.SetCursorPosX(Math.Max(cursorX + ToolbarGroupSpacing - style.ItemSpacing.X, rightX));

        if (unseenLabel != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, unseenErrors ? ViewerTheme.Error : ViewerTheme.Caution);
            bool clicked = ImGui.Button($"{unseenText}##toolbar-unseen-messages", new Vector2(0f, buttonSize));
            ImGui.PopStyleColor();
            ImGuiExtensions.ItemTooltip(Locale.GetString("open-messages-logs"));
            if (clicked)
                ShowMessagesPanel();

            ImGui.SameLine();
        }

        if (ToolbarIconButton(
                Icons.Terminal,
                "toolbar-messages",
                Locale.GetString("toolbar-messages-tooltip"),
                buttonSize,
                _messagesPanelVisible))
        {
            if (_messagesPanelVisible)
                _showMessagesPanel = false;
            else
                ShowMessagesPanel();
        }

        ImGui.SameLine();
        if (ToolbarIconButton(
                Icons.LayoutDashboard,
                "toolbar-reset-layout",
                Locale.GetString("layout-reset-title"),
                buttonSize))
        {
            RequestLayoutReset();
        }
    }

    /// <summary>
    ///     Draws the options of the active voxel tool after the tool buttons: the classification for Paint room, the
    ///     gas, amount, and temperature for Paint gas, and the temperature and volume for Ignite. Each field shows its
    ///     unit after it and its full name as a tooltip.
    /// </summary>
    /// <remarks>
    ///     The fields get the buttons' height, so the row keeps one baseline. The Selected voxels section in
    ///     Properties applies the same values.
    /// </remarks>
    private void RenderToolbarToolOptions(float buttonSize)
    {
        if (_voxelEditTool is VoxelEditTool.Select or VoxelEditTool.EraseGas)
            return;

        var style = ImGui.GetStyle();
        float fieldWidth = ImGui.CalcTextSize("000000.0").X + style.FramePadding.X * 2f;
        ImGui.SameLine(0f, ToolbarGroupSpacing);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Vector2(style.FramePadding.X, (buttonSize - ImGui.GetFontSize()) * 0.5f));

        bool changed = false;
        switch (_voxelEditTool)
        {
            case VoxelEditTool.PaintClassification:
                ImGui.TextUnformatted(Locale.GetString("toolbar-room-id-label"));
                ImGui.SameLine(0f, style.ItemInnerSpacing.X);
                ImGui.SetNextItemWidth(fieldWidth);
                changed |= ImGui.InputInt("##voxel-edit-classification", ref _voxelClassificationDraft, 0, 0);
                ImGuiExtensions.ItemTooltip(
                    $"{Locale.GetString("classification-label")}\n{Locale.GetString("common-classification-values-description")}");

                // Positive values are room IDs and read as typed. The special values get their name.
                if (_voxelClassificationDraft <= 0)
                {
                    ImGui.SameLine(0f, style.ItemInnerSpacing.X);
                    ImGui.TextDisabled(FormatRoomId(_voxelClassificationDraft));
                }

                break;
            case VoxelEditTool.PaintGas:
                changed |= RenderToolbarGasCombo(fieldWidth * 1.6f);
                ImGui.SameLine();
                changed |= ToolbarFloatField("##voxel-edit-moles", ref _injectionMoles, "mol", "amount-mol-label", fieldWidth);
                ImGui.SameLine();
                changed |= ToolbarFloatField(
                    "##voxel-edit-temperature",
                    ref _injectionTemperature,
                    "K",
                    "temperature-k-label",
                    fieldWidth);

                break;
            case VoxelEditTool.Ignite:
                ToolbarFloatField("##ignite-temperature", ref _ignitionTemperature, "K", "temperature-k-label", fieldWidth);
                ImGui.SameLine();
                ToolbarFloatField("##ignite-volume", ref _ignitionVolume, "m³", "ignition-volume", fieldWidth, "%g");
                ImGui.PopStyleVar();
                ImGuiExtensions.QuestionTooltip(Locale.GetString("ignition-source-tooltip"));
                return;
        }

        ImGui.PopStyleVar();
        if (changed)
            _document.VoxelFeedback = null;
    }

    private bool RenderToolbarGasCombo(float width)
    {
        if (Gases.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("no-gas-registered"));
            return false;
        }

        bool changed = false;
        _injectionGasId = Math.Clamp(_injectionGasId, 0, Gases.Count - 1);
        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo("##voxel-edit-gas", FormatGas(_injectionGasId)))
        {
            for (int gasId = 0; gasId < Gases.Count; gasId++)
            {
                bool selected = gasId == _injectionGasId;
                if (ImGui.Selectable(FormatGas(gasId), selected))
                {
                    _injectionGasId = gasId;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGuiExtensions.ItemTooltip(Locale.GetString("common-gas"));
        return changed;
    }

    /// <summary>
    ///     Draws a compact number field with its unit after it.
    /// </summary>
    /// <param name="id">The field ID, starting with <c>##</c>.</param>
    /// <param name="value">The value to edit.</param>
    /// <param name="unit">The unit text drawn after the field.</param>
    /// <param name="nameKey">Locale key of the field's full name, shown as its tooltip.</param>
    /// <param name="width">The field width.</param>
    /// <param name="format">The ImGui number format.</param>
    /// <returns><see langword="true" /> when the value changed.</returns>
    private static bool ToolbarFloatField(
        string id,
        ref float value,
        string unit,
        string nameKey,
        float width,
        string format = "%.1f")
    {
        ImGui.SetNextItemWidth(width);
        bool changed = ImGui.InputFloat(id, ref value, 0f, 0f, format);
        ImGuiExtensions.ItemTooltip(Locale.GetString(nameKey));
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextDisabled(unit);
        return changed;
    }

    private void ToolbarToolButton(string icon, string labelKey, VoxelEditTool tool, float buttonSize)
    {
        if (ToolbarIconButton(icon, $"toolbar-{labelKey}", Locale.GetString(labelKey), buttonSize, _voxelEditTool == tool))
            _voxelEditTool = tool;
    }

    /// <summary>
    ///     Draws a square icon-only button with a tooltip, which also shows while it is disabled.
    /// </summary>
    /// <param name="icon">The glyph from <see cref="Icons" />.</param>
    /// <param name="id">The button ID, unique within the toolbar.</param>
    /// <param name="tooltip">The localized action name.</param>
    /// <param name="size">The side length of the square button.</param>
    /// <param name="pressed">Draws the button in its pressed colour, for a mode or panel that is on.</param>
    private bool ToolbarIconButton(string icon, string id, string tooltip, float size, bool pressed = false)
    {
        if (pressed)
        {
            var active = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
            ImGui.PushStyleColor(ImGuiCol.Button, active);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active);
        }

        // ImGui does not centre a label that is wider than the button minus its frame padding. The padding is set so
        // the icon cell fills that space exactly.
        float padding = (size - Icons.Advance) * 0.5f;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padding, padding));
        bool clicked = ImGui.Button($"{icon}##{id}", new Vector2(size, size));
        ImGui.PopStyleVar();

        if (pressed)
            ImGui.PopStyleColor(2);

        ImGuiExtensions.ItemTooltip(tooltip);
        return clicked;
    }

    /// <summary>
    ///     Draws a vertical rule between toolbar groups on the current line.
    /// </summary>
    private static void ToolbarSeparator(float height)
    {
        float halfGap = ToolbarGroupSpacing * 0.5f;
        ImGui.SameLine(0f, halfGap);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(1f, height));
        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin + new Vector2(1f, height),
            ImGui.ColorConvertFloat4ToU32(ViewerTheme.StructuralLine));

        ImGui.SameLine(0f, halfGap);
    }
}
