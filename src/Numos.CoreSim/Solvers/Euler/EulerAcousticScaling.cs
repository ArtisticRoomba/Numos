using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.EulerFlow;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Pressure-gradient scaling for the <c>euler-flow</c> stage: runs the unchanged sweep on a gas state whose
///     velocity is multiplied by a factor α, for <c>Δt / α</c> of time.
/// </summary>
/// <remarks>
///     <para>
///         For a uniform α ≥ 1 the Euler equations in the variables <c>ũ = α·u</c>, <c>Ẽ = ρe + ½ρ|ũ|²</c>, and
///         <c>t̃ = t / α</c> are the same equations with the same pressure, density, and temperature. The sweep
///         therefore needs no change. It sees a valid gas state and takes about α times fewer substeps. Mass and every
///         species stay exact, because <c>F̃ · Δt/α = F · Δt</c>.
///     </para>
///     <para>
///         A chunk stores real momentum together with the factor it was last written with
///         (<see cref="AtmosChunk.EulerMomentumScale" />). <see cref="ScaleUp" /> turns stored momentum into scaled
///         momentum before the gather, and <see cref="ScaleDown" /> turns the buffer's scaled momentum and energy back
///         into real ones before the write-back. When α rises the scaled velocity is carried over. When it falls the
///         real velocity is kept. Either choice only removes kinetic energy, so no sequence of changes can add any.
///     </para>
///     <para>
///         The factor comes from <see cref="ChooseScale" />, which measures the gathered real state. Each flow group
///         (see <see cref="EulerFlowGroups" />) picks its own. See the Euler section of
///         <c>docs/atmospherics_technical_documentation.md</c> for the rule and its limits.
///     </para>
/// </remarks>
internal static class EulerAcousticScaling
{
    /// <summary>
    ///     Whether a call may scale at all: the setting allows a factor above 1, or a test forces one. When it is
    ///     false the solver takes the single loop over the active list, with no groups and no scan.
    /// </summary>
    internal static bool IsEnabled(EulerFlowConfig settings, float? forced)
    {
        return forced.HasValue || settings.MaxSoundSpeedReduction > 1f;
    }

    /// <summary>
    ///     Picks the factor α for one group of workspaces from the gathered real state.
    /// </summary>
    /// <param name="members">The group's workspaces, gathered but not yet scaled.</param>
    /// <param name="count">Number of workspaces in <paramref name="members" />.</param>
    /// <param name="settings">The stage's settings.</param>
    /// <param name="forced">A factor that overrides the rule, or <see langword="null" />.</param>
    /// <returns>α, at least 1.</returns>
    /// <remarks>
    ///     Returns 1 without scanning when the feature is off and nothing is forced. Otherwise each workspace's
    ///     <see cref="EulerChunkWorkspace.PressureJump" /> and <see cref="EulerChunkWorkspace.CellSpeed" /> are
    ///     measured in parallel and reduced with a max, so the result does not depend on the worker count. The group
    ///     counts as slowed when any member chunk was last written with a factor above 1. A slowed group goes back to
    ///     full speed only above the thresholds, and a group at full speed slows down only below half of them. A
    ///     non-finite measurement counts as above the threshold, because <see cref="MathF.Max(float, float)" /> keeps a NaN.
    /// </remarks>
    internal static float ChooseScale(
        EulerChunkWorkspace[] members, int count, EulerFlowConfig settings, float? forced)
    {
        if (forced.HasValue)
            return Math.Clamp(forced.Value, 1f, 16f);

        float max = settings.MaxSoundSpeedReduction;
        if (!(max > 1f) || count == 0)
            return 1f;

        MeasureAll(members, count);
        return Decide(members, count, settings);
    }

    /// <summary>
    ///     Measures every workspace in parallel, filling <see cref="EulerChunkWorkspace.PressureJump" /> and
    ///     <see cref="EulerChunkWorkspace.CellSpeed" />. One call can cover the members of several groups.
    /// </summary>
    internal static void MeasureAll(EulerChunkWorkspace[] members, int count)
    {
        if (count > 0)
            ParallelPhase.Run(count, new MeasureAction(members));
    }

    /// <summary>
    ///     Reduces already measured workspaces to a factor, with the hysteresis described on
    ///     <see cref="ChooseScale" />. Does not measure and does not look at a forced factor.
    /// </summary>
    /// <returns>α, at least 1.</returns>
    internal static float Decide(EulerChunkWorkspace[] members, int count, EulerFlowConfig settings)
    {
        float max = settings.MaxSoundSpeedReduction;
        if (!(max > 1f) || count == 0)
            return 1f;

        Pascal jump = 0f;
        MetrePerSecond speed = 0f;
        bool slowed = false;
        for (int index = 0; index < count; index++)
        {
            EulerChunkWorkspace workspace = members[index];
            jump = MathF.Max(jump, workspace.PressureJump);
            speed = MathF.Max(speed, workspace.CellSpeed);
            slowed |= workspace.Chunk.EulerMomentumScale > 1f;
        }

        // Written so that a NaN fails the "calm" test in both directions.
        if (slowed)
            return jump <= settings.FullSpeedPressureJump && speed <= settings.FullSpeedVelocity ? max : 1f;

        return jump < 0.5f * settings.FullSpeedPressureJump && speed < 0.5f * settings.FullSpeedVelocity ? max : 1f;
    }

