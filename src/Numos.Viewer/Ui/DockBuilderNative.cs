using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Numos.Viewer.Ui;

/// <summary>
///     Calls into the native cimgui library for the docking, side-bar, and window lookup functions that ImGui.NET
///     1.91.6.1 does not wrap. The signatures match the <c>ig*</c> exports of cimgui 1.91.6.
/// </summary>
/// <remarks>
///     Recheck every signature here when ImGui.NET is upgraded. A mismatch does not fail the build, so it would
///     corrupt the stack or read the wrong field at run time.
/// </remarks>
internal static unsafe partial class DockBuilderNative
{
    /// <summary>
    ///     The library name ImGui.NET uses in its own imports, so both resolve to the same loaded module.
    /// </summary>
    private const string Library = "cimgui";

    /// <summary>
    ///     <c>ImGuiDockNodeFlags_DockSpace</c> from <c>imgui_internal.h</c>. ImGui.NET only exposes the public flags.
    ///     A root node added with this flag is the dockspace that <see cref="ImGui.DockSpace(uint)" /> hosts.
    /// </summary>
    public const ImGuiDockNodeFlags DockSpaceFlag = (ImGuiDockNodeFlags)(1 << 10);

    [LibraryImport(Library, EntryPoint = "igDockBuilderAddNode")]
    public static partial uint AddNode(uint nodeId, ImGuiDockNodeFlags flags);

    [LibraryImport(Library, EntryPoint = "igDockBuilderRemoveNode")]
    public static partial void RemoveNode(uint nodeId);

    /// <summary>
    ///     <c>igDockBuilderSetNodeSize</c>, called through a function pointer. Its <c>ImVec2</c> argument goes by value,
    ///     which <see cref="LibraryImportAttribute" /> cannot marshal while runtime marshalling is on. A function
    ///     pointer passes the blittable <see cref="Vector2" /> unchanged.
    /// </summary>
    private readonly static delegate* unmanaged[Cdecl]<uint, Vector2, void> SetNodeSizeExport =
        (delegate* unmanaged[Cdecl]<uint, Vector2, void>)NativeLibrary.GetExport(
            NativeLibrary.Load(Library, typeof(ImGui).Assembly, null),
            "igDockBuilderSetNodeSize");

    public static void SetNodeSize(uint nodeId, Vector2 size)
    {
        SetNodeSizeExport(nodeId, size);
    }

    /// <summary>
    ///     Splits a node in two and returns the ID of the part on <paramref name="splitDirection" />.
    /// </summary>
    /// <remarks>
    ///     The central node, if <paramref name="nodeId" /> holds it, moves to the part opposite
    ///     <paramref name="splitDirection" />.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "igDockBuilderSplitNode")]
    public static partial uint SplitNode(
        uint nodeId,
        ImGuiDir splitDirection,
        float sizeRatioForNodeAtDirection,
        uint* outIdAtDirection,
        uint* outIdAtOppositeDirection);

    /// <summary>
    ///     Docks a window into a node by its full name. A <c>###id</c> name docks the window whatever its title is.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "igDockBuilderDockWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void DockWindow(string windowName, uint nodeId);

    [LibraryImport(Library, EntryPoint = "igDockBuilderFinish")]
    public static partial void Finish(uint nodeId);

    /// <returns>The native <c>ImGuiDockNode*</c>, or null when no node has this ID.</returns>
    [LibraryImport(Library, EntryPoint = "igDockBuilderGetNode")]
    public static partial void* GetNode(uint nodeId);

    /// <returns>The native <c>ImGuiDockNode*</c> of the dockspace's central node, or null.</returns>
    [LibraryImport(Library, EntryPoint = "igDockBuilderGetCentralNode")]
    public static partial void* GetCentralNode(uint nodeId);

    /// <summary>
    ///     Begins a window fixed to one edge of <paramref name="viewport" /> that shrinks the viewport's work area for
    ///     later frames. The caller must call <see cref="ImGui.End" /> whatever this returns.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "igBeginViewportSideBar", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool BeginViewportSideBar(
        string name,
        ImGuiViewport* viewport,
        ImGuiDir direction,
        float axisSize,
        ImGuiWindowFlags flags);

    /// <summary>
    ///     Returns the ID of the central node in the dockspace rooted at <paramref name="dockspaceId" />, or 0 when the
    ///     dockspace has no node yet.
    /// </summary>
    /// <remarks>
    ///     This reads <c>ImGuiDockNode::ID</c>, the struct's first field in imgui 1.91.6. The offset is part of the
    ///     cimgui ABI, so recheck it whenever ImGui.NET is upgraded.
    /// </remarks>
    public static uint GetCentralNodeId(uint dockspaceId)
    {
        void* node = GetCentralNode(dockspaceId);
        return node == null ? 0 : *(uint*)node;
    }

    /// <summary>
    ///     Returns whether a dock node with this ID exists, which includes nodes restored from saved settings.
    /// </summary>
    public static bool NodeExists(uint nodeId)
    {
        return GetNode(nodeId) != null;
    }

    [LibraryImport(Library, EntryPoint = "igImHashStr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint HashString(string data, nuint dataSize, uint seed);

    [LibraryImport(Library, EntryPoint = "igFindWindowSettingsByID")]
    private static partial void* FindWindowSettings(uint windowId);

    [LibraryImport(Library, EntryPoint = "igFindWindowByName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void* FindWindow(string windowName);

    /// <summary>
    ///     Returns whether ImGui knows the window, either from a Begin this session or from loaded settings. A window
    ///     it does not know gets its <c>FirstUseEver</c> placement on its next Begin.
    /// </summary>
    /// <param name="windowName">The full window name. A <c>###id</c> name matches the window whatever its title is.</param>
    /// <remarks>
    ///     A window's ID is the hash of its name with seed 0, and the hash restarts at <c>###</c>. That is how
    ///     <c>ImGui::FindWindowByName</c> computes it too.
    /// </remarks>
    public static bool IsWindowKnown(string windowName)
    {
        return FindWindow(windowName) != null || FindWindowSettings(HashString(windowName, 0, 0)) != null;
    }
}
