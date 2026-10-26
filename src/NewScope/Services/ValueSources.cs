using System.Buffers.Binary;
using System.Diagnostics;
using NewScope.Models;

namespace NewScope.Services;

public interface IValueSource : IDisposable
{
    string Description { get; }
    int ActualSpeedKHz => 0;
    int MaxSpeedKHz => 0;
    void Open();
    void Close();
    double[] Read(IReadOnlyList<ISampleChannel> channels);
}

public sealed class JLinkValueSource : IValueSource
{
    private const int MaxBlockBytes = 512;
    private const int MaxBlockGap = 64;
    private readonly string _device;
    private readonly int _speedKHz;
    private readonly IJLinkSession _session;
    private readonly int _recoveryAttempts;
    private readonly int _recoveryDelayMilliseconds;
    private IReadOnlyList<ISampleChannel>? _plannedChannels;
    private ReadBlock[] _blocks = [];
    private double[] _values = [];
    private bool _recovering;

    public JLinkValueSource(string device, int speedKHz)
        : this(device, speedKHz, new JLinkWorkerSession(), 2, 200)
    {
    }

    internal JLinkValueSource(
        string device,
        int speedKHz,
        IJLinkSession session,
        int recoveryAttempts = 2,
        int recoveryDelayMilliseconds = 200)
    {
        _device = device;
        _speedKHz = speedKHz;
        _session = session;
        _recoveryAttempts = Math.Max(0, recoveryAttempts);
        _recoveryDelayMilliseconds = Math.Max(0, recoveryDelayMilliseconds);
    }

    public string Description => _session.Description;
    public int ActualSpeedKHz => _session.ActualSpeedKHz;
    public int MaxSpeedKHz => _session.MaxSpeedKHz;
    public int RecoveryCount { get; private set; }

    public void Open()
    {
        _session.Open(_device, _speedKHz, resumeAfterAttach: true);
        InvalidatePlan();
    }

    public void Close()
    {
        InvalidatePlan();
        _session.Close();
    }

    public double[] Read(IReadOnlyList<ISampleChannel> channels)
    {
        if (channels.Count == 0)
        {
            return [];
        }

        EnsurePlan(channels);
        try
        {
            return ReadPlanned();
        }
        catch (JLinkException exception) when (!_recovering)
        {
            AppLog.Warn($"J-Link read failed, reconnecting: {exception.Message}");
            Recover();
            EnsurePlan(channels);
            return ReadPlanned();
        }
    }

    public void Dispose() => _session.Dispose();

    private double[] ReadPlanned()
    {
        foreach (var block in _blocks)
        {
            _session.ReadMemory(block.Start, block.Buffer, block.Length);
            foreach (var (channel, offset, index) in block.Channels)
            {
                _values[index] = ReadValue(block.Buffer, offset, channel.Symbol);
            }
        }

        return _values;
    }

    private void EnsurePlan(IReadOnlyList<ISampleChannel> channels)
    {
        if (ReferenceEquals(_plannedChannels, channels) && _values.Length == channels.Count)
        {
            return;
        }

        var ordered = channels
            .Select((channel, index) => (channel, index))
            .OrderBy(item => item.channel.Symbol.Address)
            .ToArray();

        var blocks = new List<ReadBlock>();
        ReadBlock? current = null;

        foreach (var (channel, index) in ordered)
        {
            var start = channel.Symbol.Address;
            var end = start + (uint)channel.Symbol.Size;

            if (current is null ||
                start > current.Start + (uint)current.Length + MaxBlockGap ||
                end - current.Start > MaxBlockBytes)
            {
                current = new ReadBlock(start, channel.Symbol.Size);
                blocks.Add(current);
            }
            else if (end > current.Start + (uint)current.Length)
            {
                current.Length = (int)(end - current.Start);
            }

            current.Channels.Add((channel, (int)(start - current.Start), index));
        }

        foreach (var block in blocks)
        {
            block.Buffer = new byte[block.Length];
        }

        _plannedChannels = channels;
        _blocks = blocks.ToArray();
        _values = new double[channels.Count];
    }

    private void InvalidatePlan()
    {
        _plannedChannels = null;
        _blocks = [];
        _values = [];
    }