    /// <summary>
    ///     Fills <see cref="EulerChunkWorkspace.PressureJump" /> and <see cref="EulerChunkWorkspace.CellSpeed" /> from
    ///     the workspace's read buffer.
    /// </summary>
    /// <remarks>
    ///     The jump follows the faces the sweep solves. An interior face counts when both open bits agree, which the
    ///     face masks already encode together with voxel face blocking. A chunk face counts against the neighbour's
    ///     gathered pressure when it has a workspace and against its live pressure when it has none, as the sweep and
    ///     the wake scan do. A face into a void cell compares with zero. A trace or vacuum cell already gathers as
    ///     pressure zero. Reads other workspaces' read buffers only, and writes only this workspace.
    /// </remarks>
    internal static void Measure(EulerChunkWorkspace workspace)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerChunkState read = workspace.Read;
        Int3 dimensions = chunk.Dimensions;
        Pascal jump = 0f;
        MetrePerSecond speed = 0f;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Pascal pressure = read.CellPressure[voxelIndex];
            MetrePerSecond cellSpeed = MathF.Max(
                MathF.Abs(read.CellVelocityX[voxelIndex]),
                MathF.Max(MathF.Abs(read.CellVelocityY[voxelIndex]), MathF.Abs(read.CellVelocityZ[voxelIndex])));
            speed = MathF.Max(speed, cellSpeed);

            byte open = workspace.OpenMask[voxelIndex];
            byte sink = workspace.VoidMask[voxelIndex];
            if (sink != 0)
                jump = MathF.Max(jump, MathF.Abs(pressure));

            if (open == 0)
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (!EulerFaceTopology.IsOpen(open, direction))
                    continue;

                Int3 neighborPosition = position + NeighborResolution.NeighborDirections[direction];
                Pascal neighborPressure;
                if (neighborPosition.IsWithin(dimensions))
                {
                    // Each interior face is seen from its lower voxel.
                    if ((direction & 1) == 0)
                        continue;

                    neighborPressure = read.CellPressure[chunk.GetIndexUnsafe(neighborPosition)];
                }
                else
                {
                    ushort neighborIndex = EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction);
                    EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
                    neighborPressure = neighbor != null
                        ? neighbor.Read.CellPressure[neighborIndex]
                        : workspace.NeighborChunks[direction]!.TotalPressure[neighborIndex];
                }

                jump = MathF.Max(jump, MathF.Abs(pressure - neighborPressure));
            }
        }

        workspace.PressureJump = jump;
        workspace.CellSpeed = speed;
    }

    /// <summary>
    ///     Multiplies the stored momentum of a chunk by the factor to carry into the scaled solve:
    ///     <c>min(s, α)</c> for the chunk's last factor <c>s</c>. Call it before the chunk is gathered.
    /// </summary>
    /// <returns>Whether the momentum changed, so the chunk needs a new gather if it had one.</returns>
    internal static bool ScaleUp(AtmosChunk chunk, float alpha)
    {
        float factor = MathF.Min(chunk.EulerMomentumScale, alpha);
        if (factor == 1f)
            return false;

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            chunk.MomentumX[voxelIndex] *= factor;
            chunk.MomentumY[voxelIndex] *= factor;
            chunk.MomentumZ[voxelIndex] *= factor;
        }

        return true;
    }

    /// <summary>
    ///     Turns the read buffers of a group from scaled back to real momentum and energy, then records α on each
    ///     chunk. Runs in parallel over the workspaces.
    /// </summary>
    /// <remarks>
    ///     The write-back, the velocity floor, damping, and everything after them read the buffer's momentum, mass,
    ///     and energy, and the species rows. Only momentum and energy carry the scale. A voxel's energy drops by
    ///     the kinetic energy of its scaled momentum and gains that of its real momentum, both from
    ///     <see cref="EulerMixture.KineticEnergy(Kilogram, KilogramMetrePerSecond, KilogramMetrePerSecond, KilogramMetrePerSecond)" />,
    ///     the function the write-back subtracts. The internal energy it recovers is then the one the sweep evolved.
    ///     The cell-state arrays and the signal speed are not read after the sweeps.
    /// </remarks>
    internal static void ScaleDown(EulerChunkWorkspace[] members, int count, float alpha)
    {
        ParallelPhase.Run(count, new ScaleDownAction(members, alpha));
    }

    private static void ScaleDownWorkspace(EulerChunkWorkspace workspace, float alpha)
    {
        AtmosChunk chunk = workspace.Chunk;
        if (alpha != 1f)
        {
            EulerChunkState buffer = workspace.Read;
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                KilogramMetrePerSecond scaledX = buffer.MomentumX[voxelIndex];
                KilogramMetrePerSecond scaledY = buffer.MomentumY[voxelIndex];
                KilogramMetrePerSecond scaledZ = buffer.MomentumZ[voxelIndex];
                Kilogram mass = buffer.Mass[voxelIndex];
                if (!(mass > 0f) || (scaledX == 0f && scaledY == 0f && scaledZ == 0f))
                    continue;

                KilogramMetrePerSecond realX = scaledX / alpha;
                KilogramMetrePerSecond realY = scaledY / alpha;
                KilogramMetrePerSecond realZ = scaledZ / alpha;
                buffer.Energy[voxelIndex] += EulerMixture.KineticEnergy(mass, realX, realY, realZ) -
                                             EulerMixture.KineticEnergy(mass, scaledX, scaledY, scaledZ);
                buffer.MomentumX[voxelIndex] = realX;
                buffer.MomentumY[voxelIndex] = realY;
                buffer.MomentumZ[voxelIndex] = realZ;
            }
        }

        chunk.EulerMomentumScale = alpha;
    }

    private readonly struct MeasureAction(EulerChunkWorkspace[] members) : IAction
    {
        public void Invoke(int index) => Measure(members[index]);
    }

    private readonly struct ScaleDownAction(EulerChunkWorkspace[] members, float alpha) : IAction
    {
        public void Invoke(int index) => ScaleDownWorkspace(members[index], alpha);
    }
}
