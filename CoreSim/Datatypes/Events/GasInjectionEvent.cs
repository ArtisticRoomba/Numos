using System.Numerics;

namespace Numos.Datatypes.Events;

public struct GasInjectionEvent
{
    public Vector3 Position;
    public int GasId;
    public float Moles;
    public float Temperature;
}