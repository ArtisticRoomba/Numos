namespace Numos.Collections;

/// <summary>
/// 2-3D to 1D mapping for an array.
/// </summary>
/// <typeparam name="T">The type of data to store in the array.</typeparam>
/// <para>
/// Super basic helper struct that wraps an array and allows for 2D/3D indexing in sim ctx.
/// Struct so that we don't double-lookup on the reference, as this is basically just an array extension.
/// </para>
/// <para>
/// Because this is wrapped in a struct with a stable surface exposed,
/// we can technically swap out the underlying impl whenever we want to microopt.
/// </para>
public struct FlatArray<T>(T[] data)
{
    /// <summary>
    /// Internal backing array.
    /// </summary>
    /// <remarks>Marked as private to prevent sim from relying on whatever mapping is in the array.
    /// Sim should access this via an API.</remarks>
    private T[] _data = data;

    // TODO API surface
}