using System.Collections.Immutable;
using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Everything one background <see cref="SimulationFrameBuilder.BuildSimulation" /> call reads, so the viewer can
///     tell whether the draw data it has is still current.
/// </summary>
/// <remarks>
///     <para>
///         Record equality compares the builder, the snapshot map, and the handle list by reference. The host reuses
///         each of them until it changes, so equal inputs mean the last build still matches the frame.
///     </para>
///     <para>
///         <see cref="Fields" /> is the part of the frame's snapshot fields the drawing reads. Turning velocity arrows
///         on widens it once the host has captured velocity. The chunk versions do not move then, so
///         <see cref="RequiresRemap" /> has to force the builder to map the chunks again.
///     </para>
/// </remarks>
/// <param name="Builder">The builder that runs the build. Only one build runs on it at a time.</param>
/// <param name="Snapshots">The simulation's snapshot map from the frame.</param>
/// <param name="Handles">The chunk handles in position order, which sets the snapshot order.</param>
/// <param name="SourceVersion">The tick the snapshots were captured at.</param>
/// <param name="TimelineRevision">The frame's timeline revision, to notice a timeline move.</param>
/// <param name="VisualizationId">The visualization to map.</param>
/// <param name="MappingRevision">The visualization's mapping revision when the build was requested.</param>
/// <param name="Fields">The snapshot fields the drawing reads that every snapshot holds.</param>
/// <param name="Resolution">The legend resolution.</param>
/// <param name="LegendRangeRevision">Counts edits to the legend range settings.</param>
/// <param name="AutomaticRangeOffset">The padding of the automatic range.</param>
/// <param name="RangeOverride">The fixed range, or <see langword="null" /> for the automatic one.</param>
internal sealed record DrawBuildInputs(
    SimulationFrameBuilder Builder,
    ImmutableDictionary<Int3, AtmosChunkSnapshot> Snapshots,
    IReadOnlyList<AtmosChunkHandle> Handles,
    long SourceVersion,
    long TimelineRevision,
    string VisualizationId,
    ulong MappingRevision,
    AtmosChunkSnapshotFields Fields,
    int Resolution,
    ulong LegendRangeRevision,
    float AutomaticRangeOffset,
    VisualizationRange? RangeOverride)
{
    /// <summary>
    ///     Returns whether a build from these inputs has to map every chunk again instead of reusing chunks whose
    ///     version did not change.
    /// </summary>
    /// <param name="previous">The inputs of the draw data the build starts from, or <see langword="null" />.</param>
    public bool RequiresRemap(DrawBuildInputs? previous)
    {
        return previous == null ||
               !string.Equals(previous.VisualizationId, VisualizationId, StringComparison.OrdinalIgnoreCase) ||
               previous.MappingRevision != MappingRevision ||
               previous.Fields != Fields ||
               previous.Resolution != Resolution ||
               previous.LegendRangeRevision != LegendRangeRevision;
    }

    /// <summary>
    ///     Orders the snapshots by <see cref="Handles" />, skipping chunks the map does not hold yet.
    /// </summary>
    /// <remarks>
    ///     The map and the handle list are immutable, so this can run on a worker thread.
    /// </remarks>
    public AtmosChunkSnapshot[] OrderSnapshots()
    {
        var ordered = new List<AtmosChunkSnapshot>(Handles.Count);
        foreach (var handle in Handles)
        {
            if (Snapshots.TryGetValue(handle.Position, out var snapshot) && snapshot.Dimensions.X > 0)
                ordered.Add(snapshot);
        }

        return ordered.ToArray();
    }
}
