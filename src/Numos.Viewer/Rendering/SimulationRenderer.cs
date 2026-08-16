using Numos.SimDrawer;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Handles OpenGL rendering of simulation data.
/// </summary>
public class SimulationRenderer : IDisposable
{
    private readonly GL _gl;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private uint _shaderProgram;
    private int _elementCount;
    private PrimitiveType _primitiveType = PrimitiveType.Triangles;

    public SimulationRenderer(GL gl)
    {
        _gl = gl;
        InitializeShaders();
    }

    private void InitializeShaders()
    {
        const string vertexShaderSource = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aColor;

out vec3 vertexColor;

uniform mat4 projection;
uniform mat4 view;
uniform mat4 model;

void main()
{
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
    vertexColor = aColor;
}
";

        const string fragmentShaderSource = @"
#version 330 core
in vec3 vertexColor;
out vec4 FragColor;

void main()
{
    FragColor = vec4(vertexColor, 1.0);
}
";

        uint vertexShader = CompileShader(vertexShaderSource, ShaderType.VertexShader);
        uint fragmentShader = CompileShader(fragmentShaderSource, ShaderType.FragmentShader);

        _shaderProgram = _gl.CreateProgram();
        _gl.AttachShader(_shaderProgram, vertexShader);
        _gl.AttachShader(_shaderProgram, fragmentShader);
        _gl.LinkProgram(_shaderProgram);

        _gl.GetProgram(_shaderProgram, GLEnum.LinkStatus, out int success);
        if (success == 0)
        {
            string infoLog = _gl.GetProgramInfoLog(_shaderProgram);
            Console.WriteLine($"ERROR::PROGRAM::LINKING_FAILED\n{infoLog}");
        }

        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
    }

    private uint CompileShader(string source, ShaderType type)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, GLEnum.CompileStatus, out int success);
        if (success == 0)
        {
            string infoLog = _gl.GetShaderInfoLog(shader);
            Console.WriteLine($"ERROR::SHADER::COMPILATION_FAILED\n{infoLog}");
        }

        return shader;
    }

    public void UpdateGeometry(
        Vertex[] vertices,
        uint[] indices,
        PrimitiveType primitiveType = PrimitiveType.Triangles)
    {
        _elementCount = indices.Length;
        _primitiveType = primitiveType;

        if (_vao == 0)
            _vao = _gl.GenVertexArray();
        if (_vbo == 0)
            _vbo = _gl.GenBuffer();
        if (_ebo == 0)
            _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (Vertex* v = vertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(Vertex)), v,
                    BufferUsageARB.DynamicDraw);
            }
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        unsafe
        {
            fixed (uint* i = indices)
            {
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), i,
                    BufferUsageARB.DynamicDraw);
            }
        }

        const uint positionLocation = 0;
        unsafe
        {
            _gl.VertexAttribPointer(positionLocation, 3, VertexAttribPointerType.Float, false,
                (uint)sizeof(Vertex), 0);
        }

        _gl.EnableVertexAttribArray(positionLocation);

        const uint colorLocation = 1;
        unsafe
        {
            _gl.VertexAttribPointer(colorLocation, 3, VertexAttribPointerType.Float, false,
                (uint)sizeof(Vertex), 12);
        }

        _gl.EnableVertexAttribArray(colorLocation);

        // Keep the EBO associated with the VAO.
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
    }

    public void ClearGeometry()
    {
        _elementCount = 0;
        _primitiveType = PrimitiveType.Triangles;
    }

    public void Render(
        Matrix4X4<float> projection,
        Matrix4X4<float> view,
        Matrix4X4<float> model)
    {
        if (_vao == 0 || _elementCount == 0 || _shaderProgram == 0)
            return;

        _gl.UseProgram(_shaderProgram);

        int projectionLocation = _gl.GetUniformLocation(_shaderProgram, "projection");
        int viewLocation = _gl.GetUniformLocation(_shaderProgram, "view");
        int modelLocation = _gl.GetUniformLocation(_shaderProgram, "model");

        unsafe
        {
            _gl.UniformMatrix4(projectionLocation, 1, false, (float*)&projection);
            _gl.UniformMatrix4(viewLocation, 1, false, (float*)&view);
            _gl.UniformMatrix4(modelLocation, 1, false, (float*)&model);
        }

        _gl.BindVertexArray(_vao);

        unsafe
        {
            _gl.DrawElements(
                _primitiveType,
                (uint)_elementCount,
                DrawElementsType.UnsignedInt,
                (void*)0);
        }

        _gl.BindVertexArray(0);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_vao != 0)
        {
            _gl.DeleteVertexArray(_vao);
            _vao = 0;
        }

        if (_vbo != 0)
        {
            _gl.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_ebo != 0)
        {
            _gl.DeleteBuffer(_ebo);
            _ebo = 0;
        }

        if (_shaderProgram != 0)
        {
            _gl.DeleteProgram(_shaderProgram);
            _shaderProgram = 0;
        }
    }
}