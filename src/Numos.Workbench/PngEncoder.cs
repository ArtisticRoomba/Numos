using System.Buffers.Binary;
using System.IO.Compression;

namespace Numos.Workbench;

/// <summary>
///     Encodes 8-bit RGBA images as PNG files.
/// </summary>
/// <remarks>
///     The encoder writes one IDAT chunk with no scanline filter, using <see cref="ZLibStream" />. It needs no image
///     package, which keeps the headless runner small and AOT-compatible.
/// </remarks>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    ///     Encodes an image.
    /// </summary>
    /// <param name="width">The width in pixels. Must be at least 1.</param>
    /// <param name="height">The height in pixels. Must be at least 1.</param>
    /// <param name="rgba">`width * height * 4` bytes, row by row from the top, red first.</param>
    /// <returns>The PNG file bytes.</returns>
    /// <exception cref="ArgumentException">The size or the buffer length is invalid.</exception>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long expected = (long)width * height * 4;
        if (expected > int.MaxValue / 2 || rgba.Length != expected)
        {
            throw new ArgumentException(
                $"A {width} x {height} image needs {expected} bytes of RGBA data and at most {int.MaxValue / 2}.");
        }

        int stride = width * 4;
        byte[] raw = new byte[(stride + 1) * height];
        for (int row = 0; row < height; row++)
            rgba.Slice(row * stride, stride).CopyTo(raw.AsSpan(row * (stride + 1) + 1, stride));

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            zlib.Write(raw);

        using var output = new MemoryStream();
        output.Write(Signature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; // Bit depth.
        header[9] = 6; // Color type: RGBA.
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>
    ///     Encodes an image and writes it to a file, creating missing parent directories.
    /// </summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="rgba">`width * height * 4` bytes, row by row from the top, red first.</param>
    public static void WriteFile(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        byte[] bytes = Encode(width, height, rgba);
        ExportFiles.EnsureParentDirectory(path);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        uint crc = Update(0xFFFFFFFFu, type);
        crc = Update(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        stream.Write(length);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

            table[index] = value;
        }

        return table;
    }
}
