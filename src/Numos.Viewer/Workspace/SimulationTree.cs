using System.Globalization;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Workbench;

namespace Numos.Viewer.Workspace;

/// <summary>
///     The simulation tree of one project, built from published <see cref="WorkbenchFrame" />s.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Refresh" /> reads only the frame, never the live session. It rebuilds a section only when the
///         frame values that section follows have changed, so a paused or idle project costs a few comparisons per UI
///         frame. An unchanged section keeps its node objects and child arrays.
///     </para>
///     <para>
///         The shape is Project, then World (simulations with their chunks, Topology, Solvers, Gases, and Atmos
///         configuration), Timeline (branches, with the checkpoints under the selected one), and Visualization. Labels
///         are localized at build time, so a culture change rebuilds everything.
///     </para>
/// </remarks>
internal sealed class SimulationTree
{
    private readonly Dictionary<TreeNodeId, TreeNode[]> _children = [];
    private readonly Dictionary<TreeNodeId, TreeNode> _nodes = [];

    /// <summary>
    ///     The chunk handle array each simulation's chunk nodes were built from.
    /// </summary>
    private readonly Dictionary<AtmosSimulationId, IReadOnlyList<AtmosChunkHandle>> _chunkSources = [];

    private TreeNode[] _worldGroups = [];
    private long _projectEpoch = long.MinValue;
    private bool _hasProject;
    private string? _culture;
    private (AtmosSimulationId Id, string Name, Int3 Dimensions)[]? _simulationSignature;
    private SectionKey _linkSets = SectionKey.Unset;
    private SectionKey _solverStages = SectionKey.Unset;
    private SectionKey _gases = SectionKey.Unset;
    private SectionKey _timeline = SectionKey.Unset;

    /// <summary>
    ///     The project node, or <see langword="null" /> while no project is open.
    /// </summary>
    public TreeNode? Root { get; private set; }

    /// <summary>
    ///     The sequence of the frame the last <see cref="Refresh" /> read.
    /// </summary>
    public long FrameSequence { get; private set; } = long.MinValue;

    /// <summary>
    ///     The number of nodes in the tree.
    /// </summary>
    public int Count => _nodes.Count;

    /// <summary>
    ///     Brings the tree up to date with a frame.
    /// </summary>
    /// <param name="frame">The latest published frame.</param>
    /// <param name="culture">The current UI culture name. A change relocalizes every label.</param>
    /// <returns><see langword="true" /> when any node or child list changed.</returns>
    public bool Refresh(WorkbenchFrame frame, string culture)
    {
        FrameSequence = frame.Sequence;
        bool changed = false;
        if (frame.ProjectEpoch != _projectEpoch ||
            frame.HasProject != _hasProject ||
            !string.Equals(culture, _culture, StringComparison.Ordinal))
        {
            Reset();
            _projectEpoch = frame.ProjectEpoch;
            _hasProject = frame.HasProject;
            _culture = culture;
            changed = true;
            if (frame.HasProject)
                BuildSkeleton();
        }

        if (Root == null)
            return changed;

        string projectLabel = frame.ProjectName ?? Locale.GetString("common-untitled-simulation");
        if (Root.Label != projectLabel)
        {
            Root = Root with { Label = projectLabel };
            _nodes[Root.Id] = Root;
            changed = true;
        }

        changed |= RefreshSimulations(frame);
        changed |= RefreshChunks(frame);
        changed |= RefreshLinkSets(frame);
        changed |= RefreshSolverStages(frame);
        changed |= RefreshGases(frame);
        changed |= RefreshTimeline(frame);
        return changed;
    }

    public bool Contains(TreeNodeId id)
    {
        return _nodes.ContainsKey(id);
    }

    public bool TryGetNode(TreeNodeId id, out TreeNode node)
    {
        return _nodes.TryGetValue(id, out node!);
    }

    /// <summary>
    ///     Returns the children of a node in display order. The array is the same object until its section is rebuilt.
    /// </summary>
    public IReadOnlyList<TreeNode> GetChildren(TreeNodeId id)
    {
        return _children.TryGetValue(id, out var children) ? children : [];
    }

    /// <summary>
    ///     Returns the ids above a node, root first, or an empty list for the root or a node the tree lacks.
    /// </summary>
    public IReadOnlyList<TreeNodeId> GetAncestors(TreeNodeId id)
    {
        if (!_nodes.TryGetValue(id, out var node) || node.Parent is not { } parent)
            return [];

        var ancestors = new List<TreeNodeId>();
        TreeNodeId? current = parent;
        while (current is { } value && _nodes.TryGetValue(value, out var ancestor))
        {
            ancestors.Add(value);
            current = ancestor.Parent;
        }

        ancestors.Reverse();
        return ancestors;
    }

    private void Reset()
    {
        _nodes.Clear();
        _children.Clear();
        _chunkSources.Clear();
        _worldGroups = [];
        Root = null;
        _simulationSignature = null;
        _linkSets = SectionKey.Unset;
        _solverStages = SectionKey.Unset;
        _gases = SectionKey.Unset;
        _timeline = SectionKey.Unset;
    }

