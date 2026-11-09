namespace Numos.Viewer.Ui;

/// <summary>
///     Codepoints for the pixel icons in <c>assets/icons.png</c>. The sheet has 11 px cells in 8 columns, read
///     row by row. Cell <c>n</c> holds the icon at codepoint <see cref="First" /> plus <c>n</c>. Icons are white ink
///     on a transparent background, with no anti-aliasing. Append a new icon in the next free cell and add its
///     constant at the end, so the existing codepoints do not move.
/// </summary>
internal static class Icons
{
    /// <summary>
    ///     First codepoint of the icon block, in the Unicode private use area.
    /// </summary>
    public const ushort First = 0xE000;

    /// <summary>
    ///     Last codepoint of the icon block.
    /// </summary>
    public const ushort Last = 0xE02A;

    /// <summary>
    ///     Number of icons on the sheet.
    /// </summary>
    public const int Count = Last - First + 1;

    /// <summary>
    ///     Width and height of one sheet cell in pixels.
    /// </summary>
    public const int CellSize = 11;

    /// <summary>
    ///     Number of cells in one sheet row.
    /// </summary>
    public const int Columns = 8;

    /// <summary>
    ///     Advance width of every icon in pixels, at the 13 px size the viewer loads.
    /// </summary>
    public const float Advance = 13f;

    /// <summary>
    ///     Offset of the icon rectangle from the glyph origin. The icon has a 1 px left bearing. Its top row sits on
    ///     line row 1, so it spans the same rows as the ProggyClean brackets.
    /// </summary>
    public static readonly System.Numerics.Vector2 Offset = new(1f, 1f);

    public const string FilePlus = "\uE000";
    public const string FolderOpen = "\uE001";
    public const string Save = "\uE002";
    public const string X = "\uE003";
    public const string Play = "\uE004";
    public const string Pause = "\uE005";
    public const string StepForward = "\uE006";
    public const string MousePointer2 = "\uE007";
    public const string Paintbrush = "\uE008";
    public const string SprayCan = "\uE009";
    public const string Eraser = "\uE00A";
    public const string Flame = "\uE00B";
    public const string Terminal = "\uE00C";
    public const string TriangleAlert = "\uE00D";
    public const string CircleX = "\uE00E";
    public const string LayoutDashboard = "\uE00F";
    public const string Settings = "\uE010";
    public const string Folder = "\uE011";
    public const string Globe = "\uE012";
    public const string Box = "\uE013";
    public const string Grid3x3 = "\uE014";
    public const string Network = "\uE015";
    public const string Link = "\uE016";
    public const string Plug = "\uE017";
    public const string Cpu = "\uE018";
    public const string FlaskConical = "\uE019";
    public const string SlidersHorizontal = "\uE01A";
    public const string History = "\uE01B";
    public const string GitBranch = "\uE01C";
    public const string Layers = "\uE01D";
    public const string Palette = "\uE01E";
    public const string Scan = "\uE01F";
    public const string Crosshair = "\uE020";
    public const string SquareSplitHorizontal = "\uE021";
    public const string ChevronRight = "\uE022";
    public const string Lock = "\uE023";
    public const string Info = "\uE024";
    public const string Thermometer = "\uE025";
    public const string Wind = "\uE026";
    public const string Plus = "\uE027";
    public const string Trash2 = "\uE028";
    public const string Loader = "\uE029";
    public const string LogOut = "\uE02A";
}
