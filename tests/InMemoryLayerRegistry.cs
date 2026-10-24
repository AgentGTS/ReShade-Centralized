namespace ReShadeCentralized.Tests;

internal sealed class InMemoryLayerRegistry : ILayerRegistry
{
    public Dictionary<LayerRegistration, int> Values { get; } = [];
    public int? FailWrite { get; set; }
    public List<(LayerRegistration Registration, int? Value)> WriteHistory { get; } = [];

    private int writes;

    public IEnumerable<LayerRegistration> Enumerate() => Values.Keys.ToArray();

    public int? Read(LayerRegistration key) => Values.TryGetValue(key, out int value) ? value : null;

    public void Write(LayerRegistration key, int? value)
    {
        WriteHistory.Add((key, value));
        if (++writes == FailWrite)
        {
            throw new IOException("Injected registry failure.");
        }

        if (value == null)
        {
            Values.Remove(key);
        }
        else
        {
            Values[key] = value.Value;
        }
    }
}
