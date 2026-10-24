using System.Numerics;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class ChunkMeshBuilderTests
{
    [Test]
    public void Build_SingleVoxel_ProducesSixFacesOfTwoTriangles()
    {
        var chunk = BuildChunk(new Int3(0, 0, 0), new Int3(1, 1, 1));

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        Assert.Multiple(() =>
        {
            Assert.That(mesh.VertexCount, Is.EqualTo(36));
            Assert.That(mesh.Positions.Length, Is.EqualTo(36 * 3));
            Assert.That(mesh.Colors.Length, Is.EqualTo(36 * 4));
            Assert.That(mesh.Positions.Min(), Is.EqualTo(0f));
            Assert.That(mesh.Positions.Max(), Is.EqualTo(1f));
        });
    }

    [Test]
    public void Build_TrianglesWindCounterClockwiseSeenFromOutside()
    {
        var chunk = BuildChunk(new Int3(0, 0, 0), new Int3(1, 1, 1));
        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);
        var center = new Vector3(0.5f);

        for (int triangle = 0; triangle < mesh.VertexCount / 3; triangle++)
        {
            var a = GetVertex(mesh, triangle * 3);
            var b = GetVertex(mesh, triangle * 3 + 1);
            var c = GetVertex(mesh, triangle * 3 + 2);
            var normal = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) / 3f - center;

            Assert.That(Vector3.Dot(normal, outward), Is.GreaterThan(0f), $"triangle {triangle}");
            Assert.That(normal.Length(), Is.EqualTo(1f).Within(1e-6f), $"triangle {triangle}");
        }
    }

    [Test]
    public void Build_AdjacentVoxels_CullTheSharedFace()
    {
        var chunk = BuildChunk(new Int3(0, 0, 0), new Int3(2, 1, 1));

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        // Ten of the twelve faces remain, and nothing is left on the plane x = 1 between the voxels.
        Assert.That(mesh.VertexCount, Is.EqualTo(10 * ChunkMeshBuilder.VerticesPerFace));
        for (int triangle = 0; triangle < mesh.VertexCount / 3; triangle++)
        {
            var a = GetVertex(mesh, triangle * 3);
            var b = GetVertex(mesh, triangle * 3 + 1);
            var c = GetVertex(mesh, triangle * 3 + 2);
            bool onSeam = a.X == 1f && b.X == 1f && c.X == 1f;
            Assert.That(onSeam, Is.False, $"triangle {triangle}");
        }
    }

    [Test]
    public void Build_SolidBlock_KeepsOnlyTheSurface()
    {
        var chunk = BuildChunk(new Int3(0, 0, 0), new Int3(3, 3, 3));

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        // 27 voxels would need 972 vertices. Six 3x3 sides leave 54 faces.
        Assert.That(mesh.VertexCount, Is.EqualTo(54 * ChunkMeshBuilder.VerticesPerFace));
        Assert.That(ChunkMeshBuilder.CountVertices(chunk), Is.EqualTo(mesh.VertexCount));
    }

    [Test]
    public void Build_FilteredVoxel_ExposesTheNeighborFacesNextToIt()
    {
        var chunk = BuildChunk(
            new Int3(0, 0, 0),
            new Int3(3, 1, 1),
            roomOf: index => index == 1 ? VoxelClassification.RoomSolid : 1);

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        // Two visible voxels with six exposed faces each.
        Assert.That(mesh.VertexCount, Is.EqualTo(12 * ChunkMeshBuilder.VerticesPerFace));
    }

    [Test]
    public void Build_NoVisibleVoxel_ProducesEmptyArrays()
    {
        var chunk = BuildChunk(
            new Int3(0, 0, 0),
            new Int3(2, 2, 1),
            roomOf: _ => VoxelClassification.RoomSolid);

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        Assert.Multiple(() =>
        {
            Assert.That(mesh.VertexCount, Is.Zero);
            Assert.That(mesh.Positions, Is.Empty);
            Assert.That(mesh.Colors, Is.Empty);
        });
    }

    [Test]
    public void Build_ChunkAwayFromTheOrigin_PlacesVerticesInWorldCoordinates()
    {
        var chunk = BuildChunk(new Int3(-1, 2, 0), new Int3(2, 3, 4));

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        var xs = Enumerable.Range(0, mesh.VertexCount).Select(index => mesh.Positions[index * 3]).ToArray();
        var ys = Enumerable.Range(0, mesh.VertexCount).Select(index => mesh.Positions[index * 3 + 1]).ToArray();
        var zs = Enumerable.Range(0, mesh.VertexCount).Select(index => mesh.Positions[index * 3 + 2]).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That((xs.Min(), xs.Max()), Is.EqualTo((-2f, 0f)));
            Assert.That((ys.Min(), ys.Max()), Is.EqualTo((6f, 9f)));
            Assert.That((zs.Min(), zs.Max()), Is.EqualTo((0f, 4f)));
        });
    }

    [Test]
    public void Build_Colors_FollowTheVoxelThatOwnsEachVertex()
    {
        var chunk = BuildChunk(
            new Int3(0, 0, 0),
            new Int3(2, 1, 1),
            temperatureOf: index => index == 0 ? 250f : 450f);

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        var cold = chunk.GetCell(0).Color.ToRgba8();
        var hot = chunk.GetCell(1).Color.ToRgba8();
        Assert.That(cold, Is.Not.EqualTo(hot));

        // Vertices run in cell order. Each voxel keeps five faces, because they share the sixth.
        const int verticesPerVoxel = 5 * ChunkMeshBuilder.VerticesPerFace;
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var expected = vertex < verticesPerVoxel ? cold : hot;
            var actual = (
                mesh.Colors[vertex * 4],
                mesh.Colors[vertex * 4 + 1],
                mesh.Colors[vertex * 4 + 2],
                mesh.Colors[vertex * 4 + 3]);

            Assert.That(actual, Is.EqualTo(expected), $"vertex {vertex}");
        }
    }

    [Test]
    public void Build_UsesTheChannelConversionOfRaylibColors()
    {
        var chunk = BuildChunk(new Int3(0, 0, 0), new Int3(1, 1, 1), temperatureOf: _ => 300f);
        var color = chunk.GetCell(0).Color;

        var mesh = ChunkMeshBuilder.Build(chunk, transparent: false);

        // Raylib builds a Color from floats by truncating channel * 255.
        Assert.That(mesh.Colors[0], Is.EqualTo((byte)(Math.Clamp(color.R, 0f, 1f) * 255f)));
        Assert.That(mesh.Colors[1], Is.EqualTo((byte)(Math.Clamp(color.G, 0f, 1f) * 255f)));
        Assert.That(mesh.Colors[2], Is.EqualTo((byte)(Math.Clamp(color.B, 0f, 1f) * 255f)));
        Assert.That(mesh.Colors[3], Is.EqualTo(byte.MaxValue));
    }

    [Test]
    public void Build_Transparent_SetsEveryAlphaButKeepsTheColor()
    {
        var chunk = BuildChunk(new Int3(0, 0, 0), new Int3(2, 2, 1));

        var opaque = ChunkMeshBuilder.Build(chunk, transparent: false);
        var transparent = ChunkMeshBuilder.Build(chunk, transparent: true);

        Assert.That(transparent.Positions, Is.EqualTo(opaque.Positions));
        for (int vertex = 0; vertex < opaque.VertexCount; vertex++)
        {
            Assert.That(transparent.Colors[vertex * 4 + 3], Is.EqualTo(ChunkMeshBuilder.TransparentAlpha));
            Assert.That(transparent.Colors[vertex * 4], Is.EqualTo(opaque.Colors[vertex * 4]));
            Assert.That(transparent.Colors[vertex * 4 + 1], Is.EqualTo(opaque.Colors[vertex * 4 + 1]));
            Assert.That(transparent.Colors[vertex * 4 + 2], Is.EqualTo(opaque.Colors[vertex * 4 + 2]));
        }
    }

    [Test]
    public void WriteColors_MatchesTheColorsOfBuild()
    {
        var chunk = BuildChunk(
            new Int3(0, 0, 0),
            new Int3(3, 2, 2),
            temperatureOf: index => 200f + index * 20f);

        var built = ChunkMeshBuilder.Build(chunk, transparent: true);
        var colors = new byte[built.VertexCount * 4];
        int written = ChunkMeshBuilder.WriteColors(chunk, transparent: true, colors);

        Assert.That(written, Is.EqualTo(built.VertexCount));
        Assert.That(colors, Is.EqualTo(built.Colors));
    }

    [Test]
    public void CountVertices_Checkerboard_NeedsSixFacesPerVisibleVoxel()
    {
        var dimensions = new Int3(8, 8, 8);
        var chunk = BuildChunk(
            new Int3(0, 0, 0),
            dimensions,
            roomOf: index =>
            {
                int x = index % 8;
                int y = index / 8 % 8;
                int z = index / 64;
                return (x + y + z) % 2 == 0 ? 1 : VoxelClassification.RoomSolid;
            });

        int vertices = ChunkMeshBuilder.CountVertices(chunk);

        // No two visible voxels touch, so no face is culled.
        Assert.That(vertices, Is.EqualTo(256 * 6 * ChunkMeshBuilder.VerticesPerFace));
    }

    private static Vector3 GetVertex(ChunkMeshData mesh, int vertex)
    {
        return new Vector3(mesh.Positions[vertex * 3], mesh.Positions[vertex * 3 + 1], mesh.Positions[vertex * 3 + 2]);
    }

    private static ChunkDrawData BuildChunk(
        Int3 position,
        Int3 dimensions,
        Func<int, int>? roomOf = null,
        Func<int, float>? temperatureOf = null)
    {
        int count = dimensions.X * dimensions.Y * dimensions.Z;
        var snapshot = new AtmosChunkSnapshot
        {
            Version = new AtmosChunkVersion(1, 1),
            GridPosition = position,
            Dimensions = dimensions,
            TotalPressure = Enumerable.Repeat(100f, count).ToArray(),
            Temperature = Enumerable.Range(0, count).Select(index => temperatureOf?.Invoke(index) ?? 293.15f).ToArray(),
            VoxelRoomMap = Enumerable.Range(0, count).Select(index => roomOf?.Invoke(index) ?? 1).ToArray(),
            Gases = []
        };

        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var frame = builder.BuildSimulation([snapshot], BuiltInVisualizationIds.Temperature, 1);
        return frame.Chunks[position];
    }
}
