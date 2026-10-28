using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly Dictionary<string, string> _configFieldErrors = [];

    private readonly Dictionary<string, float> _configFloatDrafts = [];
    private readonly Dictionary<string, int> _configIntDrafts = [];

    private FeedbackMessage? _chunkFeedback;
    private AtmosChunkHandle? _chunkPendingRemoval;
    private bool _closeProjectModalOpen;
    private FeedbackMessage? _configurationFeedback;
    private string? _createProjectError;
    private bool _createProjectModalOpen;
    private FeedbackMessage? _gasRegistryFeedback;
    private bool _includeDefaultGasesDraft = true;

    private bool _igniteOnCellClick;
    private float _ignitionTemperature = 2000f;
    private float _ignitionVolume = 1e-3f;

    // The selection the ignition controls last saw, so only a change of selection counts as a click, and the
    // frame they were last drawn on, so a selection that changed while they were hidden doesn't count either.
    private (Int3 Chunk, int X, int Y, int Z)? _lastIgnitionSelection;
    private int _lastIgnitionControlsFrame = -1;

    private Int3? _injectionChunkPosition;
    private FeedbackMessage? _injectionFeedback;
    private int _injectionGasId;
    private float _injectionMoles = 1f;
    private float _injectionTemperature = AtmosPhysicalConstants.RoomTemperature;
    private int _injectionX;
    private int _injectionY;
    private int _injectionZ;
    private int _newChunkRoomId = 1;

    private int _newChunkX;
    private int _newChunkY;
    private int _newChunkZ;
    private float _newGasBoilingPoint;
    private bool _newGasCondensationEnabled;
    private float _newGasDiffusionCoefficient = AtmosConfigDefaults.DefaultDiffusionCoefficient;
    private float _newGasEnthalpyOfVaporization;
    private float _newGasMolarMass;
    private FeedbackMessage? _newGasFeedback;
    private int _newGasLiquidId = -1;
    private float _newGasMolarHeatCapacityAtConstantVolume =
        AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;

    private string _newGasName = "New Gas";
    private int _projectChunkDepthDraft = 1;
    private int _projectChunkHeightDraft = AtmosChunkConstants.DefaultHeight;
    private int _projectChunkWidthDraft = AtmosChunkConstants.DefaultWidth;

    private string _projectNameDraft = "Untitled Simulation";
    private bool _removeChunkModalOpen;
    private bool _requestOpenCloseProject;

    private bool _requestOpenCreateProject;
    private bool _requestRemoveChunk;
    private AtmosTransportModel _projectTransportModelDraft;
    private bool _requestResetConfiguration;
    private bool _resetConfigurationModalOpen;

    /// <summary>
    ///     Tick reached by the last Step press. Cleared by Run and by anything else that moves the world away from that
    ///     tick, so the "Stepped to" line never describes a state the user is no longer looking at.
    /// </summary>
    private int? _steppedToTick;

    private Int3? _toolChunkPosition;
    private int _toolClassificationDraft;
    private int _voxelClassificationDraft;
    private FeedbackMessage? _voxelFeedback;

    private void RequestCreateProject()
    {
        _projectNameDraft = _session.World == null ? Locale.GetString("common-untitled-simulation") : Locale.GetString("project-name-copy", ("projectName", _session.ProjectName));
        _projectChunkWidthDraft = _session.ChunkDimensions.X > 0
            ? _session.ChunkDimensions.X
            : AtmosChunkConstants.DefaultWidth;

        _projectChunkHeightDraft = _session.ChunkDimensions.Y > 0
            ? _session.ChunkDimensions.Y
            : AtmosChunkConstants.DefaultHeight;

        _projectChunkDepthDraft = _session.ChunkDimensions.Z > 0 ? _session.ChunkDimensions.Z : 1;
        _includeDefaultGasesDraft = true;
        var currentModel = _session.World == null
            ? null
            : AtmosWorldSolverPipeline.DetectTransportModel(_session.World.Solvers.Steps);

        _projectTransportModelDraft = currentModel ?? AtmosTransportModel.Euler;
        _createProjectError = null;
        _requestOpenCreateProject = true;
        _createProjectModalOpen = true;
    }

    private void RequestCloseProject()
    {
        if (_session.World == null)
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

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.InputText(Locale.GetString("project-name"), ref _projectNameDraft, 128);
        ImGui.Separator();
        ImGui.Text(Locale.GetString("chunk-dimensions"));
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("chunk-dimensions-description"));

        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("width-project-chunk"), ref _projectChunkWidthDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("height-project-chunk"), ref _projectChunkHeightDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt(Locale.GetString("depth-project-chunk"), ref _projectChunkDepthDraft);

        ImGui.Separator();
        RenderTransportModelCombo();

        ImGui.Separator();
        ImGui.Checkbox(Locale.GetString("include-oxygen-and-nitrogen"), ref _includeDefaultGasesDraft);
        ImGuiExtensions.QuestionTooltip(
            _includeDefaultGasesDraft
                ? Locale.GetString("default-gases-appended-description")
                : Locale.GetString("blank-gas-registry-description"));

        if (_session.World != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                ViewerTheme.Caution,
                _session.Branches?.BranchCount > 1
                    ? Locale.GetString("create-project-discard-branches", ("branchCount", _session.Branches.BranchCount))
                    : Locale.GetString("create-project-close-current"));
        }

        if (!string.IsNullOrWhiteSpace(_createProjectError))
        {
            ImGui.Spacing();
            ImGui.TextColored(ViewerTheme.Error, _createProjectError);
        }

        ImGui.Spacing();
        if (ImGui.Button(Locale.GetString("create"), new Vector2(120, 0)))
        {
            try
            {
                CreateSimulationProject(
                    _projectNameDraft,
                    _projectChunkWidthDraft,
                    _projectChunkHeightDraft,
                    _projectChunkDepthDraft,
                    _includeDefaultGasesDraft,
                    _projectTransportModelDraft);

                _createProjectModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                _createProjectError = exception.Message;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _createProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }
    }

    private static readonly AtmosTransportModel[] TransportModelValues =
        [AtmosTransportModel.Legacy, AtmosTransportModel.Euler];

    private static readonly string[] TransportModelLabels = [Locale.GetString("legacy-pressure-relaxation"), Locale.GetString("euler-compressible-flow")];

    /// <summary>
    ///     Picks the preset that fills the stage list of a new project. The Solver Pipeline panel changes the stages
    ///     afterwards.
    /// </summary>
    private void RenderTransportModelCombo()
    {
        int index = Array.IndexOf(TransportModelValues, _projectTransportModelDraft);
        if (ImGui.Combo(Locale.GetString("transport-model"), ref index, TransportModelLabels, TransportModelLabels.Length) && index >= 0)
            _projectTransportModelDraft = TransportModelValues[index];

        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("transport-model-description"));
    }

    private void DrawCloseProjectModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(Locale.GetString("close-simulation-project"), ref _requestOpenCloseProject);

        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(Locale.GetString("close-simulation-project"), ref _closeProjectModalOpen);
        if (!modal.IsVisible)
            return;

        string branchWarning = _session.Branches?.BranchCount > 1
            ? Locale.GetString("close-project-branches-warning", ("branchCount", _session.Branches.BranchCount))
            : string.Empty;

        ImGui.TextWrapped(
            Locale.GetString("close-project-confirmation", ("projectName", _session.ProjectName), ("branchWarning", branchWarning)));

        ImGui.Spacing();
        if (ImGui.Button(Locale.GetString("close-project"), new Vector2(140, 0)))
        {
            _session.CloseProject();
            RequestNextFrame();
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
        _chunkPendingRemoval = chunk;
        _requestRemoveChunk = true;
        _removeChunkModalOpen = true;
    }

    private void DrawRemoveChunkModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(Locale.GetString("remove-chunk-tools"), ref _requestRemoveChunk);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(Locale.GetString("remove-chunk-tools"), ref _removeChunkModalOpen);
        if (!modal.IsVisible)
            return;

        if (_simulation == null ||
            _chunkPendingRemoval is not { } chunk ||
            !_liveChunkPositions.Contains(chunk.Position))
        {
            ImGui.TextWrapped(Locale.GetString("selected-chunk-no-longer-exists"));
            if (ImGui.Button(Locale.GetString("common-close"), new Vector2(120, 0)))
            {
                _removeChunkModalOpen = false;
                _chunkPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            return;
        }

        ImGui.TextWrapped(
            Locale.GetString("remove-chunk-confirmation", ("chunkPosition", FormatChunkPosition(chunk.Position))));

        ImGui.Spacing();
        ImGui.BeginDisabled(_session.Timeline?.IsInspecting == true);
        if (ImGui.Button(Locale.GetString("remove-chunk"), new Vector2(140, 0)))
        {
            RemoveProjectChunk(chunk);
            _chunkPendingRemoval = null;
            _removeChunkModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _chunkPendingRemoval = null;
            _removeChunkModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private void RequestResetConfiguration()
    {
        _requestResetConfiguration = true;
        _resetConfigurationModalOpen = true;
    }

    private void DrawResetConfigurationModal()
    {
        ImGuiExtensions.OpenPopupWhenRequested(Locale.GetString("reset-atmosphere-settings-configuration"), ref _requestResetConfiguration);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(Locale.GetString("reset-atmosphere-settings-configuration"), ref _resetConfigurationModalOpen);
        if (!modal.IsVisible)
            return;

        ImGui.TextWrapped(
            Locale.GetString("reset-atmosphere-settings-confirmation"));

        ImGui.Spacing();
        ImGui.BeginDisabled(_session.Config == null || _session.Timeline?.IsInspecting == true);
        if (ImGui.Button(Locale.GetString("reset"), new Vector2(120, 0)))
        {
            ResetConfigurationValues();
            _resetConfigurationModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _resetConfigurationModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    /// <summary>
    ///     Draws the shared read-only notice while the Timeline is inspecting history.
    /// </summary>
    /// <returns><see langword="true" /> when the caller should disable its editing controls.</returns>
    private bool DrawInspectionNotice()
    {
        if (_session.Timeline is not { IsInspecting: true } timeline)
            return false;

        ImGuiExtensions.ReadOnlyNotice(
            Locale.GetString("read-only-inspection-notice", ("tick", timeline.Position.Tick)));

        return true;
    }

    private void RenderSolutionPanel()
    {
        if (!_showSolutionPanel || _simulation == null || _session.Config == null)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Solution##solution",
            ref _showSolutionPanel,
            new Vector2(10, 40),
            new Vector2(300, 290));

        if (!window.IsVisible)
            return;

        bool inspecting = DrawInspectionNotice();
        int currentTick = _session.World?.TickCount ?? 0;
        if (inspecting || !_isPaused || _steppedToTick != currentTick)
            _steppedToTick = null;

        ImGui.TextUnformatted(_session.ProjectName ?? Locale.GetString("common-untitled-simulation"));
        if (ImGui.BeginTable(
                "ProjectStatus##solution",
                2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled(Locale.GetString("common-state-heading"));
            if (inspecting)
                ImGui.TextUnformatted(Locale.GetString("common-inspecting-history"));
            else if (_isPaused)
                ImGui.TextUnformatted(Locale.GetString("paused"));
            else
                ImGui.TextColored(ViewerTheme.Running, Locale.GetString("running"));

            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField(Locale.GetString("active-simulation"), GetSimulationName(_simulation.Id));
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField(Locale.GetString("common-simulations-heading"), (_session.World?.Simulations.Count ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField(Locale.GetString("common-chunks-heading"), _simulation.ChunkCount.ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField(Locale.GetString("gases"), _session.Config.GasRegistry.Count.ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField(Locale.GetString("current-tick"), currentTick.ToString());
            ImGui.EndTable();
        }

        ImGui.SeparatorText(Locale.GetString("simulation-controls"));

        // Step ticks the world directly, which must not happen to inspected history; the Timeline has its own
        // transport for that.
        ImGui.BeginDisabled(inspecting);
        if (_isPaused)
        {
            if (ImGui.Button(Locale.GetString("run"), new Vector2(90, 0)))
            {
                _isPaused = false;
                _steppedToTick = null;
            }
        }
        else if (ImGui.Button(Locale.GetString("common-pause"), new Vector2(90, 0)))
        {
            _isPaused = true;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!_isPaused);
        if (ImGui.Button(Locale.GetString("step"), new Vector2(90, 0)))
        {
            _steppedToTick = _session.Tick();
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        if (_steppedToTick is { } steppedTick)
            ImGui.TextDisabled(Locale.GetString("stepped-to-tick", ("tick", steppedTick)));

        RenderSolutionDetails();
        RenderFlowDiagnostics();

        ImGui.Separator();
        if (ImGui.Button(Locale.GetString("close-project"), new Vector2(130, 0)))
            RequestCloseProject();
    }

    private bool TryGetDetailChunk(out AtmosChunkSnapshot snapshot, out string source)
    {
        if (_focusedChunk is { } focused && TryGetUsableSnapshot(focused.Position, out snapshot))
        {
            source = Locale.GetString("focused-chunk");
            return true;
        }

        if (_selectedCell is { } selected && TryGetUsableSnapshot(selected.Chunk.Position, out snapshot))
        {
            source = Locale.GetString("selected-voxel-chunk");
            return true;
        }

        if (_toolChunkPosition is { } tool && TryGetUsableSnapshot(tool, out snapshot))
        {
            source = Locale.GetString("selected-in-tools");
            return true;
        }

        foreach (var handle in _liveChunkHandles)
        {
            if (TryGetUsableSnapshot(handle.Position, out snapshot))
            {
                source = Locale.GetString("first-chunk");
                return true;
            }
        }

        snapshot = default;
        source = string.Empty;
        return false;
    }

    private bool TryGetUsableSnapshot(Int3 position, out AtmosChunkSnapshot snapshot)
    {
        return _snapshotCache.TryGetValue(position, out snapshot) && snapshot.Dimensions.X > 0;
    }

    private void SetProjectMessage(string message, bool isError)
    {
        WriteMessage(isError ? WorkbenchLogLevel.Error : WorkbenchLogLevel.Info, "Simulation", message);
    }

    private static void DrawFeedback(FeedbackMessage? feedback)
    {
        ImGuiExtensions.Feedback(feedback?.Message, feedback?.IsError == true);
    }

    private void RenderProjectChunkControls(bool inspecting)
    {
        ImGui.TextDisabled(Locale.GetString("chunk-size", ("x", _session.ChunkDimensions.X), ("y", _session.ChunkDimensions.Y), ("z", _session.ChunkDimensions.Z)));

        if (_liveChunkHandles.Count == 0)
        {
            if (!inspecting)
                _toolChunkPosition = null;

            ImGui.TextDisabled(Locale.GetString("no-chunks"));
        }
        else
        {
            // Keep a selection that is missing from an inspected tick, so returning to the head restores it.
            if (!inspecting &&
                (!_toolChunkPosition.HasValue || !_liveChunkPositions.Contains(_toolChunkPosition.Value)))
                _toolChunkPosition = _liveChunkHandles[0].Position;

            RenderToolChunkTable(inspecting);
            RenderSelectedChunkActions(inspecting);
        }

        ImGui.SeparatorText(Locale.GetString("new-chunk"));
        bool newChunkChanged = false;
        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("new-chunk-x"), ref _newChunkX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("new-chunk-y"), ref _newChunkY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("new-chunk-z"), ref _newChunkZ);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("chunk-grid-position-tooltip"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt(Locale.GetString("initial-room-id"), ref _newChunkRoomId);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-chunk-classification-tooltip"));

        if (newChunkChanged)
            _chunkFeedback = null;

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("add-chunk"), new Vector2(120, 0)))
            AddProjectChunk(new Int3(_newChunkX, _newChunkY, _newChunkZ), _newChunkRoomId);

        ImGui.EndDisabled();
        DrawFeedback(_chunkFeedback);
    }

    private void RenderToolChunkTable(bool inspecting)
    {
        const int maximumVisibleRows = 8;
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.ScrollY;

        // Header plus up to eight rows; longer chunk lists scroll inside the table instead of pushing the panel.
        float rowHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2f + 1f;
        int visibleRows = Math.Min(_liveChunkHandles.Count, maximumVisibleRows) + 1;
        var outerSize = new Vector2(0f, rowHeight * visibleRows + 4f);
        if (!ImGui.BeginTable("ToolChunks##tools", 2, flags, outerSize))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Locale.GetString("chunk-column"));
        ImGui.TableSetupColumn(Locale.GetString("common-state-heading"), ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableHeadersRow();

        AtmosChunkHandle? chunkToSeal = null;
        AtmosChunkHandle? chunkToUnsleep = null;
        foreach (var handle in _liveChunkHandles)
        {
            var position = handle.Position;
            ImGui.PushID($"chunk-{position.X}-{position.Y}-{position.Z}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable(
                    FormatChunkPosition(position),
                    _toolChunkPosition == position,
                    ImGuiSelectableFlags.SpanAllColumns))
                SelectToolChunk(position);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                SelectToolChunk(position);

            if (ImGui.BeginPopupContextItem(Locale.GetString("chunk-actions")))
            {
                if (ImGui.MenuItem(Locale.GetString("move-camera"), null, false, _drawData?.Chunks.ContainsKey(position) == true))
                    MoveCameraToToolChunk(position);

                if (ImGui.MenuItem(Locale.GetString("seal-with-walls"), null, false, !inspecting))
                    chunkToSeal = handle;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Locale.GetString("seal-with-walls-tooltip"));

                if (ImGui.MenuItem(Locale.GetString("unsleep"), null, false, !inspecting))
                    chunkToUnsleep = handle;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Locale.GetString("unsleep-tooltip"));

                if (ImGui.MenuItem(Locale.GetString("remove-chunk-menu"), null, false, !inspecting))
                    RequestRemoveChunk(handle);

                ImGui.EndPopup();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(
                TryGetUsableSnapshot(position, out var snapshot)
                    ? snapshot.IsAwake ? Locale.GetString("awake") : Locale.GetString("sleeping")
                    : Locale.GetString("unknown"));

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (chunkToSeal.HasValue)
            SealProjectChunk(chunkToSeal.Value);
        else if (chunkToUnsleep.HasValue)
            UnsleepProjectChunk(chunkToUnsleep.Value);
    }

    private void SelectToolChunk(Int3 position)
    {
        if (_toolChunkPosition == position)
            return;

        _toolChunkPosition = position;
        _chunkFeedback = null;
    }

    private void MoveCameraToToolChunk(Int3 position)
    {
        if (_drawData != null && _drawData.Chunks.TryGetValue(position, out var chunkData))
            MoveCameraToChunk(chunkData);
    }

    private void RenderSelectedChunkActions(bool inspecting)
    {
        if (_toolChunkPosition is not { } position || !_liveChunkPositions.Contains(position))
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
        if (ImGui.InputInt(Locale.GetString("classification-chunk-fill"), ref _toolClassificationDraft))
            _chunkFeedback = null;

        ImGui.EndDisabled();
        ImGui.TextDisabled(Locale.GetString("common-classification-values-description"));

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("fill-selected-chunk")))
            FillProjectChunk(chunk, _toolClassificationDraft);

        ImGui.EndDisabled();
    }

    private void RenderProjectGasControls(bool inspecting)
    {
        if (_session.Config!.GasRegistry.Count == 0)
            ImGui.TextDisabled(Locale.GetString("blank-gas-registry"));
        else
            RenderGasRegistryTable(inspecting);

        DrawFeedback(_gasRegistryFeedback);

        ImGui.SeparatorText(Locale.GetString("add-gas-definition"));
        bool formChanged = false;
        ImGui.BeginDisabled(inspecting);
        formChanged |= ImGui.InputText(Locale.GetString("new-gas-name"), ref _newGasName, 64);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(
            Locale.GetString("new-gas-molar-cv"),
            ref _newGasMolarHeatCapacityAtConstantVolume);

        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(Locale.GetString("new-gas-boiling-point"), ref _newGasBoilingPoint);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("new-gas-boiling-point-tooltip"));

        ImGui.BeginDisabled(inspecting);
        formChanged |= ImGui.Checkbox(Locale.GetString("new-gas-condensation-enabled"), ref _newGasCondensationEnabled);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(
            Locale.GetString("new-gas-vaporization-enthalpy"),
            ref _newGasEnthalpyOfVaporization);

        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputInt(Locale.GetString("new-gas-liquid-id"), ref _newGasLiquidId);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("new-gas-liquid-id-tooltip"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(Locale.GetString("new-gas-diffusion-coefficient"), ref _newGasDiffusionCoefficient);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("new-gas-diffusion-coefficient-tooltip"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(Locale.GetString("new-gas-molar-mass"), ref _newGasMolarMass, 0f, 0f, "%.6f");
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("new-gas-molar-mass-tooltip"));

        if (formChanged)
            _newGasFeedback = null;

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("add-gas"), new Vector2(120, 0)))
        {
            AddProjectGas(
                new GasProperties
                {
                    Name = _newGasName,
                    MolarHeatCapacityAtConstantVolume = _newGasMolarHeatCapacityAtConstantVolume,
                    BoilingPoint = _newGasBoilingPoint,
                    CondensationEnabled = _newGasCondensationEnabled,
                    MolarEnthalpyOfVaporization = _newGasEnthalpyOfVaporization,
                    LiquidId = _newGasLiquidId,
                    DiffusionCoefficient = _newGasDiffusionCoefficient,
                    MolarMass = _newGasMolarMass
                });
        }

        ImGui.EndDisabled();
        DrawFeedback(_newGasFeedback);
    }

    private void RenderGasRegistryTable(bool inspecting)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingFixedFit;

        var registry = _session.Config!.GasRegistry;
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
        return Locale.GetString("gas-details",
                   ("name", gas.Name ?? string.Empty),
                   ("condensation", Locale.GetString(gas.CondensationEnabled ? "enabled" : "disabled")),
                   ("vaporization-enthalpy", QuantityFormat.Format(gas.MolarEnthalpyOfVaporization, "J/mol")),
                   ("liquid-id", gas.LiquidId.ToString(CultureInfo.InvariantCulture)),
                   ("diffusion-coefficient", QuantityFormat.Format(gas.DiffusionCoefficient, "m²/s", 3)),
                   ("molar-mass", QuantityFormat.Format(gas.MolarMass, "kg/mol", 6)));
    }

    private void RenderProjectInjectionControls(bool inspecting)
    {
        if (_liveChunkHandles.Count == 0)
        {
            _injectionChunkPosition = null;
            ImGui.TextDisabled(Locale.GetString("add-chunk-before-injecting"));
            return;
        }

        if (!_injectionChunkPosition.HasValue ||
            !_liveChunkPositions.Contains(_injectionChunkPosition.Value))
            _injectionChunkPosition = _liveChunkHandles[0].Position;

        bool inputChanged = false;
        ImGui.BeginDisabled(inspecting);
        string chunkLabel = FormatChunkPosition(_injectionChunkPosition.Value);
        if (ImGui.BeginCombo(Locale.GetString("injection-chunk"), chunkLabel))
        {
            foreach (var handle in _liveChunkHandles)
            {
                bool selected = handle.Position == _injectionChunkPosition.Value;
                if (ImGui.Selectable(FormatChunkPosition(handle.Position), selected))
                {
                    _injectionChunkPosition = handle.Position;
                    inputChanged = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (_selectedCell.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_selectedCell.Value.Chunk.Position, out var selectedChunk) &&
            selectedChunk.Identity == _selectedCell.Value.Chunk)
        {
            if (ImGui.Button(Locale.GetString("use-selected-cell")))
            {
                var coordinates = selectedChunk.GetCoordinates(_selectedCell.Value.LocalIndex);
                _injectionChunkPosition = selectedChunk.ChunkPosition;
                _injectionX = coordinates.X;
                _injectionY = coordinates.Y;
                _injectionZ = coordinates.Z;
                inputChanged = true;
            }
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt(Locale.GetString("injection-voxel-x"), ref _injectionX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt(Locale.GetString("injection-voxel-y"), ref _injectionY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt(Locale.GetString("injection-voxel-z"), ref _injectionZ);
        ImGui.EndDisabled();

        if (ImGui.Button(Locale.GetString("move-camera-to-voxel"), new Vector2(180, 0)) &&
            _injectionChunkPosition.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_injectionChunkPosition.Value, out var cameraChunk))
        {
            MoveCameraToVoxel(cameraChunk, _injectionX, _injectionY, _injectionZ);
        }

        bool canInject = _session.Config!.GasRegistry.Count > 0;
        if (canInject)
        {
            _injectionGasId = Math.Clamp(_injectionGasId, 0, _session.Config.GasRegistry.Count - 1);
            ImGui.BeginDisabled(inspecting);
            if (ImGui.BeginCombo(Locale.GetString("injection-gas"), FormatGas(_injectionGasId)))
            {
                for (int gasId = 0; gasId < _session.Config.GasRegistry.Count; gasId++)
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

        ImGui.BeginDisabled(!canInject);
        if (ImGui.Button(Locale.GetString("inject-gas"), new Vector2(120, 0)) &&
            _injectionChunkPosition.HasValue)
        {
            InjectProjectGas(
                new AtmosChunkHandle(_injectionChunkPosition.Value),
                _injectionX,
                _injectionY,
                _injectionZ,
                _injectionGasId,
                _injectionMoles,
                _injectionTemperature);
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        inputChanged |= RenderProjectIgnitionControls(inspecting);

        if (inputChanged)
            _injectionFeedback = null;

        DrawFeedback(_injectionFeedback);
    }

    /// <summary>
    ///     Draws the ignition source controls under the gas injection ones. Ignition mode is a toggle: while it is
    ///     on, clicking a cell in the viewport ignites that cell for one tick. Nothing is ignited by the controls
    ///     themselves, so there is no default voxel that can be hit by accident.
    /// </summary>
    /// <returns><see langword="true" /> when one of the ignition inputs changed.</returns>
    private bool RenderProjectIgnitionControls(bool inspecting)
    {
        bool inputChanged = false;
        ImGui.SeparatorText(Locale.GetString("ignition-source"));

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputFloat(Locale.GetString("ignition-temperature"), ref _ignitionTemperature);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputFloat(Locale.GetString("ignition-volume"), ref _ignitionVolume, 0f, 0f, "%g");
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("ignition-source-tooltip"));

        // The cell the user has selected in the viewport, if it can be resolved to a chunk and voxel.
        (Int3 Chunk, int X, int Y, int Z)? selection = null;
        if (_selectedCell.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_selectedCell.Value.Chunk.Position, out var selectedChunk) &&
            selectedChunk.Identity == _selectedCell.Value.Chunk)
        {
            var coordinates = selectedChunk.GetCoordinates(_selectedCell.Value.LocalIndex);
            selection = (selectedChunk.ChunkPosition, coordinates.X, coordinates.Y, coordinates.Z);
        }

        int frame = ImGui.GetFrameCount();
        bool drawnLastFrame = _lastIgnitionControlsFrame == frame - 1;
        _lastIgnitionControlsFrame = frame;

        ImGui.BeginDisabled(inspecting);
        bool modeToggled = ImGui.Checkbox(Locale.GetString("ignition-mode"), ref _igniteOnCellClick);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("ignition-mode-tooltip"));

        // Only a change of selection while the controls were visible on the previous frame is a click.
        // Turning the mode on, the first draw, and a hidden panel only move the baseline, so none of them can
        // ignite whatever happens to be selected (or the first voxel of the first chunk).
        bool clicked =
            _igniteOnCellClick &&
            !modeToggled &&
            drawnLastFrame &&
            !inspecting &&
            selection.HasValue &&
            !Nullable.Equals(selection, _lastIgnitionSelection);

        _lastIgnitionSelection = selection;
        if (clicked)
        {
            var target = selection!.Value;
            IgniteVoxelFromInputs(target.Chunk, target.X, target.Y, target.Z);
        }

        if (_igniteOnCellClick)
            ImGui.TextColored(ViewerTheme.Caution, Locale.GetString("ignition-mode-on"));

        return inputChanged;
    }

    private void IgniteVoxelFromInputs(Int3 chunkPosition, int x, int y, int z)
    {
        if (!(_ignitionTemperature > 0f) || !(_ignitionVolume > 0f))
        {
            _injectionFeedback = Report(Locale.GetString("ignition-positive-temperature-volume-required"), true);
            return;
        }

        IgniteProjectSource(
            new AtmosChunkHandle(chunkPosition),
            x,
            y,
            z,
            _ignitionTemperature,
            _ignitionVolume);
    }

    private void ClearConfigurationDrafts()
    {
        _configFloatDrafts.Clear();
        _configIntDrafts.Clear();
        _configFieldErrors.Clear();
    }
}