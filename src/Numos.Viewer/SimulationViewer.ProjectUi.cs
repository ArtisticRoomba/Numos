using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.Viewer.Ui;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string SealChunkTooltip = "Replace the chunk's simulated outer faces with solid voxels.";
    private const string UnsleepChunkTooltip = "Wake the chunk so it participates in subsequent simulation ticks.";
    private const string RemoveChunkTooltip =
        "Remove the chunk, its gas, and any portal or dock links that touch it.";
    private const string ClassificationFormatHelp = "0 unassigned, -2 solid, -1 void, positive values are room IDs.";
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
        _projectNameDraft = _world == null ? "Untitled Simulation" : $"{_projectName} Copy";
        _projectChunkWidthDraft = _chunkDimensions.X > 0
            ? _chunkDimensions.X
            : AtmosChunkConstants.DefaultWidth;

        _projectChunkHeightDraft = _chunkDimensions.Y > 0
            ? _chunkDimensions.Y
            : AtmosChunkConstants.DefaultHeight;

        _projectChunkDepthDraft = _chunkDimensions.Z > 0 ? _chunkDimensions.Z : 1;
        _includeDefaultGasesDraft = true;
        _createProjectError = null;
        _requestOpenCreateProject = true;
        _createProjectModalOpen = true;
    }

    private void RequestCloseProject()
    {
        if (_world == null)
            return;

        _requestOpenCloseProject = true;
        _closeProjectModalOpen = true;
    }

    private void DrawCreateProjectModal()
    {
        const string popupId = "Create Simulation";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestOpenCreateProject);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + viewport.Size * 0.5f,
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));

        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _createProjectModalOpen);
        if (!modal.IsVisible)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.InputText("Project name", ref _projectNameDraft, 128);
        ImGui.Separator();
        ImGui.Text("Chunk dimensions");
        ImGuiExtensions.QuestionTooltip(
            "Every chunk in this project uses these fixed dimensions, counted in voxels along X, Y, and Z.");

        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Width##project-chunk", ref _projectChunkWidthDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Height##project-chunk", ref _projectChunkHeightDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Depth##project-chunk", ref _projectChunkDepthDraft);

        ImGui.Separator();
        ImGui.Checkbox("Include Oxygen and Nitrogen", ref _includeDefaultGasesDraft);
        ImGuiExtensions.QuestionTooltip(
            _includeDefaultGasesDraft
                ? "The default gas definitions will be appended to the new project."
                : "The project will start with a blank gas registry.");

        if (_world != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                ViewerTheme.Caution,
                _replayBranches?.BranchCount > 1
                    ? $"Creating this project will discard all {_replayBranches.BranchCount} in-memory branches."
                    : "Creating this project will close the current in-memory project.");
        }

        if (!string.IsNullOrWhiteSpace(_createProjectError))
        {
            ImGui.Spacing();
            ImGui.TextColored(ViewerTheme.Error, _createProjectError);
        }

        ImGui.Spacing();
        if (ImGui.Button("Create", new Vector2(120, 0)))
        {
            try
            {
                CreateSimulationProject(
                    _projectNameDraft,
                    _projectChunkWidthDraft,
                    _projectChunkHeightDraft,
                    _projectChunkDepthDraft,
                    _includeDefaultGasesDraft);

                _createProjectModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                _createProjectError = exception.Message;
                WriteException("Could not create the simulation", exception);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _createProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawCloseProjectModal()
    {
        const string popupId = "Close Simulation Project?";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestOpenCloseProject);

        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _closeProjectModalOpen);
        if (!modal.IsVisible)
            return;

        string branchWarning = _replayBranches?.BranchCount > 1
            ? $" All {_replayBranches.BranchCount} session branches will be discarded; save selected branches individually first."
            : string.Empty;

        ImGui.TextWrapped(
            $"Close '{_projectName}' and dispose its world and simulations? This project only exists in memory.{branchWarning}");

        ImGui.Spacing();
        if (ImGui.Button("Close Project", new Vector2(140, 0)))
        {
            string projectName = _projectName ?? "Untitled Simulation";
            DisposeSimulationProject();
            WriteMessage(ViewerLogLevel.Info, "Simulation", $"Closed project '{projectName}'.");
            _closeProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
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
        const string popupId = "Remove Chunk?##tools";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestRemoveChunk);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _removeChunkModalOpen);
        if (!modal.IsVisible)
            return;

        if (_simulation == null ||
            _chunkPendingRemoval is not { } chunk ||
            !_liveChunkPositions.Contains(chunk.Position))
        {
            ImGui.TextWrapped("The selected chunk no longer exists.");
            if (ImGui.Button("Close", new Vector2(120, 0)))
            {
                _removeChunkModalOpen = false;
                _chunkPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            return;
        }

        ImGui.TextWrapped(
            $"Remove chunk {FormatChunkPosition(chunk.Position)}, its gas, and any portal or dock links that touch it? " +
            "The removal is recorded, so you can branch from an earlier tick in the Timeline to get the chunk back.");

        ImGui.Spacing();
        ImGui.BeginDisabled(_replayTimeline?.IsInspecting == true);
        if (ImGui.Button("Remove Chunk", new Vector2(140, 0)))
        {
            RemoveProjectChunk(chunk);
            _chunkPendingRemoval = null;
            _removeChunkModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
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
        const string popupId = "Reset Atmosphere Settings?##configuration";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestResetConfiguration);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _resetConfigurationModalOpen);
        if (!modal.IsVisible)
            return;

        ImGui.TextWrapped(
            "Replace every AtmosConfig setting with the library defaults? The gas registry is kept. " +
            "This is recorded as one configuration change.");

        ImGui.Spacing();
        ImGui.BeginDisabled(_config == null || _replayTimeline?.IsInspecting == true);
        if (ImGui.Button("Reset", new Vector2(120, 0)))
        {
            ResetConfigurationValues();
            _resetConfigurationModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
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
        if (_replayTimeline is not { IsInspecting: true } timeline)
            return false;

        ImGuiExtensions.ReadOnlyNotice(
            $"Read-only while inspecting tick {timeline.Position.Tick}. " +
            "Use Return to Head or Continue Branch in the Timeline to edit.");

        return true;
    }

    private void RenderSolutionPanel()
    {
        if (!_showSolutionPanel || _simulation == null || _config == null)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Solution##solution",
            ref _showSolutionPanel,
            new Vector2(10, 40),
            new Vector2(300, 290));

        if (!window.IsVisible)
            return;

        bool inspecting = DrawInspectionNotice();
        int currentTick = _world?.TickCount ?? 0;
        if (inspecting || !_isPaused || _steppedToTick != currentTick)
            _steppedToTick = null;

        ImGui.TextUnformatted(_projectName ?? "Untitled Simulation");
        if (ImGui.BeginTable(
                "ProjectStatus##solution",
                2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled("STATE");
            if (inspecting)
                ImGui.TextUnformatted("Inspecting history");
            else if (_isPaused)
                ImGui.TextUnformatted("Paused");
            else
                ImGui.TextColored(ViewerTheme.Running, "Running");

            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("ACTIVE SIMULATION", GetSimulationName(_simulation.Id));
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("SIMULATIONS", (_world?.Simulations.Count ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("CHUNKS", _simulation.ChunkCount.ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("GASES", _config.GasRegistry.Count.ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("CURRENT TICK", currentTick.ToString());
            ImGui.EndTable();
        }

        ImGui.SeparatorText("Simulation controls");

        // Step ticks the world directly, which must not happen to inspected history; the Timeline has its own
        // transport for that.
        ImGui.BeginDisabled(inspecting);
        if (_isPaused)
        {
            if (ImGui.Button("Run", new Vector2(90, 0)))
            {
                _isPaused = false;
                _steppedToTick = null;
            }
        }
        else if (ImGui.Button("Pause", new Vector2(90, 0)))
        {
            _isPaused = true;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!_isPaused);
        if (ImGui.Button("Step", new Vector2(90, 0)))
        {
            _world!.Tick();
            _steppedToTick = _world.TickCount;
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        if (_steppedToTick is { } steppedTick)
            ImGui.TextDisabled($"Stepped to tick {steppedTick}.");

        RenderSolutionDetails();

        ImGui.Separator();
        if (ImGui.Button("Close Project", new Vector2(130, 0)))
            RequestCloseProject();
    }

    private bool TryGetDetailChunk(out AtmosChunkSnapshot snapshot, out string source)
    {
        if (_focusedChunk is { } focused && TryGetUsableSnapshot(focused.Position, out snapshot))
        {
            source = "Focused chunk";
            return true;
        }

        if (_showSliceViewport &&
            _selectedSliceChunkPosition is { } slice &&
            TryGetUsableSnapshot(slice, out snapshot))
        {
            source = "Slice chunk";
            return true;
        }

        if (_toolChunkPosition is { } tool && TryGetUsableSnapshot(tool, out snapshot))
        {
            source = "Selected in Tools";
            return true;
        }

        foreach (var handle in _liveChunkHandles)
        {
            if (TryGetUsableSnapshot(handle.Position, out snapshot))
            {
                source = "First chunk";
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
        WriteMessage(isError ? ViewerLogLevel.Error : ViewerLogLevel.Info, "Simulation", message);
    }

    private static void DrawFeedback(FeedbackMessage? feedback)
    {
        ImGuiExtensions.Feedback(feedback?.Message, feedback?.IsError == true);
    }

    private void RenderProjectChunkControls(bool inspecting)
    {
        ImGui.TextDisabled($"Chunk size: {_chunkDimensions.X} × {_chunkDimensions.Y} × {_chunkDimensions.Z} voxels");

        if (_liveChunkHandles.Count == 0)
        {
            if (!inspecting)
                _toolChunkPosition = null;

            ImGui.TextDisabled("No chunks. Add one at a chunk-grid position.");
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

        ImGui.SeparatorText("New chunk");
        bool newChunkChanged = false;
        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt("X##new-chunk", ref _newChunkX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt("Y##new-chunk", ref _newChunkY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt("Z##new-chunk", ref _newChunkZ);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip("Chunk-grid position, not voxels. Neighbouring chunks differ by one on one axis.");

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        newChunkChanged |= ImGui.InputInt("Initial room ID##new-chunk", ref _newChunkRoomId);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip("Classification written to every voxel of the new chunk. " + ClassificationFormatHelp);

        if (newChunkChanged)
            _chunkFeedback = null;

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button("Add Chunk", new Vector2(120, 0)))
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
        ImGui.TableSetupColumn("CHUNK");
        ImGui.TableSetupColumn("STATE", ImGuiTableColumnFlags.WidthFixed, 80f);
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

            if (ImGui.BeginPopupContextItem("Chunk actions"))
            {
                if (ImGui.MenuItem("Move Camera", null, false, _drawData?.Chunks.ContainsKey(position) == true))
                    MoveCameraToToolChunk(position);

                if (ImGui.MenuItem("Seal With Walls", null, false, !inspecting))
                    chunkToSeal = handle;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(SealChunkTooltip);

                if (ImGui.MenuItem("Unsleep", null, false, !inspecting))
                    chunkToUnsleep = handle;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(UnsleepChunkTooltip);

                if (ImGui.MenuItem("Remove...", null, false, !inspecting))
                    RequestRemoveChunk(handle);

                ImGui.EndPopup();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(
                TryGetUsableSnapshot(position, out var snapshot)
                    ? snapshot.IsAwake ? "Awake" : "Sleeping"
                    : "Unknown");

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
            ImGui.TextDisabled("Select a chunk to act on it.");
            return;
        }

        var chunk = new AtmosChunkHandle(position);
        ImGui.BeginDisabled(_drawData?.Chunks.ContainsKey(position) != true);
        if (ImGui.Button("Move Camera##chunk-action"))
            MoveCameraToToolChunk(position);

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button("Unsleep##chunk-action"))
            UnsleepProjectChunk(chunk);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(UnsleepChunkTooltip);

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button("Seal With Walls##chunk-action"))
            SealProjectChunk(chunk);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(SealChunkTooltip);

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button("Remove Chunk...##chunk-action"))
            RequestRemoveChunk(chunk);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(RemoveChunkTooltip);

        ImGui.Spacing();
        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        if (ImGui.InputInt("Classification##chunk-fill", ref _toolClassificationDraft))
            _chunkFeedback = null;

        ImGui.EndDisabled();
        ImGui.TextDisabled(ClassificationFormatHelp);

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button("Fill Selected Chunk"))
            FillProjectChunk(chunk, _toolClassificationDraft);

        ImGui.EndDisabled();
    }

    private void RenderProjectGasControls(bool inspecting)
    {
        if (_config!.GasRegistry.Count == 0)
            ImGui.TextDisabled("Blank gas registry. Add a gas before injecting.");
        else
            RenderGasRegistryTable(inspecting);

        DrawFeedback(_gasRegistryFeedback);

        ImGui.SeparatorText("Add gas definition");
        bool formChanged = false;
        ImGui.BeginDisabled(inspecting);
        formChanged |= ImGui.InputText("Name##new-gas", ref _newGasName, 64);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(
            "Molar Cv (J/(mol·K))##new-gas",
            ref _newGasMolarHeatCapacityAtConstantVolume);

        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat("Boiling point (K)##new-gas", ref _newGasBoilingPoint);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip("Boiling temperature at the saturation reference pressure in AtmosConfig.");

        ImGui.BeginDisabled(inspecting);
        formChanged |= ImGui.Checkbox("Condensation enabled##new-gas", ref _newGasCondensationEnabled);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat(
            "Vaporization enthalpy (J/mol)##new-gas",
            ref _newGasEnthalpyOfVaporization);

        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputInt("Liquid ID##new-gas", ref _newGasLiquidId);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            "Reserved for a custom liquid integration; the built-in solver ignores it. " +
            "-1 is the Viewer's placeholder for no liquid.");

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        formChanged |= ImGui.InputFloat("Diffusion coefficient##new-gas", ref _newGasDiffusionCoefficient);
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            "Dimensionless fraction of this gas's mole imbalance mixed per tick, from 0 to 1. " +
            "The solver clamps values above 1.");

        if (formChanged)
            _newGasFeedback = null;

        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button("Add Gas", new Vector2(120, 0)))
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
                    DiffusionCoefficient = _newGasDiffusionCoefficient
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

        var registry = _config!.GasRegistry;
        int gasToRemove = -1;
        using (var table = ImGuiExtensions.BeginTable("GasRegistry##configuration", 6, flags))
        {
            if (!table.IsVisible)
                return;

            ImGui.TableSetupColumn("ID");
            ImGui.TableSetupColumn("##colour");
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Cv (J/(mol·K))");
            ImGui.TableSetupColumn("Boiling (K)");
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
                if (ImGui.SmallButton("Remove"))
                    gasToRemove = gasId;

                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip | ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Removal is allowed while no stored chunk gas IDs would be shifted.");

                ImGui.PopID();
            }
        }

        if (gasToRemove >= 0)
            RemoveProjectGas(gasToRemove);
    }

    private static string FormatGasDetails(GasProperties gas)
    {
        return $"{gas.Name}\n" +
               $"Condensation: {(gas.CondensationEnabled ? "enabled" : "disabled")}\n" +
               $"Vaporization enthalpy: {QuantityFormat.Format(gas.MolarEnthalpyOfVaporization, "J/mol")}\n" +
               $"Liquid ID: {gas.LiquidId.ToString(CultureInfo.InvariantCulture)}\n" +
               $"Diffusion coefficient: {QuantityFormat.Format(gas.DiffusionCoefficient, string.Empty, 3)}";
    }

    private void RenderProjectInjectionControls(bool inspecting)
    {
        if (_liveChunkHandles.Count == 0)
        {
            _injectionChunkPosition = null;
            ImGui.TextDisabled("Add a chunk before injecting gas.");
            return;
        }

        if (!_injectionChunkPosition.HasValue ||
            !_liveChunkPositions.Contains(_injectionChunkPosition.Value))
            _injectionChunkPosition = _liveChunkHandles[0].Position;

        bool inputChanged = false;
        ImGui.BeginDisabled(inspecting);
        string chunkLabel = FormatChunkPosition(_injectionChunkPosition.Value);
        if (ImGui.BeginCombo("Chunk##inject", chunkLabel))
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
            if (ImGui.Button("Use Selected Cell"))
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
        inputChanged |= ImGui.InputInt("Voxel X##inject", ref _injectionX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt("Voxel Y##inject", ref _injectionY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputInt("Voxel Z##inject", ref _injectionZ);
        ImGui.EndDisabled();

        if (ImGui.Button("Move Camera to Voxel", new Vector2(180, 0)) &&
            _injectionChunkPosition.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_injectionChunkPosition.Value, out var cameraChunk))
        {
            MoveCameraToVoxel(cameraChunk, _injectionX, _injectionY, _injectionZ);
        }

        bool canInject = _config!.GasRegistry.Count > 0;
        if (canInject)
        {
            _injectionGasId = Math.Clamp(_injectionGasId, 0, _config.GasRegistry.Count - 1);
            ImGui.BeginDisabled(inspecting);
            if (ImGui.BeginCombo("Gas##inject", FormatGas(_injectionGasId)))
            {
                for (int gasId = 0; gasId < _config.GasRegistry.Count; gasId++)
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
            ImGui.TextDisabled("Gas: none registered");
        }

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputFloat("Amount (mol)##inject", ref _injectionMoles);
        ImGui.SetNextItemWidth(NumericInputWidth);
        inputChanged |= ImGui.InputFloat("Temperature (K)##inject", ref _injectionTemperature);

        ImGui.BeginDisabled(!canInject);
        if (ImGui.Button("Inject Gas##inject-action", new Vector2(120, 0)) &&
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

        if (inputChanged)
            _injectionFeedback = null;

        DrawFeedback(_injectionFeedback);
    }

    private void ClearConfigurationDrafts()
    {
        _configFloatDrafts.Clear();
        _configIntDrafts.Clear();
        _configFieldErrors.Clear();
    }

    /// <summary>
    ///     Validation domain for an <c>AtmosConfig</c> field, matching what <c>AtmosConfigSnapshot</c> keeps as-is.
    ///     Values outside the domain are silently replaced or clamped by the snapshot, so the panel rejects them
    ///     instead of recording a value that means something else.
    /// </summary>
    private enum ConfigDomain
    {
        Positive,
        NonNegative,
        UnitInterval
    }
}