namespace NewScope.Models;

public interface ISampleChannel
{
    SymbolVariable Symbol { get; }
    ChannelBuffer Buffer { get; }
    string Name { get; }
}

public sealed class SampleChannel(SymbolVariable symbol, int capacity = 200_000) : ISampleChannel
{
    public SymbolVariable Symbol { get; } = symbol;
    public ChannelBuffer Buffer { get; } = new(capacity);
    public string Name => Symbol.Name;
}

public sealed record ChannelSnapshot(string Name, SamplePoint[] Samples);
