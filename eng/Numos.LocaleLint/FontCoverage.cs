using System.Buffers.Binary;

namespace Numos.LocaleLint;

/// <summary>
///     Answers whether the fonts the viewer loads have a glyph for a codepoint.
/// </summary>
internal sealed class FontCoverage
{
    // These ranges must track SimulationViewer.LoadFonts. ProggyClean is loaded with ImGui's default glyph range
    // (Basic Latin and Latin-1 Supplement). The Cyrillic font is loaded with GetGlyphRangesCyrillic.
    private readonly static (int First, int Last)[] ProggyRanges = [(0x0020, 0x00FF)];

    private readonly static (int First, int Last)[] CyrillicRanges =
        [(0x0020, 0x00FF), (0x0400, 0x052F), (0x2DE0, 0x2DFF), (0xA640, 0xA69F)];

    private readonly HashSet<int> _loaded;

    private FontCoverage(HashSet<int> loaded)
    {
        _loaded = loaded;
    }

    /// <summary>
    ///     Reads the viewer's font files and builds the set of codepoints that ImGui would load.
    /// </summary>
    /// <param name="assetsDirectory">The directory that holds <c>ProggyClean.ttf</c> and the Cyrillic font.</param>
    /// <exception cref="LocaleLintSetupException">A font file is missing or cannot be read.</exception>
    public static FontCoverage Load(string assetsDirectory)
    {
        var loaded = new HashSet<int>();
        AddRanges(loaded, Path.Combine(assetsDirectory, "ProggyClean.ttf"), ProggyRanges);
        AddRanges(loaded, Path.Combine(assetsDirectory, "NumosFixedCyrillic-6x13.ttf"), CyrillicRanges);
        return new FontCoverage(loaded);
    }

    /// <summary>
    ///     Builds a coverage from explicit codepoints. Tests use this to avoid reading fonts.
    /// </summary>
    public static FontCoverage FromCodepoints(IEnumerable<int> codepoints)
    {
        return new FontCoverage(new HashSet<int>(codepoints));
    }

    /// <summary>
    ///     Returns whether a codepoint has a glyph.
    /// </summary>
    public bool Has(int codepoint)
    {
        return _loaded.Contains(codepoint);
    }

    private static void AddRanges(HashSet<int> loaded, string path, (int First, int Last)[] ranges)
    {
        if (!File.Exists(path))
            throw new LocaleLintSetupException($"The font '{path}' does not exist.");

        foreach (int codepoint in ReadCmap(File.ReadAllBytes(path), path))
        {
            foreach ((int first, int last) in ranges)
            {
                if (codepoint >= first && codepoint <= last)
                {
                    loaded.Add(codepoint);
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Reads every codepoint that the <c>cmap</c> table maps to a glyph. Formats 0, 4, 6, and 12 are supported.
    /// </summary>
    internal static IEnumerable<int> ReadCmap(byte[] font, string name)
    {
        var result = new HashSet<int>();
        try
        {
            int tableCount = U16(font, 4);
            int cmap = -1;
            for (int i = 0; i < tableCount; i++)
            {
                int record = 12 + i * 16;
                if (font[record] == 'c' && font[record + 1] == 'm' && font[record + 2] == 'a' && font[record + 3] == 'p')
                    cmap = (int)U32(font, record + 8);
            }

            if (cmap < 0)
                throw new LocaleLintSetupException($"The font '{name}' has no cmap table.");

            int subtableCount = U16(font, cmap + 2);
            for (int i = 0; i < subtableCount; i++)
            {
                int offset = cmap + (int)U32(font, cmap + 4 + i * 8 + 4);
                ReadSubtable(font, offset, result);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            throw new LocaleLintSetupException($"The font '{name}' is truncated.");
        }

        return result;
    }

    private static void ReadSubtable(byte[] font, int offset, HashSet<int> result)
    {
        switch (U16(font, offset))
        {
            case 0:
                for (int code = 0; code < 256; code++)
                {
                    if (font[offset + 6 + code] != 0)
                        result.Add(code);
                }

                break;
            case 4:
                ReadFormat4(font, offset, result);
                break;
            case 6:
            {
                int first = U16(font, offset + 6);
                int count = U16(font, offset + 8);
                for (int i = 0; i < count; i++)
                {
                    if (U16(font, offset + 10 + i * 2) != 0)
                        result.Add(first + i);
                }

                break;
            }
            case 12:
            {
                uint groups = U32(font, offset + 12);
                for (uint g = 0; g < groups; g++)
                {
                    int group = offset + 16 + (int)g * 12;
                    uint start = U32(font, group);
                    uint end = U32(font, group + 4);
                    uint glyph = U32(font, group + 8);
                    for (uint code = start; code <= end; code++)
                    {
                        if (glyph + (code - start) != 0)
                            result.Add((int)code);
                    }
                }

                break;
            }
        }
    }

    private static void ReadFormat4(byte[] font, int offset, HashSet<int> result)
    {
        int segCount = U16(font, offset + 6) / 2;
        int endCodes = offset + 14;
        int startCodes = endCodes + segCount * 2 + 2;
        int deltas = startCodes + segCount * 2;
        int rangeOffsets = deltas + segCount * 2;

        for (int s = 0; s < segCount; s++)
        {
            int end = U16(font, endCodes + s * 2);
            int start = U16(font, startCodes + s * 2);
            int delta = (short)U16(font, deltas + s * 2);
            int rangeOffset = U16(font, rangeOffsets + s * 2);

            for (int code = start; code <= end && code != 0xFFFF; code++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = code + delta & 0xFFFF;
                }
                else
                {
                    int address = rangeOffsets + s * 2 + rangeOffset + (code - start) * 2;
                    glyph = U16(font, address);
                    if (glyph != 0)
                        glyph = glyph + delta & 0xFFFF;
                }

                if (glyph != 0)
                    result.Add(code);
            }
        }
    }

    private static int U16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static uint U32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }
}