    private void BuildSkeleton()
    {
        Root = Add(new TreeNode(TreeNodeId.Project, null, string.Empty));
        var world = Add(Singleton(TreeNodeId.World));
        var timeline = Add(Singleton(TreeNodeId.Timeline));
        var visualization = Add(Singleton(TreeNodeId.Visualization));
        _children[Root.Id] = [world, timeline, visualization];

        _worldGroups =
        [
            Add(Singleton(TreeNodeId.Topology)),
            Add(Singleton(TreeNodeId.Solvers)),
            Add(Singleton(TreeNodeId.Gases)),
            Add(Singleton(TreeNodeId.AtmosConfiguration))
        ];

        _children[world.Id] = _worldGroups;
        _children[visualization.Id] = [Add(Singleton(TreeNodeId.Views3D)), Add(Singleton(TreeNodeId.Slice2D))];
    }

    /// <summary>
    ///     Rebuilds the simulation nodes when the id, name, or chunk dimensions of any simulation changes. The frame's
    ///     simulation list is a new object every frame while the world runs, so the check compares what the nodes
    ///     show. Chunk nodes of a simulation that stays are kept.
    /// </summary>
    private bool RefreshSimulations(WorkbenchFrame frame)
    {
        var signature = new (AtmosSimulationId Id, string Name, Int3 Dimensions)[frame.Simulations.Count];
        for (int index = 0; index < signature.Length; index++)
        {
            var shown = frame.Simulations[index];
            signature[index] = (shown.Id, shown.Name, shown.ChunkDimensions);
        }

        if (_simulationSignature != null && _simulationSignature.AsSpan().SequenceEqual(signature))
            return false;

        _simulationSignature = signature;
        var live = new HashSet<TreeNodeId>();
        var children = new TreeNode[frame.Simulations.Count + _worldGroups.Length];
        for (int index = 0; index < frame.Simulations.Count; index++)
        {
            var simulation = frame.Simulations[index];
            var dimensions = simulation.ChunkDimensions;
            var node = Add(
                new TreeNode(TreeNodeId.ForSimulation(simulation.Id), TreeNodeId.World, simulation.Name)
                {
                    Detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{dimensions.X}×{dimensions.Y}×{dimensions.Z}"),
                    Simulation = simulation.Id
                });

            live.Add(node.Id);
            children[index] = node;
        }

        foreach (var stale in GetChildren(TreeNodeId.World))
        {
            if (stale.Id.Kind == TreeNodeKind.Simulation && !live.Contains(stale.Id))
            {
                RemoveSubtree(stale.Id);
                if (stale.Simulation is { } removed)
                    _chunkSources.Remove(removed);
            }
        }

        _worldGroups.CopyTo(children, frame.Simulations.Count);
        _children[TreeNodeId.World] = children;
        return true;
    }

    private bool RefreshChunks(WorkbenchFrame frame)
    {
        bool changed = false;
        foreach (var simulation in frame.Simulations)
        {
            if (_chunkSources.TryGetValue(simulation.Id, out var source) &&
                ReferenceEquals(source, simulation.ChunkHandles))
                continue;

            _chunkSources[simulation.Id] = simulation.ChunkHandles;
            var parent = TreeNodeId.ForSimulation(simulation.Id);
            var children = new TreeNode[simulation.ChunkHandles.Count];
            for (int index = 0; index < children.Length; index++)
            {
                var position = simulation.ChunkHandles[index].Position;
                string label = Locale.GetString(
                    "tree-chunk",
                    ("position", string.Create(
                        CultureInfo.InvariantCulture,
                        $"({position.X}, {position.Y}, {position.Z})")));

                children[index] = new TreeNode(TreeNodeId.ForChunk(simulation.Id, position), parent, label)
                {
                    Simulation = simulation.Id,
                    Chunk = position
                };
            }

            ReplaceChildren(parent, children);
            changed = true;
        }

        return changed;
    }

    private bool RefreshLinkSets(WorkbenchFrame frame)
    {
        var key = new SectionKey(frame.LinkSetRevision, 0, frame.LinkSets);
        if (key == _linkSets)
            return false;

        _linkSets = key;
        var children = new TreeNode[frame.LinkSets.Count];
        for (int index = 0; index < children.Length; index++)
        {
            var set = frame.LinkSets[index];
            string labelKey = set.Kind switch
            {
                ExplicitLinkSetKind.Portal => "tree-portal",
                ExplicitLinkSetKind.Dock => "tree-dock",
                _ => "tree-link-set"
            };

            children[index] = new TreeNode(
                TreeNodeId.ForLinkSet(set.Kind, set.Handle),
                TreeNodeId.Topology,
                Locale.GetString(labelKey, ("number", set.Handle.Index + 1)))
            {
                LinkSet = set.Handle
            };
        }

        ReplaceChildren(TreeNodeId.Topology, children);
        return true;
    }

