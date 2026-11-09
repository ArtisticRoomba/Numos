using System.Collections.Frozen;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Workspace;

/// <summary>
///     What a node of the simulation tree stands for. Properties and the context menu switch on it.
/// </summary>
/// <remarks>
///     A new kind needs an enum value, a row in <see cref="TreeNodeKinds.Info" />, a builder section in
///     <see cref="SimulationTree" />, one case each in the Properties and context menu switches, and its locale keys.
/// </remarks>
internal enum TreeNodeKind
{
    Project,
    World,
    Simulation,
    Chunk,
    Topology,
    Portal,
    Dock,

    /// <summary>
    ///     A link set made through the generic link API. The viewer cannot create one, but a loaded replay can hold one.
    /// </summary>
    LinkSet,
    Solvers,
    SolverStage,
    Gases,
    Gas,
    AtmosConfiguration,
    Timeline,
    Branch,
    Checkpoint,
    Visualization,
    Views3D,
    Slice2D
}

/// <summary>
///     The fixed presentation of one <see cref="TreeNodeKind" />.
/// </summary>
/// <param name="Icon">The glyph from <see cref="Icons" /> drawn before the label.</param>
/// <param name="LabelKey">
///     Locale key of the kind's name. Properties shows it in the header, and singleton nodes such as World use it as
///     their label.
/// </param>
/// <param name="ActivatesSimulation">
///     Whether selecting the node makes its <see cref="TreeNode.Simulation" /> the active simulation.
/// </param>
internal sealed record TreeNodeKindInfo(string Icon, string LabelKey, bool ActivatesSimulation);

/// <summary>
///     The presentation table of every <see cref="TreeNodeKind" />.
/// </summary>
internal static class TreeNodeKinds
{
    /// <summary>
    ///     One row per kind. A missing row throws on lookup, and a test checks that every kind has one.
    /// </summary>
    public static IReadOnlyDictionary<TreeNodeKind, TreeNodeKindInfo> Info { get; } =
        new Dictionary<TreeNodeKind, TreeNodeKindInfo>
        {
            [TreeNodeKind.Project] = new(Icons.Folder, "tree-kind-project", false),
            [TreeNodeKind.World] = new(Icons.Globe, "tree-kind-world", false),
            [TreeNodeKind.Simulation] = new(Icons.Box, "tree-kind-simulation", true),
            [TreeNodeKind.Chunk] = new(Icons.Grid3x3, "tree-kind-chunk", true),
            [TreeNodeKind.Topology] = new(Icons.Network, "tree-kind-topology", false),
            [TreeNodeKind.Portal] = new(Icons.Link, "tree-kind-portal", false),
            [TreeNodeKind.Dock] = new(Icons.Plug, "tree-kind-dock", false),
            [TreeNodeKind.LinkSet] = new(Icons.Link, "tree-kind-link-set", false),
            [TreeNodeKind.Solvers] = new(Icons.Cpu, "tree-kind-solvers", false),
            [TreeNodeKind.SolverStage] = new(Icons.Layers, "tree-kind-solver-stage", false),
            [TreeNodeKind.Gases] = new(Icons.FlaskConical, "tree-kind-gases", false),
            [TreeNodeKind.Gas] = new(Icons.Wind, "tree-kind-gas", false),
            [TreeNodeKind.AtmosConfiguration] = new(Icons.SlidersHorizontal, "tree-kind-atmos-configuration", false),
            [TreeNodeKind.Timeline] = new(Icons.History, "tree-kind-timeline", false),
            [TreeNodeKind.Branch] = new(Icons.GitBranch, "tree-kind-branch", false),
            [TreeNodeKind.Checkpoint] = new(Icons.Save, "tree-kind-checkpoint", false),
            [TreeNodeKind.Visualization] = new(Icons.Palette, "tree-kind-visualization", false),
            [TreeNodeKind.Views3D] = new(Icons.Scan, "tree-kind-views-3d", false),
            [TreeNodeKind.Slice2D] = new(Icons.SquareSplitHorizontal, "tree-kind-slice-2d", false)
        }.ToFrozenDictionary();
}
