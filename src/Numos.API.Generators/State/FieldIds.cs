using System.Text;

namespace Numos.API.Generators.State;

/// <summary>
///     Turns a field's wire name into its id. Must match <c>NumosState.FieldId</c> in Numos.CoreSim exactly.
/// </summary>
internal static class FieldIds
{
    /// <summary>
    ///     FNV-1a (32-bit) over the name's UTF-8 bytes, with the sign bit cleared, and 0 mapped to 1.
    /// </summary>
    /// <remarks>The reader needs ids that are positive and strictly increasing within an object.</remarks>
    public static int Compute(string name)
    {
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes(name))
        {
            hash ^= value;
            hash *= 16777619;
        }

        int id = (int)(hash & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }
}
