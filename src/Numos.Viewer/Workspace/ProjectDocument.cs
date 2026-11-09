using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Workspace;

/// <summary>
///     The viewer's UI state for one open project: surfaces, drafts, feedback, pending removals, and timeline state.
/// </summary>
/// <remarks>
///     <para>
///         The viewer replaces the document whenever the frame's <see cref="Numos.Workbench.WorkbenchFrame.ProjectEpoch" />
///         changes, so nothing here outlives its project. <see cref="Dispose" /> frees the surfaces' GPU resources and
///         must run on the UI thread.
///     </para>
///     <para>
///         The editable values are fields, so ImGui widgets can take them by <see langword="ref" />. Program
///         preferences and the render caches of the active surface stay on the viewer.
///     </para>
/// </remarks>
internal sealed class ProjectDocument : IDisposable
{
    /// <summary>
    ///     One surface per simulation in the frame, sorted by simulation id.
    /// </summary>
    public readonly List<SimulationSurface> Surfaces = [];

    /// <summary>
    ///     Builder for the UI thread: the visualization list, snapshot field queries, and slices. Background builds
    ///     use one builder per surface, because one builder must not run two builds at once.
    /// </summary>
    public SimulationFrameBuilder? FrameBuilder;

    /// <summary>
    ///     The frame's configuration copy that <see cref="FrameBuilder" /> and the surface builders were made from.
    /// </summary>
    public AtmosConfig? PresentationConfig;

    /// <summary>
    ///     The timeline revision of the draw data on screen. A change means the timeline moved the world.
    /// </summary>
    public long PresentedTimelineRevision = long.MinValue;

    /// <summary>
    ///     The simulation the viewer presents and edits. Every command names its simulation, so the session's active
    ///     simulation only follows it for the session's own defaults.
    /// </summary>
    public AtmosSimulationId? ActiveSimulationId;

    /// <summary>
    ///     A simulation to activate once a frame lists it, such as one the viewer just asked the host to add.
    /// </summary>
    public AtmosSimulationId? PendingActiveSimulationId;

    /// <summary>
    ///     The editable configuration copy. A callback that ends an apply must use the draft it began on, so a late
    ///     result from a closed project never counts against this one.
    /// </summary>
    public readonly ConfigDraft ConfigDraft = new();

    /// <summary>
    ///     Values typed into an Atmos configuration field and not committed yet, keyed by field id.
    /// </summary>
    public readonly Dictionary<string, float> ConfigFloatDrafts = [];

    public readonly Dictionary<string, int> ConfigIntDrafts = [];

    /// <summary>
    ///     The validation error each Atmos configuration field shows, keyed by field id.
    /// </summary>
    public readonly Dictionary<string, string> ConfigFieldErrors = [];

    public readonly TopologyDraft Topology = new();

    /// <summary>
    ///     The viewport pick that fills the endpoints of <see cref="Topology" />.
    /// </summary>
    public readonly PortalEndpointCapture PortalCapture = new();

    public readonly ChunkDraft Chunk = new();
    public readonly GasDraft Gas = new();
    public readonly NewSimulationDraft NewSimulation = new();
    public readonly SolverAddDraft SolverAdd = new();

    /// <summary>
    ///     The simulation tree, refreshed from each frame.
    /// </summary>
    public readonly SimulationTree Tree = new();

    /// <summary>
    ///     The one selection. It also stands for the chunk, link set, and solver stage the older panels act on.
    /// </summary>
    public readonly WorkspaceSelection Selection = new();

    /// <summary>
    ///     A form Properties should open, scroll to, and focus the next time it draws the selected node.
    /// </summary>
    public PropertiesForm FocusForm;

    public string? SimulationFeedback;
    public bool SimulationFeedbackIsError;
    public string? TopologyFeedback;
    public bool TopologyFeedbackIsError;
    public FeedbackMessage? ChunkFeedback;
    public FeedbackMessage? VoxelFeedback;
    public FeedbackMessage? InjectionFeedback;
    public FeedbackMessage? GasRegistryFeedback;
    public FeedbackMessage? NewGasFeedback;
    public FeedbackMessage? ConfigurationFeedback;
    public FeedbackMessage? SolverFeedback;

