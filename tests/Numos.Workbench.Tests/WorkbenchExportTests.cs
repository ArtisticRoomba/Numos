using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class WorkbenchExportTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, true);
    }

    private static WorkbenchSession CreateSession()
    {
        var session = new WorkbenchSession();
        session.CreateProject(
            new WorkbenchProjectOptions("Export", 4, 3, 1, false),
            config =>
            {
                config.VoxelVolume = 8.31446262f;
                config.GasRegistry.Add(new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = 1f });
            });

        session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(7, 2, 0)),
            new RegionEdit(true, 1, false, 300f, [new RegionGas(0, null, 3000f)], 300f));

        return session;
    }

    private static (string Header, float[] Values, int HeaderLength) ReadNpy(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.That(bytes[0], Is.EqualTo(0x93));
        Assert.That(Encoding.ASCII.GetString(bytes, 1, 5), Is.EqualTo("NUMPY"));
        Assert.That(bytes[6], Is.EqualTo(1));
        Assert.That(bytes[7], Is.EqualTo(0));
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        string header = Encoding.ASCII.GetString(bytes, 10, headerLength);
        float[] values = new float[(bytes.Length - 10 - headerLength) / 4];
        Buffer.BlockCopy(bytes, 10 + headerLength, values, 0, values.Length * 4);
        return (header, values, 10 + headerLength);
    }

    [Test]
    public void NpyWriter_WritesAVersionOneHeaderAlignedTo64Bytes()
    {
        string path = Path.Combine(_directory, "a.npy");
        NpyWriter.WriteFile(path, [1f, 2f, 3f, 4f, 5f, 6f], [1, 2, 3]);

        var (header, values, dataOffset) = ReadNpy(path);
        Assert.Multiple(() =>
        {
            Assert.That(header, Is.EqualTo("{'descr': '<f4', 'fortran_order': False, 'shape': (1, 2, 3), }".PadRight(header.Length - 1) + "\n"));
            Assert.That(dataOffset % 64, Is.Zero);
            Assert.That(values, Is.EqualTo(new[] { 1f, 2f, 3f, 4f, 5f, 6f }));
        });
    }

    [Test]
    public void NpyWriter_WritesOneDimensionalShapesWithATrailingComma()
    {
        string path = Path.Combine(_directory, "b.npy");
        NpyWriter.WriteFile(path, [1f, 2f], [2]);

        Assert.That(ReadNpy(path).Header, Does.Contain("'shape': (2,),"));
        Assert.Throws<ArgumentException>(() => NpyWriter.WriteFile(path, [1f], [2]));
    }

    [Test]
    public void ExportRegion_WritesOneFilePerFieldWithShapeAndRange()
    {
        using var session = CreateSession();
        string template = Path.Combine(_directory, "out-{field}.npy");

        var exports = session.ExportRegion(
            new VoxelBox(new Int3(2, 1, 0), new Int3(5, 2, 0)),
            ["pressure", "moles:0"],
            template);

        var (header, values, _) = ReadNpy(exports[0].Path!);
        Assert.Multiple(() =>
        {
            Assert.That(exports, Has.Count.EqualTo(2));
            Assert.That(exports[1].Path, Does.EndWith("out-moles_0.npy"));
            Assert.That(exports[0].Shape, Is.EqualTo(new[] { 1, 2, 4 }));
            Assert.That(header, Does.Contain("'shape': (1, 2, 4)"));
            Assert.That(values, Has.Length.EqualTo(8));
            Assert.That(values.All(static value => Math.Abs(value - 3000f) < 1f), Is.True);
            Assert.That(exports[0].Minimum, Is.EqualTo(3000f).Within(1f));
            Assert.That(exports[1].Maximum, Is.EqualTo(10f).Within(1e-3f));
            Assert.Throws<ArgumentException>(() =>
                session.ExportRegion(new VoxelBox(new Int3(0, 0, 0), new Int3(1, 0, 0)), ["pressure", "temperature"], Path.Combine(_directory, "same.npy")));
        });
    }

    [Test]
    public void ExportSlice_ReturnsVUShapeAndNaNOutsideChunks()
    {
        using var session = CreateSession();
        session.AddChunk(new Int3(3, 0, 0), 2);

        var exports = session.ExportSlice(SliceAxis.Z, 0, ["classification"], Path.Combine(_directory, "slice.npy"));

        var (header, values, _) = ReadNpy(exports[0].Path!);
        Assert.Multiple(() =>
        {
            Assert.That(exports[0].Shape, Is.EqualTo(new[] { 3, 16 }));
            Assert.That(header, Does.Contain("'shape': (3, 16)"));
            Assert.That(values.Take(8).All(static value => value == 1f), Is.True);
            Assert.That(values.Skip(8).Take(4).All(float.IsNaN), Is.True, "chunk (2, 0, 0) does not exist");
            Assert.That(values.Skip(12).Take(4).All(static value => value == 2f), Is.True);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                session.ExportSlice(SliceAxis.Z, 5, ["classification"], Path.Combine(_directory, "bad.npy")));
        });
    }

    [Test]
    public void ExportSlice_UsesSimDrawerAxesForAnXSlice()
    {
        using var session = CreateSession();
        session.AddChunk(new Int3(0, 0, 1), 2);

        var exports = session.ExportSlice(SliceAxis.X, 0, ["classification"], Path.Combine(_directory, "x.npy"));

        var (header, values, _) = ReadNpy(exports[0].Path!);
        var (width, height) = SimulationFrameBuilder.GetSliceDimensions(new Int3(1, 3, 2), SliceAxis.X);
        Assert.Multiple(() =>
        {
            Assert.That((width, height), Is.EqualTo((2, 3)), "u runs along z and v along y");
            Assert.That(exports[0].Shape, Is.EqualTo(new[] { 3, 2 }));
            Assert.That(header, Does.Contain("'shape': (3, 2)"));
            Assert.That(values, Is.EqualTo(new[] { 1f, 2f, 1f, 2f, 1f, 2f }));
        });
    }

    [Test]
    public void RenderSlice_AutomaticRangeIgnoresSolidVoxels()
    {
        using var session = CreateSession();
        session.AddChunk(new Int3(2, 0, 0), VoxelClassification.RoomSolid);
        session.EditRegion(new VoxelBox(new Int3(4, 0, 0), new Int3(7, 2, 0)), new RegionEdit(
            ClearGas: true,
            Gases: [new RegionGas(0, null, 6000f)],
            GasTemperatureK: 300f));

        var image = session.RenderSlice(SliceAxis.Z, 0, BuiltInVisualizationIds.Pressure);

        Assert.Multiple(() =>
        {
            Assert.That(image.RangeMinimum, Is.EqualTo(3000f).Within(30f));
            Assert.That(image.RangeMaximum, Is.EqualTo(6000f).Within(60f));
        });
    }

    [Test]
    public void ProbeRecorder_WritesHeaderAndRowsWithoutChangingTheSimulation()
    {
        using var session = CreateSession();
        string path = Path.Combine(_directory, "probes.csv");
        var probes = new[] { new ProbePoint("a", new Int3(0, 0, 0)), new ProbePoint("b", new Int3(7, 2, 0)) };
        ulong before = session.World!.ComputeStateHash().Digest;

        using (var recorder = session.CreateProbeRecorder(probes, ["pressure", "temperature"], path))
        {
            recorder.Record();
            session.Tick(4, 0, done =>
            {
                if (done % 2 == 0)
                    recorder.Record();
            });
        }

        string[] lines = File.ReadAllLines(path);
        string[] first = lines[1].Split(',');
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("tick,timeSeconds,a.pressure,a.temperature,b.pressure,b.temperature"));
            Assert.That(lines, Has.Length.EqualTo(4));
            Assert.That(first, Has.Length.EqualTo(6));
            Assert.That(first[0], Is.EqualTo("0"));
            Assert.That(lines[3].Split(',')[0], Is.EqualTo("4"));
            Assert.That(lines[3].Split(',')[1], Is.EqualTo("0.2"));
            Assert.That(before, Is.Not.Zero);
            Assert.Throws<ArgumentException>(() =>
                session.CreateProbeRecorder([new ProbePoint("a,b", new Int3(0, 0, 0))], ["pressure"], path));
        });
    }

    [Test]
    public void RenderSlice_ProducesAnImageWithVisualizationColors()
    {
        using var session = CreateSession();
        session.EditRegion(new VoxelBox(new Int3(0, 0, 0), new Int3(3, 2, 0)), new RegionEdit(
            ClearGas: true,
            Gases: [new RegionGas(0, null, 6000f)],
            GasTemperatureK: 300f));

        var image = session.RenderSlice(SliceAxis.Z, 0, BuiltInVisualizationIds.Pressure, null, 2, 64);

        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(16));
            Assert.That(image.Height, Is.EqualTo(6));
            Assert.That(image.Rgba, Has.Length.EqualTo(16 * 6 * 4));
            Assert.That(image.RangeMaximum, Is.GreaterThan(image.RangeMinimum));
            Assert.That(image.Rgba.Where((_, index) => index % 4 == 3).All(static alpha => alpha == 255), Is.True);

            // The left half holds twice the pressure of the right half, so the colors differ.
            Assert.That(image.Rgba.AsSpan(0, 3).SequenceEqual(image.Rgba.AsSpan((16 - 1) * 4, 3)), Is.False);
            Assert.Throws<KeyNotFoundException>(() => session.RenderSlice(SliceAxis.Z, 0, "nope"));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.RenderSlice(SliceAxis.Z, 0, "pressure", null, 0));
        });
    }

    [Test]
    public void PngEncoder_WritesValidChunksAndPixels()
    {
        byte[] rgba =
        [
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
            10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120
        ];

        byte[] png = PngEncoder.Encode(3, 2, rgba);

        Assert.That(png.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        var chunks = new List<(string Type, byte[] Data)>();
        int offset = 8;
        while (offset < png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png.AsSpan(offset + 8, length).ToArray();
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
            Assert.That(stored, Is.EqualTo(Crc32(png.AsSpan(offset + 4, 4 + length))), $"CRC of {type}");
            chunks.Add((type, data));
            offset += 12 + length;
        }

        var header = chunks[0].Data;
        using var inflate = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        byte[] scanlines = raw.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(chunks.Select(static chunk => chunk.Type), Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)), Is.EqualTo(3u));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)), Is.EqualTo(2u));
            Assert.That(header[8], Is.EqualTo(8));
            Assert.That(header[9], Is.EqualTo(6));
            Assert.That(scanlines, Has.Length.EqualTo(2 * (1 + 3 * 4)));
            Assert.That(scanlines[0], Is.Zero);
            Assert.That(scanlines.AsSpan(1, 12).ToArray(), Is.EqualTo(rgba[..12]));
            Assert.That(scanlines.AsSpan(14, 12).ToArray(), Is.EqualTo(rgba[12..]));
            Assert.Throws<ArgumentException>(() => PngEncoder.Encode(3, 2, rgba[..20]));
        });
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
