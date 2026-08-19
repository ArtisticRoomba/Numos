namespace Numos.API;

internal sealed class GasMixtureState
{
    internal GasMixtureState(float volume, float temperature)
    {
        Volume = volume;
        Temperature = temperature;
    }

    internal float Volume { get; set; }
    internal float Temperature { get; set; }
    internal SortedDictionary<int, float> Moles { get; } = [];

    internal int ActiveGasCount => Moles.Count;

    internal float TotalMoles
    {
        get
        {
            double total = 0d;
            foreach (float moles in Moles.Values)
                total += moles;
            return (float)total;
        }
    }

    internal GasMixtureState Clone()
    {
        var clone = new GasMixtureState(Volume, Temperature);
        foreach (var (gasId, moles) in Moles)
            clone.Moles.Add(gasId, moles);
        return clone;
    }

    internal KeyValuePair<int, float>[] ToGasArray()
    {
        var gases = new KeyValuePair<int, float>[Moles.Count];
        var index = 0;
        foreach (var gas in Moles)
            gases[index++] = gas;
        return gases;
    }
}