    private bool RefreshSolverStages(WorkbenchFrame frame)
    {
        var key = new SectionKey(frame.DefinitionRevision, 0, frame.SolverSteps);
        if (key == _solverStages)
            return false;

        _solverStages = key;
        string disabled = Locale.GetString("disabled");
        var children = new TreeNode[frame.SolverSteps.Count];
        for (int index = 0; index < children.Length; index++)
        {
            var step = frame.SolverSteps[index];
            children[index] = new TreeNode(TreeNodeId.ForSolverStage(step.Name), TreeNodeId.Solvers, step.Name)
            {
                Stage = step.Name,
                IsMuted = !step.IsEnabled,
                Detail = step.IsEnabled ? null : disabled
            };
        }

        ReplaceChildren(TreeNodeId.Solvers, children);
        return true;
    }

    private bool RefreshGases(WorkbenchFrame frame)
    {
        var key = new SectionKey(frame.DefinitionRevision, 0, frame.Gases);
        if (key == _gases)
            return false;

        _gases = key;
        var children = new TreeNode[frame.Gases.Count];
        for (int gasId = 0; gasId < children.Length; gasId++)
        {
            string? name = frame.Gases[gasId].Name;
            children[gasId] = new TreeNode(
                TreeNodeId.ForGas(gasId),
                TreeNodeId.Gases,
                string.IsNullOrWhiteSpace(name) ? Locale.GetString("gas-id", ("id", gasId)) : name)
            {
                GasId = gasId,
                Detail = string.Create(CultureInfo.InvariantCulture, $"#{gasId}")
            };
        }

        ReplaceChildren(TreeNodeId.Gases, children);
        return true;
    }

    /// <summary>
    ///     Rebuilds the branches and checkpoints. The key leaves out the branch list itself, because the host replaces
    ///     it whenever the head moves, which is every tick of a live run.
    /// </summary>
    private bool RefreshTimeline(WorkbenchFrame frame)
    {
        var timeline = frame.Timeline;
        var key = timeline == null
            ? new SectionKey(frame.TimelineRevision, -1, null)
            : new SectionKey(
                frame.TimelineRevision,
                ((long)timeline.Branches.Count << 32) | (uint)timeline.SelectedBranchId,
                timeline.Checkpoints);

        if (key == _timeline)
            return false;

        _timeline = key;
        if (timeline == null)
        {
            ReplaceChildren(TreeNodeId.Timeline, []);
            return true;
        }

        var branches = new TreeNode[timeline.Branches.Count];
        TreeNodeId? selected = null;
        for (int index = 0; index < branches.Length; index++)
        {
            var branch = timeline.Branches[index];
            var id = TreeNodeId.ForBranch(branch.Id);
            branches[index] = new TreeNode(id, TreeNodeId.Timeline, branch.Name)
            {
                BranchId = branch.Id,
                Detail = Locale.GetString("timeline-fork-tick", ("tick", branch.Fork.Tick))
            };

            if (branch.Id == timeline.SelectedBranchId)
                selected = id;
        }

        ReplaceChildren(TreeNodeId.Timeline, branches);
        if (selected is not { } parent)
            return true;

        var checkpoints = new TreeNode[timeline.Checkpoints.Count];
        var used = new HashSet<TreeNodeId>();
        for (int index = 0; index < checkpoints.Length; index++)
        {
            var position = timeline.Checkpoints[index].Checkpoint.Position;
            var id = TreeNodeId.ForCheckpoint(position);
            if (!used.Add(id))
            {
                id = TreeNodeId.ForCheckpoint(position, withSequence: true);
                used.Add(id);
            }

            checkpoints[index] = new TreeNode(id, parent, Locale.GetString("tree-checkpoint", ("tick", position.Tick)))
            {
                BranchId = timeline.SelectedBranchId,
                Tick = position.Tick,
                Position = position
            };
        }

        ReplaceChildren(parent, checkpoints);
        return true;
    }

    private TreeNode Singleton(TreeNodeId id)
    {
        return new TreeNode(id, id.GetCanonicalParent(), Locale.GetString(TreeNodeKinds.Info[id.Kind].LabelKey));
    }

    private TreeNode Add(TreeNode node)
    {
        _nodes[node.Id] = node;
        return node;
    }

    /// <summary>
    ///     Replaces a node's children and drops the subtrees of the old ones.
    /// </summary>
    private void ReplaceChildren(TreeNodeId parent, TreeNode[] children)
    {
        if (_children.TryGetValue(parent, out var previous))
        {
            foreach (var child in previous)
                RemoveSubtree(child.Id);
        }

        foreach (var child in children)
            _nodes[child.Id] = child;

        _children[parent] = children;
    }

    private void RemoveSubtree(TreeNodeId id)
    {
        _nodes.Remove(id);
        if (!_children.Remove(id, out var children))
            return;

        foreach (var child in children)
            RemoveSubtree(child.Id);
    }

    /// <summary>
    ///     The frame values one section was built from. The reference is compared by identity.
    /// </summary>
    private readonly record struct SectionKey(long First, long Second, object? Source)
    {
        public static SectionKey Unset { get; } = new(long.MinValue, long.MinValue, null);

        public bool Equals(SectionKey other)
        {
            return First == other.First && Second == other.Second && ReferenceEquals(Source, other.Source);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(First, Second);
        }
    }
}
