using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using static Numos.Headless.Tests.HeadlessTestSupport;

namespace Numos.Headless.Tests;

[TestFixture]
public sealed class HeadlessExtractionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, true);
    }

    private string PathFor(string name)
    {
        return Path.Combine(_directory, name).Replace('\\', '/');
    }

    /// <summary>
    ///     An 8 x 3 duct with solid walls in rows 0 and 2. The left half holds 6000 Pa and the right half 3000 Pa.
    ///     Voxel volume 8.31446262 m^3 makes the moles per voxel equal pressure divided by temperature.
    /// </summary>
    private static string[] DuctSetup()
    {
        return
        [
            CreateProject("create", 4, 3, 1),
            Request("walls", "editRegion", $"\"min\":{C(0, 0, 0)},\"max\":{C(7, 2, 0)},\"createChunks\":true,\"classification\":-2"),
            Request("tube", "editRegion", $"\"min\":{C(0, 1, 0)},\"max\":{C(7, 1, 0)},\"classification\":1,\"temperatureK\":300"),
            Request("left", "editRegion", $"\"min\":{C(0, 1, 0)},\"max\":{C(3, 1, 0)},\"gas\":[{{\"gasId\":0,\"partialPressurePa\":6000}}],\"gasTemperatureK\":300"),
            Request("right", "editRegion", $"\"min\":{C(4, 1, 0)},\"max\":{C(7, 1, 0)},\"gas\":[{{\"gasId\":0,\"partialPressurePa\":3000}}],\"gasTemperatureK\":300")
        ];
    }

    private static (string Header, float[] Values) ReadNpy(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.That(bytes[0], Is.EqualTo(0x93));
        Assert.That(Encoding.ASCII.GetString(bytes, 1, 5), Is.EqualTo("NUMPY"));
        Assert.That((bytes[6], bytes[7]), Is.EqualTo(((byte)1, (byte)0)));
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        Assert.That((10 + headerLength) % 64, Is.Zero);
        string header = Encoding.ASCII.GetString(bytes, 10, headerLength);
        Assert.That(header, Does.StartWith("{'descr': '<f4', 'fortran_order': False, 'shape': ("));
        Assert.That(header, Does.EndWith("\n"));
        float[] values = new float[(bytes.Length - 10 - headerLength) / 4];
        Buffer.BlockCopy(bytes, 10 + headerLength, values, 0, values.Length * 4);
        return (header, values);
    }

    [Test]
    public async Task ExportField_RegionWritesNpyInZYXOrder()
    {
        string template = PathFor("duct-{field}.npy");
        var run = await RunAsync(
            [.. DuctSetup(),
                Request(
                    "export",
                    "exportField",
                    $"\"fields\":[\"pressure\",\"temperature\"],\"region\":{{\"min\":{C(0, 0, 0)},\"max\":{C(7, 2, 0)}}},\"path\":\"{template}\"")]);

        var files = run.Result("export").GetProperty("export").GetProperty("files");
        var (header, pressure) = ReadNpy(files[0].GetProperty("path").GetString()!);
        var (_, temperature) = ReadNpy(files[1].GetProperty("path").GetString()!);

        // Shape (z, y, x) = (1, 3, 8). Rows 0 and 2 are solid walls.
        Assert.Multiple(() =>
        {
            Assert.That(run.Result("export").GetProperty("export").GetProperty("format").GetString(), Is.EqualTo("npy"));
            Assert.That(header, Does.Contain("'shape': (1, 3, 8)"));
            Assert.That(files[0].GetProperty("shape").EnumerateArray().Select(static e => e.GetInt32()), Is.EqualTo(new[] { 1, 3, 8 }));
            Assert.That(pressure, Has.Length.EqualTo(24));
            Assert.That(pressure.Take(8).All(float.IsNaN), Is.True);
            Assert.That(pressure.Skip(16).All(float.IsNaN), Is.True);
            Assert.That(pressure.Skip(8).Take(4).All(static v => Math.Abs(v - 6000f) < 1f), Is.True);
            Assert.That(pressure.Skip(12).Take(4).All(static v => Math.Abs(v - 3000f) < 1f), Is.True);
            Assert.That(temperature[8], Is.EqualTo(300f).Within(0.01f));
            Assert.That(files[0].GetProperty("min").GetSingle(), Is.EqualTo(3000f).Within(1f));
            Assert.That(files[0].GetProperty("max").GetSingle(), Is.EqualTo(6000f).Within(1f));
            Assert.That(files[0].GetProperty("path").GetString(), Does.EndWith("duct-pressure.npy"));
        });
    }

    [Test]
    public async Task ExportField_SliceWritesTwoDimensionalVUArray()
    {
        string template = PathFor("slice-{field}.npy");
        var run = await RunAsync(
            [.. DuctSetup(),
                Request("export", "exportField", $"\"fields\":[\"moles:0\"],\"slice\":{{\"axis\":\"z\",\"index\":0}},\"path\":\"{template}\""),
                Request("yslice", "exportField", $"\"fields\":[\"classification\"],\"slice\":{{\"axis\":\"y\",\"index\":1}},\"path\":\"{PathFor("y.npy")}\""),
                Request("badAxis", "exportField", $"\"fields\":[\"moles:0\"],\"slice\":{{\"axis\":\"w\",\"index\":0}},\"path\":\"{template}\""),
                Request("badIndex", "exportField", $"\"fields\":[\"moles:0\"],\"slice\":{{\"axis\":\"z\",\"index\":9}},\"path\":\"{template}\""),
                Request("both", "exportField", $"\"fields\":[\"moles:0\"],\"slice\":{{\"axis\":\"z\",\"index\":0}},\"region\":{{\"min\":{C(0, 0, 0)},\"max\":{C(1, 0, 0)}}},\"path\":\"{template}\""),
                Request("unknown", "exportField", $"\"fields\":[\"density\"],\"slice\":{{\"axis\":\"z\",\"index\":0}},\"path\":\"{template}\"")]);

        var file = run.Result("export").GetProperty("export").GetProperty("files")[0];
        var (header, moles) = ReadNpy(file.GetProperty("path").GetString()!);
        var (yHeader, classes) = ReadNpy(PathFor("y.npy"));
        Assert.Multiple(() =>
        {
            Assert.That(header, Does.Contain("'shape': (3, 8)"));
            Assert.That(file.GetProperty("path").GetString(), Does.EndWith("slice-moles_0.npy"));
            Assert.That(moles[8], Is.EqualTo(20f).Within(1e-3f));
            Assert.That(moles[8 + 4], Is.EqualTo(10f).Within(1e-3f));
            Assert.That(float.IsNaN(moles[0]), Is.True);
            Assert.That(yHeader, Does.Contain("'shape': (1, 8)"));
            Assert.That(classes, Is.EqualTo(Enumerable.Repeat(1f, 8)));
            Assert.That(run.ErrorCode("badAxis"), Is.EqualTo("invalidRequest"));
            Assert.That(run.ErrorCode("badIndex"), Is.EqualTo("operationRejected"));
            Assert.That(run.ErrorCode("both"), Is.EqualTo("invalidRequest"));
            Assert.That(run.ErrorCode("unknown"), Is.EqualTo("fieldNotFound"));
        });
    }

    [Test]
    public async Task ExportField_JsonReturnsValuesInlineAndCapsLargeExtents()
    {
        var run = await RunAsync(
            [.. DuctSetup(),
                Request("inline", "exportField", $"\"fields\":[\"pressure\"],\"region\":{{\"min\":{C(3, 1, 0)},\"max\":{C(4, 1, 0)}}},\"format\":\"json\""),
                Request("slice", "exportField", $"\"fields\":[\"classification\"],\"slice\":{{\"axis\":\"z\",\"index\":0}},\"format\":\"json\""),
                Request("huge", "exportField", $"\"fields\":[\"pressure\"],\"region\":{{\"min\":{C(0, 0, 0)},\"max\":{C(1000, 1000, 0)}}},\"format\":\"json\""),
                Request("format", "exportField", $"\"fields\":[\"pressure\"],\"region\":{{\"min\":{C(0, 0, 0)},\"max\":{C(1, 0, 0)}}},\"format\":\"csv\""),
                Request("nopath", "exportField", $"\"fields\":[\"pressure\"],\"region\":{{\"min\":{C(0, 0, 0)},\"max\":{C(1, 0, 0)}}}")]);

        var file = run.Result("inline").GetProperty("export").GetProperty("files")[0];
        var slice = run.Result("slice").GetProperty("export").GetProperty("files")[0];
        Assert.Multiple(() =>
        {
            Assert.That(file.TryGetProperty("path", out _), Is.False);
            Assert.That(file.GetProperty("values").GetArrayLength(), Is.EqualTo(2));
            Assert.That(file.GetProperty("values")[0].GetSingle(), Is.EqualTo(6000f).Within(1f));
            Assert.That(file.GetProperty("values")[1].GetSingle(), Is.EqualTo(3000f).Within(1f));
            Assert.That(slice.GetProperty("shape").EnumerateArray().Select(static e => e.GetInt32()), Is.EqualTo(new[] { 3, 8 }));
            Assert.That(slice.GetProperty("values")[8].GetSingle(), Is.EqualTo(1f));
            Assert.That(run.ErrorCode("huge"), Is.EqualTo("invalidRequest"));
            Assert.That(run.ErrorCode("format"), Is.EqualTo("invalidRequest"));
            Assert.That(run.ErrorCode("nopath"), Is.EqualTo("missingProperty"));
        });
    }

    [Test]
    public async Task Tick_WithProbesWritesACsvRowEveryNTicks()
    {
        string path = PathFor("probes.csv");
        var run = await RunAsync(
            [.. DuctSetup(),
                Request(
                    "tick",
                    "tick",
                    "\"count\":10,\"probes\":{\"points\":[" +
                    $"{{\"name\":\"left\",\"voxel\":{C(2, 1, 0)}}},{{\"name\":\"right\",\"voxel\":{C(5, 1, 0)}}}]," +
                    $"\"fields\":[\"pressure\",\"moles:0\"],\"path\":\"{path}\",\"every\":5}}"),
                Request("bad", "tick", $"\"count\":1,\"probes\":{{\"points\":[],\"fields\":[\"pressure\"],\"path\":\"{path}\"}}"),
                Request("every", "tick", $"\"count\":1,\"probes\":{{\"points\":[{{\"name\":\"a\",\"voxel\":{C(2, 1, 0)}}}],\"fields\":[\"pressure\"],\"path\":\"{path}\",\"every\":0}}")]);

        string[] lines = File.ReadAllLines(path);
        var probes = run.Result("tick").GetProperty("probes");
        Assert.Multiple(() =>
        {
            Assert.That(run.Result("tick").GetProperty("ticksExecuted").GetInt32(), Is.EqualTo(10));
            Assert.That(probes.GetProperty("rows").GetInt32(), Is.EqualTo(3));
            Assert.That(probes.GetProperty("columns").GetInt32(), Is.EqualTo(6));
            Assert.That(lines[0], Is.EqualTo("tick,timeSeconds,left.pressure,left.moles:0,right.pressure,right.moles:0"));
            Assert.That(lines.Select(static line => line.Split(',')[0]), Is.EqualTo(new[] { "tick", "0", "5", "10" }));
            Assert.That(lines.All(static line => line.Split(',').Length == 6), Is.True);
            Assert.That(float.Parse(lines[1].Split(',')[2], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(6000f).Within(1f));
            Assert.That(float.Parse(lines[3].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(0.5f));
            Assert.That(run.ErrorCode("bad"), Is.EqualTo("missingProperty"));
            Assert.That(run.ErrorCode("every"), Is.EqualTo("invalidRequest"));
        });
    }

    [Test]
    public async Task Tick_WithProbesDoesNotChangeTheSimulationState()
    {
        var plain = await RunAsync(
            [.. DuctSetup(), Request("tick", "tick", "\"count\":12"), Request("hash", "timelineInfo")]);

        var probed = await RunAsync(
            [.. DuctSetup(),
                Request(
                    "tick",
                    "tick",
                    $"\"count\":12,\"probes\":{{\"points\":[{{\"name\":\"a\",\"voxel\":{C(3, 1, 0)}}}],\"fields\":[\"pressure\"],\"path\":\"{PathFor("p.csv")}\"}}"),
                Request("hash", "timelineInfo")]);

        Assert.That(
            probed.Result("hash").GetProperty("timeline").GetProperty("stateHash").GetString(),
            Is.EqualTo(plain.Result("hash").GetProperty("timeline").GetProperty("stateHash").GetString()));
    }

    [Test]
    public async Task RenderSlice_WritesAPngWithMatchingDimensions()
    {
        string path = PathFor("render/duct.png");
        var run = await RunAsync(
            [.. DuctSetup(),
                Request(
                    "render",
                    "renderSlice",
                    $"\"axis\":\"z\",\"index\":0,\"visualization\":\"pressure\",\"path\":\"{path}\",\"scale\":3"),
                Request(
                    "range",
                    "renderSlice",
                    $"\"axis\":\"z\",\"index\":0,\"visualization\":\"temperature\",\"path\":\"{PathFor("t.png")}\",\"range\":{{\"min\":200,\"max\":400}}"),
                Request("bad", "renderSlice", $"\"axis\":\"z\",\"index\":0,\"visualization\":\"nope\",\"path\":\"{path}\""),
                Request("scale", "renderSlice", $"\"axis\":\"z\",\"index\":0,\"path\":\"{path}\",\"scale\":0"),
                Request("nopath", "renderSlice", "\"axis\":\"z\",\"index\":0")]);

        var render = run.Result("render").GetProperty("render");
        byte[] png = File.ReadAllBytes(path);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));

        // IDAT follows the 25-byte IHDR chunk: 8 signature + 25 IHDR, then length, type, data.
        int idatLength = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(33, 4));
        using var inflate = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        Assert.Multiple(() =>
        {
            Assert.That(png.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Assert.That(Encoding.ASCII.GetString(png, 12, 4), Is.EqualTo("IHDR"));
            Assert.That(width, Is.EqualTo(24u));
            Assert.That(height, Is.EqualTo(9u));
            Assert.That(png[24], Is.EqualTo(8));
            Assert.That(png[25], Is.EqualTo(6));
            Assert.That(raw.Length, Is.EqualTo(9 * (1 + 24 * 4)));
            Assert.That(render.GetProperty("width").GetInt32(), Is.EqualTo(24));
            Assert.That(render.GetProperty("height").GetInt32(), Is.EqualTo(9));
            Assert.That(render.GetProperty("path").GetString(), Does.EndWith("duct.png"));
            Assert.That(run.Result("range").GetProperty("render").GetProperty("rangeMin").GetSingle(), Is.EqualTo(200f));
            Assert.That(run.Result("range").GetProperty("render").GetProperty("rangeMax").GetSingle(), Is.EqualTo(400f));
            Assert.That(run.ErrorCode("bad"), Is.EqualTo("operationRejected"));
            Assert.That(run.ErrorCode("scale"), Is.EqualTo("invalidRequest"));
            Assert.That(run.ErrorCode("nopath"), Is.EqualTo("missingProperty"));
        });
    }
}