    public AtmosSimulationId? SimulationPendingRemoval;
    public bool RemoveSimulationModalOpen;
    public bool RequestRemoveSimulation;
    public ExplicitLinkSetHandle? TopologyPendingRemoval;
    public bool RemoveTopologyModalOpen;
    public bool RequestRemoveTopology;
    public AtmosChunkHandle? ChunkPendingRemoval;
    public bool RemoveChunkModalOpen;
    public bool RequestRemoveChunk;
    public bool ResetConfigurationModalOpen;
    public bool RequestResetConfiguration;

    /// <summary>
    ///     The recorded operation the Timeline panel shows details for.
    /// </summary>
    public AtmosWorldRecordedOperation? TimelineOperation;

    public string? TimelineError;

    /// <summary>
    ///     A scrub target that waits for the host to finish the previous seek.
    /// </summary>
    public long? PendingScrubTick;

    /// <summary>
    ///     The first tick the timeline track shows.
    /// </summary>
    public long TimelineFirstTick;

    /// <summary>
    ///     Tick reached by the last Step press. Cleared by Run and by anything else that moves the world away from that
    ///     tick, so the "Stepped to" line never describes a state the user is no longer looking at.
    /// </summary>
    public long? SteppedToTick;

    /// <summary>
    ///     Disposes every surface. Call it on the UI thread, because the surfaces own GPU resources.
    /// </summary>
    public void Dispose()
    {
        foreach (var surface in Surfaces)
            surface.Dispose();

        Surfaces.Clear();
    }
}

/// <summary>
///     A form that a tree context menu item such as "Add Chunk..." asks Properties to focus.
/// </summary>
internal enum PropertiesForm
{
    None,
    NewSimulation,
    NewChunk,
    NewPortal,
    NewDock,
    NewSolverStage,
    NewGas
}

/// <summary>
///     A command outcome shown beside the control that sent it.
/// </summary>
internal readonly record struct FeedbackMessage(string Message, bool IsError);

/// <summary>
///     The new portal and new dock forms of the Topology properties.
/// </summary>
internal sealed class TopologyDraft
{
    public AtmosCellRef? PortalFirst;
    public AtmosCellRef? PortalSecond;
    public float AreaFraction = 1f;
    public bool Gas = true;
    public bool Thermal = true;
    public int DockFirstSimulation;
    public int DockFirstChunk;
    public int DockFirstFace;
    public int DockSecondSimulation;
    public int DockSecondChunk;
    public int DockSecondFace;
    public int DockRotation;
    public bool DockFlipU;
    public bool DockFlipV;
}

/// <summary>
///     The chunk forms of Properties: the new chunk, the fill classification, and the inject-by-coordinates voxel.
///     The chunk they act on is the selected chunk node.
/// </summary>
internal sealed class ChunkDraft
{
    public int NewX;
    public int NewY;
    public int NewZ;
    public int NewRoomId = 1;
    public int ToolClassification;
    public int InjectionX;
    public int InjectionY;
    public int InjectionZ;
}

/// <summary>
///     The add-gas form of the Gases properties.
/// </summary>
internal sealed class GasDraft
{
    public string Name = Locale.GetString("simulation-new-gas-name");
    public float MolarHeatCapacityAtConstantVolume =
        AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;
    public float BoilingPoint;
    public bool CondensationEnabled;
    public float EnthalpyOfVaporization;
    public int LiquidId = -1;
    public float DiffusionCoefficient = AtmosConfigDefaults.DefaultDiffusionCoefficient;
    public float MolarMass;
}

/// <summary>
///     The chunk dimensions of the add-simulation form.
/// </summary>
internal sealed class NewSimulationDraft
{
    public int Width = AtmosChunkConstants.DefaultWidth;
    public int Height = AtmosChunkConstants.DefaultHeight;
    public int Depth = 1;
}

/// <summary>
///     The add-stage form of the solver pipeline.
/// </summary>
internal sealed class SolverAddDraft
{
    /// <summary>
    ///     The stage picked to add, or <see langword="null" /> before a pick.
    /// </summary>
    public string? StageName;
}
