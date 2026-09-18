namespace Numos.API.Dangerous;

/// <summary>
///     Adds opt-in live storage access to world solver callbacks.
/// </summary>
public static class AtmosWorldSolverDangerousExtensions
{
    /// <summary>
    ///     Opens the low-level world solver API for the current callback.
    /// </summary>
    /// <param name="context">The active world solver context.</param>
    /// <returns>A resolver for live chunk storage addressed by world cell references.</returns>
    public static AtmosDangerousWorldSolverApi Dangerous(this AtmosWorldSolverContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AtmosDangerousWorldSolverApi(context);
    }
}

/// <summary>
///     Resolves world-addressed cells to unchecked live chunk views during a solver callback.
/// </summary>
public readonly ref struct AtmosDangerousWorldSolverApi
{
    private readonly AtmosWorldSolverContext _context;

    internal AtmosDangerousWorldSolverApi(AtmosWorldSolverContext context)
    {
        _context = context;
    }

    /// <summary>
    ///     Returns the live chunk containing a world-addressed cell.
    /// </summary>
    /// <param name="cell">A current cell reference from the callback's world.</param>
    /// <returns>The unchecked live chunk view.</returns>
    /// <exception cref="ArgumentException">The cell does not identify live storage in this world.</exception>
    public AtmosDangerousChunk GetChunk(AtmosCellRef cell)
    {
        if (!_context.World.TryGetSimulation(cell.Simulation, out var simulation) || simulation == null)
            throw new ArgumentException("The cell does not identify a live simulation in this world.", nameof(cell));

        int voxelCount = checked(
            simulation.ChunkDimensions.X *
            simulation.ChunkDimensions.Y *
            simulation.ChunkDimensions.Z);

        if (cell.LocalVoxelIndex >= voxelCount)
            throw new ArgumentException("The cell's local voxel index is outside its chunk.", nameof(cell));

        try
        {
            return simulation.Dangerous().GetChunk(cell.Chunk);
        }
        catch (KeyNotFoundException exception)
        {
            throw new ArgumentException("The cell does not identify a live chunk in this world.", nameof(cell), exception);
        }
    }
}