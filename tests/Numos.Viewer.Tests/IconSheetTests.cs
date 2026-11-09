using System.Buffers.Binary;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class IconSheetTests
{
    private static readonly string SheetPath = Path.Combine(AppContext.BaseDirectory, "assets", "icons.png");

    [Test]
    public void Sheet_IsCopiedToTheOutputDirectory()
    {
        Assert.That(File.Exists(SheetPath), Is.True, SheetPath);
    }

    [Test]
    public void Sheet_HoldsEveryIconCell()
    {
        // A PNG starts with an 8-byte signature, then the IHDR chunk: length, type, width, height.
        byte[] header = new byte[24];
        using (var stream = File.OpenRead(SheetPath))
            stream.ReadExactly(header);

        Assert.That(System.Text.Encoding.ASCII.GetString(header, 12, 4), Is.EqualTo("IHDR"));
        int width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20));

        Assert.That(width % Icons.CellSize, Is.Zero, "width");
        Assert.That(height % Icons.CellSize, Is.Zero, "height");
        Assert.That(width / Icons.CellSize, Is.EqualTo(Icons.Columns), "columns");
        Assert.That(width / Icons.CellSize * (height / Icons.CellSize), Is.GreaterThanOrEqualTo(Icons.Count));
    }

    [Test]
    public unsafe void Sheet_EveryUsedCellHasInk()
    {
        var image = Raylib.LoadImage(SheetPath);
        Color* colors = Raylib.LoadImageColors(image);
        try
        {
            for (int i = 0; i < Icons.Count; i++)
            {
                int cellX = i % Icons.Columns * Icons.CellSize;
                int cellY = i / Icons.Columns * Icons.CellSize;
                bool hasInk = false;
                for (int y = 0; y < Icons.CellSize && !hasInk; y++)
                {
                    for (int x = 0; x < Icons.CellSize; x++)
                    {
                        if (colors[(cellY + y) * image.Width + cellX + x].A != 0)
                        {
                            hasInk = true;
                            break;
                        }
                    }
                }

                Assert.That(hasInk, Is.True, $"cell {i} (U+{Icons.First + i:X4}) is empty");
            }
        }
        finally
        {
            Raylib.UnloadImageColors(colors);
            Raylib.UnloadImage(image);
        }
    }
}
