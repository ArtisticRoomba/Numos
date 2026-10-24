using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Numos.Workbench;

/// <summary>
///     Writes arrays in the NumPy `.npy` format, version 1.0.
/// </summary>
/// <remarks>
///     <para>
///         The writer produces little-endian `float32` data in C order. `numpy.load` reads the file without options.
///     </para>
///     <para>
///         The header pads to a multiple of 64 bytes, as current NumPy versions do, so the data starts aligned.
///     </para>
/// </remarks>
public static class NpyWriter
{
    private static readonly byte[] Magic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0];

    /// <summary>
    ///     Writes a `float32` array to a stream.
    /// </summary>
    /// <param name="stream">The destination.</param>
    /// <param name="values">The values in C order. The length must equal the product of <paramref name="shape" />.</param>
    /// <param name="shape">The array shape, slowest axis first.</param>
    /// <exception cref="ArgumentException">The shape does not match the value count.</exception>
    public static void Write(Stream stream, ReadOnlySpan<float> values, ReadOnlySpan<int> shape)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long count = 1;
        foreach (int extent in shape)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(extent);
            count *= extent;
        }

        if (count != values.Length)
            throw new ArgumentException($"The shape holds {count} values but {values.Length} were given.");

        byte[] header = BuildHeader(shape);
        stream.Write(header);
        if (BitConverter.IsLittleEndian)
        {
            stream.Write(MemoryMarshal.AsBytes(values));
            return;
        }

        byte[] buffer = new byte[4];
        foreach (float value in values)
        {
            BitConverter.TryWriteBytes(buffer, value);
            Array.Reverse(buffer);
            stream.Write(buffer);
        }
    }

    /// <summary>
    ///     Writes a `float32` array to a file and creates missing parent directories.
    /// </summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="values">The values in C order. The length must equal the product of <paramref name="shape" />.</param>
    /// <param name="shape">The array shape, slowest axis first.</param>
    public static void WriteFile(string path, ReadOnlySpan<float> values, ReadOnlySpan<int> shape)
    {
        ExportFiles.EnsureParentDirectory(path);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Write(stream, values, shape);
    }

    private static byte[] BuildHeader(ReadOnlySpan<int> shape)
    {
        var shapeText = new StringBuilder("(");
        for (int index = 0; index < shape.Length; index++)
        {
            if (index > 0)
                shapeText.Append(", ");

            shapeText.Append(shape[index].ToString(CultureInfo.InvariantCulture));
        }

        if (shape.Length == 1)
            shapeText.Append(',');

        shapeText.Append(')');
        string dictionary = $"{{'descr': '<f4', 'fortran_order': False, 'shape': {shapeText}, }}";

        // Total size = 8 (magic and version) + 2 (header length) + header text + newline.
        int unpadded = Magic.Length + 2 + dictionary.Length + 1;
        int padding = (64 - unpadded % 64) % 64;
        string text = dictionary + new string(' ', padding) + "\n";
        byte[] result = new byte[Magic.Length + 2 + text.Length];
        Magic.CopyTo(result, 0);
        BitConverter.TryWriteBytes(result.AsSpan(Magic.Length, 2), (ushort)text.Length);
        Encoding.ASCII.GetBytes(text, result.AsSpan(Magic.Length + 2));
        return result;
    }
}
