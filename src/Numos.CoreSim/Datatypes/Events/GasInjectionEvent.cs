using System.Numerics;

namespace Numos.Datatypes.Events;

internal struct GasInjectionEvent
{
    public Vector3 Position;
    public int GasId;
    public float Moles;
    public float Temperature;
}