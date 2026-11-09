using System.Globalization;
using Numos.API;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.Viewer.Workspace;

/// <summary>
///     Identifies a simulation tree node across tree rebuilds.
/// </summary>
/// <param name="Kind">What the node stands for.</param>
/// <param name="Key">
///     The canonical key, such as <c>sim/3.1/chunk/0,0,1</c>. It is built from ids, never from display names, so a
///     rename or a language change keeps it.
/// </param>
internal readonly record struct TreeNodeId(TreeNodeKind Kind, string Key)
{
    private const string SimulationPrefix = "sim/";
    private const string ChunkSeparator = "/chunk/";

    public static TreeNodeId Project { get; } = new(TreeNodeKind.Project, "project");
    public static TreeNodeId World { get; } = new(TreeNodeKind.World, "world");
    public static TreeNodeId Topology { get; } = new(TreeNodeKind.Topology, "topology");
    public static TreeNodeId Solvers { get; } = new(TreeNodeKind.Solvers, "solvers");
    public static TreeNodeId Gases { get; } = new(TreeNodeKind.Gases, "gases");
    public static TreeNodeId AtmosConfiguration { get; } = new(TreeNodeKind.AtmosConfiguration, "atmos-config");
    public static TreeNodeId Timeline { get; } = new(TreeNodeKind.Timeline, "timeline");
    public static TreeNodeId Visualization { get; } = new(TreeNodeKind.Visualization, "visualization");
    public static TreeNodeId Views3D { get; } = new(TreeNodeKind.Views3D, "views-3d");
    public static TreeNodeId Slice2D { get; } = new(TreeNodeKind.Slice2D, "slice-2d");

    /// <summary>
    ///     A simulation, keyed by index and generation.
    /// </summary>
    public static TreeNodeId ForSimulation(AtmosSimulationId simulation)
    {
        return new TreeNodeId(TreeNodeKind.Simulation, SimulationKey(simulation));
    }

    /// <summary>
    ///     A chunk of one simulation. The same position in two simulations gives two ids.
    /// </summary>
    public static TreeNodeId ForChunk(AtmosSimulationId simulation, Int3 position)
    {
        return new TreeNodeId(
            TreeNodeKind.Chunk,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SimulationKey(simulation)}{ChunkSeparator}{position.X},{position.Y},{position.Z}"));
    }

    /// <summary>
    ///     A portal, dock, or generic link set, keyed by handle. The kind follows how the set was created.
    /// </summary>
    public static TreeNodeId ForLinkSet(ExplicitLinkSetKind kind, ExplicitLinkSetHandle handle)
    {
        var nodeKind = kind switch
        {
            ExplicitLinkSetKind.Portal => TreeNodeKind.Portal,
            ExplicitLinkSetKind.Dock => TreeNodeKind.Dock,
            _ => TreeNodeKind.LinkSet
        };

        return new TreeNodeId(
            nodeKind,
            string.Create(CultureInfo.InvariantCulture, $"link/{handle.Index}.{handle.Generation}"));
    }

    public static TreeNodeId ForSolverStage(string name)
    {
        return new TreeNodeId(TreeNodeKind.SolverStage, "stage/" + name);
    }

    public static TreeNodeId ForGas(int gasId)
    {
        return new TreeNodeId(TreeNodeKind.Gas, string.Create(CultureInfo.InvariantCulture, $"gas/{gasId}"));
    }

    public static TreeNodeId ForBranch(int branchId)
    {
        return new TreeNodeId(TreeNodeKind.Branch, string.Create(CultureInfo.InvariantCulture, $"branch/{branchId}"));
    }

    /// <summary>
    ///     A checkpoint, keyed by tick. A second checkpoint at the same tick also gets its operation sequence.
    /// </summary>
    public static TreeNodeId ForCheckpoint(AtmosTimelinePosition position, bool withSequence = false)
    {
        return new TreeNodeId(
            TreeNodeKind.Checkpoint,
            withSequence
                ? string.Create(CultureInfo.InvariantCulture, $"checkpoint/{position.Tick}.{position.OperationSequence}")
                : string.Create(CultureInfo.InvariantCulture, $"checkpoint/{position.Tick}"));
    }

    /// <summary>
    ///     Returns the parent the tree gives this id, worked out from the id alone.
    /// </summary>
    /// <remarks>
    ///     The selection uses it for a node it has not seen in the tree yet, such as a portal a command just created.
    ///     It matches the tree everywhere except checkpoints, which the tree puts under the selected branch. Here they
    ///     go under Timeline.
    /// </remarks>
    public TreeNodeId? GetCanonicalParent()
    {
        switch (Kind)
        {
            case TreeNodeKind.Project:
                return null;
            case TreeNodeKind.World:
            case TreeNodeKind.Timeline:
            case TreeNodeKind.Visualization:
                return Project;
            case TreeNodeKind.Simulation:
            case TreeNodeKind.Topology:
            case TreeNodeKind.Solvers:
            case TreeNodeKind.Gases:
            case TreeNodeKind.AtmosConfiguration:
                return World;
            case TreeNodeKind.Chunk:
                int separator = Key.IndexOf(ChunkSeparator, StringComparison.Ordinal);
                return separator < 0 ? World : new TreeNodeId(TreeNodeKind.Simulation, Key[..separator]);
            case TreeNodeKind.Portal:
            case TreeNodeKind.Dock:
            case TreeNodeKind.LinkSet:
                return Topology;
            case TreeNodeKind.SolverStage:
                return Solvers;
            case TreeNodeKind.Gas:
                return Gases;
            case TreeNodeKind.Branch:
            case TreeNodeKind.Checkpoint:
                return Timeline;
            case TreeNodeKind.Views3D:
            case TreeNodeKind.Slice2D:
                return Visualization;
            default:
                return Project;
        }
    }

    private static string SimulationKey(AtmosSimulationId simulation)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{SimulationPrefix}{simulation.Index}.{simulation.Generation}");
    }
}

/// <summary>
///     One node of the simulation tree.
/// </summary>
/// <remarks>
///     The payload fields are flat. A node sets only the fields its kind uses and leaves the rest
///     <see langword="null" />. Children live in <see cref="SimulationTree" />, so a section can be rebuilt without
///     replacing the nodes above it.
/// </remarks>
/// <param name="Id">The stable id.</param>
/// <param name="Parent">The parent id, or <see langword="null" /> for the project root.</param>
/// <param name="Label">The localized display label.</param>
internal sealed record TreeNode(TreeNodeId Id, TreeNodeId? Parent, string Label)
{
    /// <summary>
    ///     Secondary text the tree draws muted after the label, or <see langword="null" />.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    ///     Whether the tree draws the label muted, such as a disabled solver stage. <see cref="Detail" /> also says why,
    ///     so the state does not rest on colour alone.
    /// </summary>
    public bool IsMuted { get; init; }

    public AtmosSimulationId? Simulation { get; init; }
    public Int3? Chunk { get; init; }
    public ExplicitLinkSetHandle? LinkSet { get; init; }
    public string? Stage { get; init; }
    public int? GasId { get; init; }
    public int? BranchId { get; init; }

    /// <summary>
    ///     The checkpoint tick. <see cref="Position" /> adds the operation sequence a seek needs.
    /// </summary>
    public ulong? Tick { get; init; }

    public AtmosTimelinePosition? Position { get; init; }
}
