using System.Buffers.Binary;
using NewScope.Models;
using NewScope.Services;
using NewScope.ViewModels;

namespace NewScope.Tests;

public class JLinkValueSourceTests
{
    [Fact]
    public void ExposesActualAndMaximumProbeSpeed()
    {
        var session = new FakeSession { ActualSpeedKHz = 15000, MaxSpeedKHz = 15000 };
        using var source = new JLinkValueSource(
            "TEST", 50000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);
        source.Open();

        Assert.Equal(15000, source.ActualSpeedKHz);
        Assert.Equal(15000, source.MaxSpeedKHz);
    }

    [Fact]
    public void ReadRecoversAndRetriesAfterTransientFailure()
    {
        var session = new FakeSession { FailuresRemaining = 1, Value = 1234 };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 2, recoveryDelayMilliseconds: 0);
        source.Open();

        var values = source.Read([CreateChannel()]);

        Assert.Equal(1234, values[0]);
        Assert.Equal(1, source.RecoveryCount);
        Assert.Equal(2, session.OpenCount);
        Assert.Equal(2, session.ReadCount);
    }

    [Fact]
    public void ReadRecoversWhenOpenNeedsARetry()
    {
        var session = new FakeSession { FailuresRemaining = 1, Value = 7 };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 2, recoveryDelayMilliseconds: 0);
        source.Open();
        session.OpenFailuresRemaining = 1;

        var values = source.Read([CreateChannel()]);

        Assert.Equal(7, values[0]);
        Assert.Equal(1, source.RecoveryCount);
        Assert.Equal(3, session.OpenCount);
    }

    [Fact]
    public void ReadThrowsWhenRecoveryCannotRestoreTheSession()
    {
        var session = new FakeSession { FailuresRemaining = int.MaxValue };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 2, recoveryDelayMilliseconds: 0);
        source.Open();

        Assert.Throws<JLinkException>(() => source.Read([CreateChannel()]));
        Assert.Equal(1, source.RecoveryCount);
        Assert.Equal(2, session.OpenCount);
        Assert.Equal(2, session.ReadCount);
    }

    [Fact]
    public void ReadsUseTheCachedPlanWhileChannelListIsStable()
    {
        var session = new FakeSession { Value = 5 };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);
        source.Open();
        var channels = new[] { CreateChannel() };

        source.Read(channels);
        source.Read(channels);

        Assert.Equal(1, session.OpenCount);
        Assert.Equal(2, session.ReadCount);
    }

    [Fact]
    public void EmptyChannelListDoesNotTouchTheProbe()
    {
        var session = new FakeSession();
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);

        Assert.Empty(source.Read([]));
        Assert.Equal(0, session.ReadCount);
    }

    [Fact]
    public void ReadsSigned16BitLittleEndianValue()
    {
        var session = new FakeSession { Raw = [0xFE, 0xFF] };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);
        source.Open();

        var values = source.Read([CreateChannel(signed: true)]);

        Assert.Equal(-2, values[0]);
    }

    [Fact]
    public void ReadsUnsigned16BitLittleEndianValue()
    {
        var session = new FakeSession { Raw = [0xFE, 0xFF] };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);
        source.Open();

        var values = source.Read([CreateChannel()]);

        Assert.Equal(65534, values[0]);
    }

    [Fact]
    public void ReadsSigned32BitLittleEndianValue()
    {
        var session = new FakeSession { Raw = [0xFE, 0xFF, 0xFF, 0xFF] };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);
        source.Open();

        var values = source.Read([CreateChannel(signed: true, size: 4, kind: "int32_t")]);

        Assert.Equal(-2, values[0]);
    }

    [Fact]
    public void ReadsSinglePrecisionLittleEndianValue()
    {
        var session = new FakeSession { Raw = [0x00, 0x00, 0xC0, 0x3F] };
        using var source = new JLinkValueSource(
            "TEST", 1000, session, recoveryAttempts: 0, recoveryDelayMilliseconds: 0);
        source.Open();

        var values = source.Read([CreateChannel(size: 4, kind: "float")]);

        Assert.Equal(1.5, values[0]);
    }

    private static ChannelViewModel CreateChannel(
        bool signed = false,
        int size = 2,
        string kind = "uint16_t") => new(
        new SymbolVariable
        {
            Name = "counter",
            Address = 0x2000_0000,
            Size = size,
            Type = kind,
            Kind = kind,
            Signed = signed
        },
        0);

    private sealed class FakeSession : IJLinkSession
    {
        public string Description => "fake J-Link";
        public int ActualSpeedKHz { get; set; }
        public int MaxSpeedKHz { get; set; }
        public int OpenCount { get; private set; }
        public int ReadCount { get; private set; }
        public int FailuresRemaining { get; set; }
        public int OpenFailuresRemaining { get; set; }
        public ushort Value { get; set; }
        public byte[]? Raw { get; set; }

        public void Open(string device, int speedKHz, bool resumeAfterAttach = true)
        {
            OpenCount++;
            if (OpenFailuresRemaining > 0)
            {
                OpenFailuresRemaining--;
                throw new JLinkException("probe unavailable");
            }
        }

        public void Close()
        {
        }

        public void ReadMemory(uint address, byte[] buffer, int length)
        {
            ReadCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new JLinkException("read timeout");
            }

            Array.Clear(buffer);
            if (Raw is not null)
            {
                Raw.AsSpan(0, Math.Min(Raw.Length, length)).CopyTo(buffer);
                return;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(buffer, Value);
        }

        public void Dispose()
        {
        }
    }
}
