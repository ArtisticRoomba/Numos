using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly static AtmosTransportModel[] TransportModelValues =
        [AtmosTransportModel.Legacy, AtmosTransportModel.Euler];

    private bool _closeCreateProjectModal;
    private bool _closeProjectModalOpen;
    private bool _createProjectPending;
    private string? _createProjectError;
    private bool _createProjectModalOpen;

    private float _ignitionTemperature = 2000f;
    private float _ignitionVolume = 1e-3f;
    private bool _includeDefaultGasesDraft = true;

    private int _injectionGasId;
    private float _injectionMoles = 1f;
    private float _injectionTemperature = AtmosPhysicalConstants.RoomTemperature;

    private int _projectChunkDepthDraft = 1;
    private int _projectChunkHeightDraft = AtmosChunkConstants.DefaultHeight;
    private int _projectChunkWidthDraft = AtmosChunkConstants.DefaultWidth;

    private string _projectNameDraft = "Untitled Simulation";
    private AtmosTransportModel _projectTransportModelDraft;
    private bool _requestOpenCloseProject;

    private bool _requestOpenCreateProject;
    private int _newProjectTickRateDraft = AtmosWorld.DefaultTickRate;
    private int _voxelClassificationDraft;

    private void RequestCreateProject()
    {
        _projectNameDraft = !_frame.HasProject
            ? Locale.GetString("common-untitled-simulation")
            : Locale.GetString("project-name-copy", ("projectName", _frame.ProjectName));

        var dimensions = (_simulation ?? (_frame.Simulations.Count > 0 ? _frame.Simulations[0] : null))
            ?.ChunkDimensions ?? default;

        _projectChunkWidthDraft = dimensions.X > 0 ? dimensions.X : AtmosChunkConstants.DefaultWidth;
        _projectChunkHeightDraft = dimensions.Y > 0 ? dimensions.Y : AtmosChunkConstants.DefaultHeight;
        _projectChunkDepthDraft = dimensions.Z > 0 ? dimensions.Z : 1;
        _includeDefaultGasesDraft = true;
        var currentModel = !_frame.HasProject
            ? null
            : AtmosWorldSolverPipeline.DetectTransportModel(_frame.SolverSteps);

        _projectTransportModelDraft = currentModel ?? AtmosTransportModel.Euler;
        _newProjectTickRateDraft = _frame.HasProject ? _frame.TickRate : AtmosWorld.DefaultTickRate;
        _createProjectError = null;
        _closeCreateProjectModal = false;
        _requestOpenCreateProject = true;
        _createProjectModalOpen = true;
    }

    private void RequestCloseProject()
    {
        if (!_frame.HasProject)
            return;

        _requestOpenCloseProject = true;
        _closeProjectModalOpen = true;
    }

    private void DrawCreateProjectModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(Locale.GetString("create-simulation"), ref _requestOpenCreateProject);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + viewport.Size * 0.5f,
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));

        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(Locale.GetString("create-simulation"), ref _createProjectModalOpen);
        if (!modal.IsVisible)
            return;

        if (_closeCreateProjectModal)
        {
            _closeCreateProjectModal = false;
            _createProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
            return;
        }

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.InputText(Locale.GetString("project-name"), ref _projectNameDraft, 128);
        ImGui.Separator();
        ImGui.Text(Locale.GetString("chunk-dimensions"));
        ImGuiExtensions.QuestionTooltip(Locale.GetString("chunk-dimensions-description"));

        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("width-project-chunk"), ref _projectChunkWidthDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("height-project-chunk"), ref _projectChunkHeightDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("depth-project-chunk"), ref _projectChunkDepthDraft);

        ImGui.Separator();
        RenderTransportModelCombo();
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("new-project-tick-rate") + "##new-project", ref _newProjectTickRateDraft, 0, 0);
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-project-tick-rate-description"));

        ImGui.Separator();
        ImGui.Checkbox(Locale.GetString("include-oxygen-and-nitrogen"), ref _includeDefaultGasesDraft);
        ImGuiExtensions.QuestionTooltip(
            _includeDefaultGasesDraft
                ? Locale.GetString("default-gases-appended-description")
                : Locale.GetString("blank-gas-registry-description"));

        if (_frame.HasProject)
        {
            int branchCount = _frame.Timeline?.Branches.Count ?? 0;
            ImGui.Spacing();
            ImGui.TextColored(
                ViewerTheme.Caution,
                branchCount > 1
                    ? Locale.GetString("create-project-discard-branches", ("branchCount", branchCount))
                    : Locale.GetString("create-project-close-current"));
        }

        if (!string.IsNullOrWhiteSpace(_createProjectError))
        {
            ImGui.Spacing();
            ImGui.TextColored(ViewerTheme.Error, _createProjectError);
        }

        ImGui.Spacing();

        // The host creates the project. The modal stays open until it answers, so a rejected option can be fixed.
        ImGui.BeginDisabled(_createProjectPending || IsBusy);
        if (ImGui.Button(Locale.GetString("create"), new Vector2(120, 0)))
        {
            if (TimelineTiming.DescribeTickRateError(_newProjectTickRateDraft) is { } tickRateError)
            {
                _createProjectError = tickRateError;
            }
            else
            {
                _createProjectError = null;
                CreateSimulationProject(
                    _projectNameDraft,
                    _projectChunkWidthDraft,
                    _projectChunkHeightDraft,
                    _projectChunkDepthDraft,
                    _includeDefaultGasesDraft,
                    _projectTransportModelDraft,
                    _newProjectTickRateDraft);
            }
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _createProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary>
    ///     Picks the preset that fills the stage list of a new project. The Solvers properties change the stages
    ///     afterwards.
    /// </summary>
    private void RenderTransportModelCombo()
    {
        string[] labels =
            [Locale.GetString("legacy-pressure-relaxation"), Locale.GetString("euler-compressible-flow")];

        int index = Array.IndexOf(TransportModelValues, _projectTransportModelDraft);
        if (ImGui.Combo(Locale.GetString("transport-model"), ref index, labels, labels.Length) && index >= 0)
            _projectTransportModelDraft = TransportModelValues[index];

        ImGuiExtensions.QuestionTooltip(Locale.GetString("transport-model-description"));
    }

    private void DrawCloseProjectModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(Locale.GetString("close-simulation-project"), ref _requestOpenCloseProject);

        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(Locale.GetString("close-simulation-project"), ref _closeProjectModalOpen);
        if (!modal.IsVisible)
            return;

        int branchCount = _frame.Timeline?.Branches.Count ?? 0;
        string branchWarning = branchCount > 1
            ? Locale.GetString("close-project-branches-warning", ("branchCount", branchCount))
            : string.Empty;

        ImGui.TextWrapped(
            Locale.GetString("close-project-confirmation", ("projectName", _frame.ProjectName), ("branchWarning", branchWarning)));

        ImGui.Spacing();
        ImGui.BeginDisabled(IsBusy);
        bool close = ImGui.Button(Locale.GetString("close-project"), new Vector2(140, 0));
        ImGui.EndDisabled();
        if (close)
        {
            Send(static session =>
            {
                session.CloseProject();
                return true;
            });
            _closeProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _closeProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private void RequestRemoveChunk(AtmosChunkHandle chunk)
    {
        _document.ChunkPendingRemoval = chunk;
        _document.RequestRemoveChunk = true;
        _document.RemoveChunkModalOpen = true;
    }

    private void DrawRemoveChunkModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(Locale.GetString("remove-chunk-tools"), ref _document.RequestRemoveChunk);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(Locale.GetString("remove-chunk-tools"), ref _document.RemoveChunkModalOpen);
        if (!modal.IsVisible)
            return;

        if (_simulation == null ||
            _document.ChunkPendingRemoval is not { } chunk ||
            !_liveChunkPositions.Contains(chunk.Position))
        {
            ImGui.TextWrapped(Locale.GetString("selected-chunk-no-longer-exists"));
            if (ImGui.Button(Locale.GetString("common-close"), new Vector2(120, 0)))
            {
                _document.RemoveChunkModalOpen = false;
                _document.ChunkPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            return;
        }

        ImGui.TextWrapped(Locale.GetString("remove-chunk-confirmation", ("chunkPosition", FormatChunkPosition(chunk.Position))));

        ImGui.Spacing();
        ImGui.BeginDisabled(IsEditingBlocked);
        if (ImGui.Button(Locale.GetString("remove-chunk"), new Vector2(140, 0)))
        {
            RemoveProjectChunk(chunk);
            _document.ChunkPendingRemoval = null;
            _document.RemoveChunkModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _document.ChunkPendingRemoval = null;
            _document.RemoveChunkModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private void RequestResetConfiguration()
    {
        _document.RequestResetConfiguration = true;
        _document.ResetConfigurationModalOpen = true;
    }

    private void DrawResetConfigurationModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(
            Locale.GetString("reset-atmosphere-settings-configuration"),
            ref _document.RequestResetConfiguration);

        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(
            Locale.GetString("reset-atmosphere-settings-configuration"),
            ref _document.ResetConfigurationModalOpen);

        if (!modal.IsVisible)
            return;

        ImGui.TextWrapped(Locale.GetString("reset-atmosphere-settings-confirmation"));

        ImGui.Spacing();
        ImGui.BeginDisabled(_document.ConfigDraft.Value == null || IsEditingBlocked);
        if (ImGui.Button(Locale.GetString("reset"), new Vector2(120, 0)))
        {
            ResetConfigurationValues();
            _document.ResetConfigurationModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _document.ResetConfigurationModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    /// <summary>
    ///     Draws the shared read-only notice while the Timeline is inspecting history.
    /// </summary>
    /// <returns>
    ///     <see langword="true" /> when the caller should disable its editing controls, which is also the case while
    ///     the host is busy.
    /// </returns>
    private bool DrawInspectionNotice()
    {
        if (_frame.Timeline is not { IsInspecting: true } timeline)
            return IsBusy;

        ImGuiExtensions.ReadOnlyNotice(Locale.GetString("read-only-inspection-notice", ("tick", timeline.Position.Tick)));

        return true;
    }

    private void SetProjectMessage(string message, bool isError)
    {
        WriteMessage(isError ? WorkbenchLogLevel.Error : WorkbenchLogLevel.Info, "Simulation", message);
    }

    private static void DrawFeedback(FeedbackMessage? feedback)
    {
        ImGuiExtensions.Feedback(feedback?.Message, feedback?.IsError == true);
    }

    /// <summary>
    ///     Draws the new chunk form for the active simulation.
    /// </summary>
    /// <param name="inspecting">Whether editing is blocked.</param>
    /// <param name="focus">Whether to scroll to the form and focus its first field.</param>
    private void RenderNewChunkForm(bool inspecting, bool focus = false)
    {
        ImGui.SeparatorText(Locale.GetString("new-chunk"));
        bool newChunkChanged = false;
        ImGui.BeginDisabled(inspecting);
        FocusFormHere(focus);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("new-chunk-x"), ref _document.Chunk.NewX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("new-chunk-y"), ref _document.Chunk.NewY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("new-chunk-z"), ref _document.Chunk.NewZ);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("chunk-grid-position-tooltip"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("initial-room-id"), ref _document.Chunk.NewRoomId);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-chunk-classification-tooltip"));

        if (newChunkChanged)
            _document.ChunkFeedback = null;

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("add-chunk"), new Vector2(120, 0)))
            AddProjectChunk(new Int3(_document.Chunk.NewX, _document.Chunk.NewY, _document.Chunk.NewZ), _document.Chunk.NewRoomId);

        ImGui.EndDisabled();
        DrawFeedback(_document.ChunkFeedback);
    }

    private void MoveCameraToToolChunk(Int3 position)
    {
        if (_drawData != null && _drawData.Chunks.TryGetValue(position, out var chunkData))
            MoveCameraToChunk(chunkData);
    }

    private void RenderSelectedChunkActions(bool inspecting)
    {
        if (GetSelectedChunkPosition() is not { } position || !_liveChunkPositions.Contains(position))
        {
            ImGui.TextDisabled(Locale.GetString("select-chunk-to-act"));
            return;
        }

        var chunk = new AtmosChunkHandle(position);
        ImGui.BeginDisabled(_drawData?.Chunks.ContainsKey(position) != true);
        if (ImGui.Button(Locale.GetString("move-camera-chunk-action")))
            MoveCameraToToolChunk(position);

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("unsleep-chunk-action")))
            UnsleepProjectChunk(chunk);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("unsleep-tooltip"));

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("seal-with-walls-chunk-action")))
            SealProjectChunk(chunk);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("seal-with-walls-tooltip"));

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("remove-chunk-action")))
            RequestRemoveChunk(chunk);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("remove-chunk-tooltip"));

        ImGui.Spacing();
        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        if (ImGui.InputInt(Locale.GetString("classification-chunk-fill"), ref _document.Chunk.ToolClassification))
            _document.ChunkFeedback = null;

        ImGui.EndDisabled();
        ImGui.TextDisabled(Locale.GetString("common-classification-values-description"));

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("fill-selected-chunk")))
            FillProjectChunk(chunk, _document.Chunk.ToolClassification);

        ImGui.EndDisabled();
    }

    /// <summary>
    ///     Draws the gas registry table and the add-gas form.
    /// </summary>
    /// <param name="inspecting">Whether editing is blocked.</param>
    /// <param name="focusNewGas">Whether to scroll to the add-gas form and focus its first field.</param>
    private void RenderProjectGasControls(bool inspecting, bool focusNewGas = false)
    {
        if (Gases.Count == 0)
            ImGui.TextDisabled(Locale.GetString("blank-gas-registry"));
        else
            RenderGasRegistryTable(inspecting);

        DrawFeedback(_document.GasRegistryFeedback);

        ImGui.SeparatorText(Locale.GetString("add-gas-definition"));
        bool formChanged = false;
        ImGui.BeginDisabled(inspecting);
        FocusFormHere(focusNewGas);
        formChanged |= ImGui.InputText(Locale.GetString("new-gas-name"), ref _document.Gas.Name, 64);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(
            Locale.GetString("new-gas-molar-cv"),
            ref _document.Gas.MolarHeatCapacityAtConstantVolume);

        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(Locale.GetString("new-gas-boiling-point"), ref _document.Gas.BoilingPoint);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-gas-boiling-point-tooltip"));

        ImGui.BeginDisabled(inspecting);
        formChanged |= ImGui.Checkbox(Locale.GetString("new-gas-condensation-enabled"), ref _document.Gas.CondensationEnabled);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(
            Locale.GetString("new-gas-vaporization-enthalpy"),
            ref _document.Gas.EnthalpyOfVaporization);

        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputInt(Locale.GetString("new-gas-liquid-id"), ref _document.Gas.LiquidId);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-gas-liquid-id-tooltip"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(Locale.GetString("new-gas-diffusion-coefficient"), ref _document.Gas.DiffusionCoefficient);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-gas-diffusion-coefficient-tooltip"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(Locale.GetString("new-gas-molar-mass"), ref _document.Gas.MolarMass, 0f, 0f, "%.6f");
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-gas-molar-mass-tooltip"));

        if (formChanged)
            _document.NewGasFeedback = null;

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("add-gas"), new Vector2(120, 0)))
        {
            AddProjectGas(
                new GasProperties
                {
                    Name = _document.Gas.Name,
                    MolarHeatCapacityAtConstantVolume = _document.Gas.MolarHeatCapacityAtConstantVolume,
                    BoilingPoint = _document.Gas.BoilingPoint,
                    CondensationEnabled = _document.Gas.CondensationEnabled,
                    MolarEnthalpyOfVaporization = _document.Gas.EnthalpyOfVaporization,
                    LiquidId = _document.Gas.LiquidId,
                    DiffusionCoefficient = _document.Gas.DiffusionCoefficient,
                    MolarMass = _document.Gas.MolarMass
                });
        }

        ImGui.EndDisabled();
        DrawFeedback(_document.NewGasFeedback);
    }

    private void RenderGasRegistryTable(bool inspecting)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingFixedFit;

        var registry = Gases;
        int gasToRemove = -1;
        using (var table = ImGuiExtensions.BeginTable("GasRegistry##configuration", 6, flags))
        {
            if (!table.IsVisible)
                return;

            ImGui.TableSetupColumn(Locale.GetString("gas-id-column"));
            ImGui.TableSetupColumn("##colour");
            ImGui.TableSetupColumn(Locale.GetString("gas-name-column"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Locale.GetString("gas-cv-column"));
            ImGui.TableSetupColumn(Locale.GetString("gas-boiling-column"));
            ImGui.TableSetupColumn("##remove");
            ImGui.TableHeadersRow();

            float swatchSize = ImGui.GetTextLineHeight();
            for (int gasId = 0; gasId < registry.Count; gasId++)
            {
                var gas = registry[gasId];
                ImGui.PushID(gasId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGuiExtensions.TextRightAligned(gasId.ToString(CultureInfo.InvariantCulture));

                // Same mapping as the gas makeup bar, so a colour means the same gas in every panel.
                ImGui.TableSetColumnIndex(1);
                ImGuiExtensions.ColorSwatch(GetGasColor(gasId), swatchSize);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(gas.Name ?? string.Empty);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                    ImGui.SetTooltip(FormatGasDetails(gas));

                ImGui.TableSetColumnIndex(3);
                ImGuiExtensions.TextRightAligned(QuantityFormat.Format(gas.MolarHeatCapacityAtConstantVolume, string.Empty));

                ImGui.TableSetColumnIndex(4);
                ImGuiExtensions.TextRightAligned(QuantityFormat.Format(gas.BoilingPoint, string.Empty));
                ImGui.TableSetColumnIndex(5);
                ImGui.BeginDisabled(inspecting);
                if (ImGui.SmallButton(Locale.GetString("remove")))
                    gasToRemove = gasId;

                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip | ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Locale.GetString("gas-removal-tooltip"));

                ImGui.PopID();
            }
        }

        if (gasToRemove >= 0)
            RemoveProjectGas(gasToRemove);
    }

    private static string FormatGasDetails(GasProperties gas)
    {
        return Locale.GetString(
            "gas-details",
            ("name", gas.Name ?? string.Empty),
            ("condensation", Locale.GetString(gas.CondensationEnabled ? "enabled" : "disabled")),
            ("vaporization-enthalpy", QuantityFormat.Format(gas.MolarEnthalpyOfVaporization, "J/mol")),
            ("liquid-id", gas.LiquidId.ToString(CultureInfo.InvariantCulture)),
            ("diffusion-coefficient", QuantityFormat.Format(gas.DiffusionCoefficient, "m²/s", 3)),
            ("molar-mass", QuantityFormat.Format(gas.MolarMass, "kg/mol", 6)));
    }

    /// <summary>
    ///     Draws the inject-by-coordinates form. It injects into the selected chunk node.
    /// </summary>
    private void RenderProjectInjectionControls(bool inspecting)
    {
        var injectionChunk = GetSelectedChunkPosition() is { } chosen && _liveChunkPositions.Contains(chosen)
            ? chosen
            : (Int3?)null;

        bool inputChanged = false;
        ImGui.BeginDisabled(inspecting);
        if (_selectedCell.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_selectedCell.Value.Chunk.Position, out var selectedChunk) &&
            selectedChunk.Identity == _selectedCell.Value.Chunk)
        {
            if (ImGui.Button(Locale.GetString("use-selected-cell")))
            {
                var coordinates = selectedChunk.GetCoordinates(_selectedCell.Value.LocalIndex);
                SelectChunk(selectedChunk.ChunkPosition);
                injectionChunk = selectedChunk.ChunkPosition;
                _document.Chunk.InjectionX = coordinates.X;
                _document.Chunk.InjectionY = coordinates.Y;
                _document.Chunk.InjectionZ = coordinates.Z;
                inputChanged = true;
            }
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt(Locale.GetString("injection-voxel-x"), ref _document.Chunk.InjectionX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt(Locale.GetString("injection-voxel-y"), ref _document.Chunk.InjectionY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt(Locale.GetString("injection-voxel-z"), ref _document.Chunk.InjectionZ);
        ImGui.EndDisabled();

        if (ImGui.Button(Locale.GetString("move-camera-to-voxel"), new Vector2(180, 0)) &&
            injectionChunk.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(injectionChunk.Value, out var cameraChunk))
        {
            MoveCameraToVoxel(cameraChunk, _document.Chunk.InjectionX, _document.Chunk.InjectionY, _document.Chunk.InjectionZ);
        }

        bool canInject = Gases.Count > 0;
        if (canInject)
        {
            _injectionGasId = Math.Clamp(_injectionGasId, 0, Gases.Count - 1);
            ImGui.BeginDisabled(inspecting);
            if (ImGui.BeginCombo(Locale.GetString("injection-gas"), FormatGas(_injectionGasId)))
            {
                for (int gasId = 0; gasId < Gases.Count; gasId++)
                {
                    bool selected = gasId == _injectionGasId;
                    if (ImGui.Selectable(FormatGas(gasId), selected))
                    {
                        _injectionGasId = gasId;
                        inputChanged = true;
                    }

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.EndDisabled();
        }
        else
        {
            ImGui.TextDisabled(Locale.GetString("no-gas-registered"));
        }

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputFloat(Locale.GetString("injection-amount"), ref _injectionMoles);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputFloat(Locale.GetString("injection-temperature"), ref _injectionTemperature);

        ImGui.BeginDisabled(!canInject || !injectionChunk.HasValue);
        if (ImGui.Button(Locale.GetString("inject-gas"), new Vector2(120, 0)) &&
            injectionChunk.HasValue)
        {
            InjectProjectGas(
                new AtmosChunkHandle(injectionChunk.Value),
                _document.Chunk.InjectionX,
                _document.Chunk.InjectionY,
                _document.Chunk.InjectionZ,
                _injectionGasId,
                _injectionMoles,
                _injectionTemperature);
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        if (inputChanged)
            _document.InjectionFeedback = null;

        DrawFeedback(_document.InjectionFeedback);
    }

    private void ClearConfigurationDrafts()
    {
        _document.ConfigFloatDrafts.Clear();
        _document.ConfigIntDrafts.Clear();
        _document.ConfigFieldErrors.Clear();
    }
}