using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.Viewer.Ui;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const double StepProgressDisplayDuration = 0.8;
    private bool _closeProjectModalOpen;
    private int _completedStepTick;
    private string? _createProjectError;
    private bool _createProjectModalOpen;
    private bool _includeDefaultGasesDraft = true;

    private Int3? _injectionChunkPosition;
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
    private int _newGasLiquidId = -1;
    private float _newGasMolarHeatCapacityAtConstantVolume =
        AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;

    private string _newGasName = "New Gas";
    private int _projectChunkDepthDraft = 1;
    private int _projectChunkHeightDraft = AtmosChunkConstants.DefaultHeight;
    private int _projectChunkWidthDraft = AtmosChunkConstants.DefaultWidth;

    private string? _projectMessage;
    private bool _projectMessageIsError;

    private string _projectNameDraft = "Untitled Simulation";
    private bool _requestOpenCloseProject;

    private bool _requestOpenCreateProject;
    private double _stepProgressDisplayUntil;
    private Int3? _toolChunkPosition;
    private int _toolClassificationDraft;
    private int _voxelClassificationDraft;

    private void RequestCreateProject()
    {
        _projectNameDraft = _simulation == null ? "Untitled Simulation" : $"{_projectName} Copy";
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
        if (_simulation == null)
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

        ImGui.InputText("Project name", ref _projectNameDraft, 128);
        ImGui.Separator();
        ImGui.Text("Chunk dimensions");
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Width##project-chunk", ref _projectChunkWidthDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Height##project-chunk", ref _projectChunkHeightDraft);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Depth##project-chunk", ref _projectChunkDepthDraft);
        ImGui.TextDisabled("Every chunk in this project uses these fixed dimensions.");

        ImGui.Separator();
        ImGui.Checkbox("Include Oxygen and Nitrogen", ref _includeDefaultGasesDraft);
        ImGui.TextDisabled(
            _includeDefaultGasesDraft
                ? "The default gas definitions will be appended to the new project."
                : "The project will start with a blank gas registry.");

        if (_simulation != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1f, 0.72f, 0.25f, 1f),
                "Creating this project will close the current in-memory project.");
        }

        if (!string.IsNullOrWhiteSpace(_createProjectError))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), _createProjectError);
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

        ImGui.TextWrapped($"Close '{_projectName}' and dispose its simulation? This project only exists in memory.");
        ImGui.Spacing();
        if (ImGui.Button("Close Project", new Vector2(140, 0)))
        {
            DisposeSimulationProject();
            _closeProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _closeProjectModalOpen = false;
            ImGui.CloseCurrentPopup();
        }
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

        ImGui.Text(_projectName ?? "Untitled Simulation");
        ImGui.TextDisabled(
            $"Chunks: {_simulation.ChunkCount}  |  Gases: {_config.GasRegistry.Count}  |  Ticks: {_simulation.TickCount}");

        if (_isPaused)
        {
            if (ImGui.Button("Run", new Vector2(90, 0)))
                _isPaused = false;
        }
        else if (ImGui.Button("Stop", new Vector2(90, 0)))
        {
            _isPaused = true;
        }

        ImGui.SameLine();
        if (_isPaused)
        {
            if (ImGui.Button("Step", new Vector2(90, 0)))
            {
                _simulation.Tick();
                _completedStepTick = _simulation.TickCount;
                _stepProgressDisplayUntil = ImGui.GetTime() + StepProgressDisplayDuration;
            }
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("Step", new Vector2(90, 0));
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Close Project", new Vector2(130, 0)))
            RequestCloseProject();

        RenderSimulationProgress();

        RenderProjectMessage();
        if (ImGui.CollapsingHeader("Simulation Details", ImGuiTreeNodeFlags.DefaultOpen))
            RenderSolutionDetails();
    }

    private void RenderSimulationProgress()
    {
        const float cycleDuration = 1.25f;
        double now = ImGui.GetTime();
        float indet = (float)(-1f * (now % (cycleDuration / cycleDuration)));

        if (!_isPaused)
        {
            ImGui.ProgressBar(indet, new Vector2(-1, 0), "Ticking...");
            return;
        }

        if (now < _stepProgressDisplayUntil)
        {
            ImGui.ProgressBar(0f, new Vector2(-1, 0), $"Step complete - Tick {_completedStepTick}");
            return;
        }

        ImGui.ProgressBar(0f, new Vector2(-1, 0), "Paused");
    }

    private void RenderProjectMessage()
    {
        if (string.IsNullOrWhiteSpace(_projectMessage))
            return;

        ImGui.Spacing();
        ImGui.PushStyleColor(
            ImGuiCol.Text,
            _projectMessageIsError
                ? new Vector4(1f, 0.35f, 0.35f, 1f)
                : new Vector4(0.4f, 0.85f, 0.5f, 1f));

        ImGui.TextWrapped(_projectMessage);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void SetProjectMessage(string message, bool isError)
    {
        _projectMessage = message;
        _projectMessageIsError = isError;
    }

    private void RenderProjectChunkControls()
    {
        ImGui.TextDisabled($"Fixed size: {_chunkDimensions.X} x {_chunkDimensions.Y} x {_chunkDimensions.Z}");
        ImGui.TextDisabled("Right-click a chunk coordinate for options.");

        AtmosChunkHandle? chunkToRemove = null;
        AtmosChunkHandle? chunkToSeal = null;
        foreach (var handle in _liveChunkHandles)
        {
            ImGui.PushID($"chunk-{handle.Position.X}-{handle.Position.Y}-{handle.Position.Z}");
            ImGui.Text(FormatChunkPosition(handle.Position));

            if (ImGui.BeginPopupContextItem("Chunk actions"))
            {
                if (ImGui.MenuItem("Move Camera") &&
                    _drawData != null &&
                    _drawData.Chunks.TryGetValue(handle.Position, out var chunkData))
                    MoveCameraToChunk(chunkData);

                if (ImGui.MenuItem("Seal With Walls"))
                    chunkToSeal = handle;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Replace the chunk's simulated outer faces with solid voxels.");

                if (ImGui.MenuItem("Remove"))
                    chunkToRemove = handle;

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        if (chunkToRemove.HasValue)
            RemoveProjectChunk(chunkToRemove.Value);
        else if (chunkToSeal.HasValue)
            SealProjectChunk(chunkToSeal.Value);

        if (_liveChunkHandles.Count == 0)
            ImGui.TextDisabled("No chunks. Add one at a chunk-grid position.");

        ImGui.Separator();
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("X##new-chunk", ref _newChunkX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Y##new-chunk", ref _newChunkY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Z##new-chunk", ref _newChunkZ);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Initial room ID##new-chunk", ref _newChunkRoomId);
        if (ImGui.Button("Add Chunk", new Vector2(120, 0)))
            AddProjectChunk(new Int3(_newChunkX, _newChunkY, _newChunkZ), _newChunkRoomId);
    }

    private void RenderProjectGasControls()
    {
        int gasToRemove = -1;
        for (int gasId = 0; gasId < _config!.GasRegistry.Count; gasId++)
        {
            var gas = _config.GasRegistry[gasId];
            ImGui.PushID($"gas-{gasId}");
            ImGui.Text($"{gasId}: {gas.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                gasToRemove = gasId;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Removal is allowed while no stored chunk gas IDs would be shifted.");

            ImGui.PopID();
        }

        if (gasToRemove >= 0)
            RemoveProjectGas(gasToRemove);

        if (_config.GasRegistry.Count == 0)
            ImGui.TextDisabled("Blank gas registry. Add a gas before injecting.");

        ImGui.Separator();
        ImGui.Text("Add gas definition");
        ImGui.InputText("Name##new-gas", ref _newGasName, 64);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputFloat("Molar Cv (J/mol-K)##new-gas", ref _newGasMolarHeatCapacityAtConstantVolume);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputFloat("Boiling point (K)##new-gas", ref _newGasBoilingPoint);
        ImGui.Checkbox("Condensation enabled##new-gas", ref _newGasCondensationEnabled);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputFloat("Vaporization enthalpy (J/mol)##new-gas", ref _newGasEnthalpyOfVaporization);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Liquid ID##new-gas", ref _newGasLiquidId);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputFloat("Diffusion coefficient##new-gas", ref _newGasDiffusionCoefficient);
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
    }

    private void RenderProjectInjectionControls()
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

        string chunkLabel = FormatChunkPosition(_injectionChunkPosition.Value);
        if (ImGui.BeginCombo("Chunk##inject", chunkLabel))
        {
            foreach (var handle in _liveChunkHandles)
            {
                bool selected = handle.Position == _injectionChunkPosition.Value;
                if (ImGui.Selectable(FormatChunkPosition(handle.Position), selected))
                    _injectionChunkPosition = handle.Position;

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
            }
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Voxel X##inject", ref _injectionX);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Voxel Y##inject", ref _injectionY);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputInt("Voxel Z##inject", ref _injectionZ);

        if (ImGui.Button("Move Camera to Voxel", new Vector2(180, 0)) &&
            _injectionChunkPosition.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_injectionChunkPosition.Value, out var cameraChunk))
        {
            MoveCameraToVoxel(cameraChunk, _injectionX, _injectionY, _injectionZ);
        }

        if (_config!.GasRegistry.Count > 0)
        {
            _injectionGasId = Math.Clamp(_injectionGasId, 0, _config.GasRegistry.Count - 1);
            string gasLabel = FormatGas(_injectionGasId);
            if (ImGui.BeginCombo("Gas##inject", gasLabel))
            {
                for (int gasId = 0; gasId < _config.GasRegistry.Count; gasId++)
                {
                    bool selected = gasId == _injectionGasId;
                    if (ImGui.Selectable(FormatGas(gasId), selected))
                        _injectionGasId = gasId;

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
        }
        else
        {
            ImGui.TextDisabled("Gas: none registered");
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputFloat("Moles##inject", ref _injectionMoles);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ImGui.InputFloat("Temperature (K)##inject", ref _injectionTemperature);

        bool canInject = _config.GasRegistry.Count > 0;
        if (!canInject)
            ImGui.BeginDisabled();

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

        if (!canInject)
            ImGui.EndDisabled();
    }
}