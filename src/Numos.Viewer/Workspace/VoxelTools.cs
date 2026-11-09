using Numos.Viewer.Ui;

namespace Numos.Viewer.Workspace;

/// <summary>
///     The viewport tool that a left click or drag on a voxel uses.
/// </summary>
internal enum VoxelEditTool
{
    Select,
    PaintClassification,
    PaintGas,
    EraseGas,

    /// <summary>
    ///     Ignites the clicked voxel for one tick with the toolbar's temperature and volume.
    /// </summary>
    Ignite
}

/// <summary>
///     What a voxel pointer event in a viewport does.
/// </summary>
internal enum VoxelPointerAction
{
    /// <summary>
    ///     The click fills the armed endpoint of the new portal form.
    /// </summary>
    PortalPick,

    /// <summary>
    ///     A click ignites the voxel under the pointer.
    /// </summary>
    Ignite,

    /// <summary>
    ///     A click or drag selects voxels, or paints them with the active paint tool.
    /// </summary>
    SelectOrPaint
}

/// <summary>
///     A toolbar button for one <see cref="VoxelEditTool" />.
/// </summary>
/// <param name="Tool">The tool the button activates.</param>
/// <param name="Icon">The glyph from <see cref="Icons" />.</param>
/// <param name="LabelKey">Locale key of the tool name, which the button shows as its tooltip.</param>
internal sealed record VoxelToolInfo(VoxelEditTool Tool, string Icon, string LabelKey);

/// <summary>
///     The voxel tools of the toolbar and the rules for when they work.
/// </summary>
internal static class VoxelTools
{
    /// <summary>
    ///     Every tool, in toolbar order.
    /// </summary>
    public static IReadOnlyList<VoxelToolInfo> All { get; } =
    [
        new(VoxelEditTool.Select, Icons.MousePointer2, "select-box-tool"),
        new(VoxelEditTool.PaintClassification, Icons.Paintbrush, "paint-room-tool"),
        new(VoxelEditTool.PaintGas, Icons.SprayCan, "paint-gas-tool"),
        new(VoxelEditTool.EraseGas, Icons.Eraser, "erase-gas-tool"),
        new(VoxelEditTool.Ignite, Icons.Flame, "ignite-tool")
    ];

    /// <summary>
    ///     Returns whether <paramref name="tool" /> sends commands that change the world.
    /// </summary>
    public static bool EditsWorld(VoxelEditTool tool)
    {
        return tool != VoxelEditTool.Select;
    }

    /// <summary>
    ///     Returns whether the toolbar button of <paramref name="tool" /> and its options can be used.
    /// </summary>
    /// <param name="tool">The tool.</param>
    /// <param name="hasSimulation">Whether a simulation is active.</param>
    /// <param name="editingBlocked">
    ///     Whether the timeline inspects history or the host is busy. Select still works then, because selecting
    ///     voxels only reads the frame on screen.
    /// </param>
    public static bool IsEnabled(VoxelEditTool tool, bool hasSimulation, bool editingBlocked)
    {
        return hasSimulation && (!EditsWorld(tool) || !editingBlocked);
    }

    /// <summary>
    ///     Returns what a voxel pointer event does.
    /// </summary>
    /// <param name="portalPickArmed">
    ///     Whether the new portal form waits for an endpoint pick. The pick takes precedence over every tool.
    /// </param>
    /// <param name="tool">The active tool.</param>
    public static VoxelPointerAction GetPointerAction(bool portalPickArmed, VoxelEditTool tool)
    {
        if (portalPickArmed)
            return VoxelPointerAction.PortalPick;

        return tool == VoxelEditTool.Ignite ? VoxelPointerAction.Ignite : VoxelPointerAction.SelectOrPaint;
    }
}
