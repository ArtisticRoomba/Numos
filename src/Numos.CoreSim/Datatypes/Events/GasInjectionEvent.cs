using System.Numerics;

namespace Numos.CoreSim.Datatypes.Events;

internal struct GasInjectionEvent
{
    public Vector3 Position;
    public int GasId;
    public Mole Moles;
    public Kelvin Temperature;
}