    private void Recover()
    {
        if (_recovering)
        {
            return;
        }

        _recovering = true;
        try
        {
            InvalidatePlan();
            _session.Close();
            Exception? lastError = null;
            for (var attempt = 0; attempt < _recoveryAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(_recoveryDelayMilliseconds * attempt);
                }

                try
                {
                    _session.Open(_device, _speedKHz, resumeAfterAttach: true);
                    RecoveryCount++;
                    AppLog.Info("J-Link reconnected after read failure");
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    AppLog.Warn($"J-Link reconnect attempt {attempt + 1} failed: {exception.Message}");
                }
            }

            throw lastError ?? new JLinkException("J-Link reconnect failed.");
        }
        catch (Exception exception)
        {
            AppLog.Error("J-Link reconnection failed", exception);
            throw;
        }
        finally
        {
            _recovering = false;
        }
    }

    private static double ReadValue(byte[] buffer, int offset, SymbolVariable symbol)
    {
        var span = buffer.AsSpan(offset, symbol.Size);
        return symbol.Size switch
        {
            1 => symbol.Signed ? (sbyte)span[0] : span[0],
            2 => symbol.Signed
                ? BinaryPrimitives.ReadInt16LittleEndian(span)
                : BinaryPrimitives.ReadUInt16LittleEndian(span),
            4 => symbol.Kind == "float"
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span))
                : symbol.Signed
                    ? BinaryPrimitives.ReadInt32LittleEndian(span)
                    : BinaryPrimitives.ReadUInt32LittleEndian(span),
            8 => symbol.Kind == "float"
                ? BinaryPrimitives.ReadDoubleLittleEndian(span)
                : symbol.Signed
                    ? BinaryPrimitives.ReadInt64LittleEndian(span)
                    : BinaryPrimitives.ReadUInt64LittleEndian(span),
            _ => double.NaN
        };
    }

    private sealed class ReadBlock(uint start, int length)
    {
        public uint Start { get; } = start;
        public int Length { get; set; } = length;
        public byte[] Buffer { get; set; } = [];
        public List<(ISampleChannel Channel, int Offset, int Index)> Channels { get; } = [];
    }
}

public sealed class DemoValueSource : IValueSource
{
    private readonly Stopwatch _clock = new();
    private IReadOnlyList<ISampleChannel>? _plannedChannels;
    private double[] _values = [];
    private double _start;

    public string Description => Localization.Get("DemoDescription");

    public void Open()
    {
        _clock.Restart();
        _start = 0;
    }

    public void Close() => _clock.Stop();

    public double[] Read(IReadOnlyList<ISampleChannel> channels)
    {
        if (!ReferenceEquals(_plannedChannels, channels) || _values.Length != channels.Count)
        {
            _plannedChannels = channels;
            _values = new double[channels.Count];
        }

        var t = _clock.Elapsed.TotalSeconds + _start;
        for (var i = 0; i < channels.Count; i++)
        {
            _values[i] = Generate(channels[i].Symbol, t);
        }

        return _values;
    }

    public void Dispose() => Close();

    private static double Generate(SymbolVariable symbol, double t)
    {
        var hash = Fnv1a(symbol.Name);
        var frequency = 0.15 + hash % 150 / 100.0;
        var phase = hash % 628 / 100.0;
        var amp = symbol.Size >= 4 ? 24000.0 : 3200.0;

        var name = symbol.Name.ToLowerInvariant();
        if (name.Contains("speed"))
        {
            amp = 900;
            frequency = 0.08;
        }
        else if (name.Contains("angle"))
        {
            amp = 360;
            frequency = 0.25;
        }
        else if (name.Contains("vdc") || name.Contains("volt"))
        {
            amp = 24000;
            frequency = 0.05;
        }
        else if (name.Contains("curr") || name.Contains("current"))
        {
            amp = 2200;
            frequency = 0.9;
        }
        else if (name.Contains("temp"))
        {
            amp = 45;
            frequency = 0.02;
        }

        if (symbol.Kind == "bool")
        {
            return Math.Sin(2 * Math.PI * frequency * t + phase) >= 0 ? 1 : 0;
        }

        var baseWave = Math.Sin(2 * Math.PI * frequency * t + phase);
        var harmonic = 0.18 * Math.Sin(2 * Math.PI * frequency * 3.7 * t + phase * 0.5);
        var slow = 0.12 * Math.Sin(2 * Math.PI * 0.013 * t);
        var noise = 0.008 * Math.Sin(2 * Math.PI * 47 * t + hash % 31);
        var value = amp * (baseWave + harmonic + slow + noise);

        return symbol.Kind == "int" ? Math.Round(value) : value;
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return hash;
    }
}
