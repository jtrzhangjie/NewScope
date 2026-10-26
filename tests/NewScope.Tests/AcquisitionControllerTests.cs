using NewScope.Models;
using NewScope.Services;

namespace NewScope.Tests;

public class AcquisitionControllerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ANewSessionClearsOldSamplesBeforeUsingANewClock()
    {
        await using var acquisition = new AcquisitionController();
        var channel = CreateChannel("signal");
        acquisition.SetChannels([channel]);
        channel.Buffer.Add(100, -1);

        acquisition.Reset();
        Assert.Empty(channel.Buffer.ToArray());
        using var source = new TestSource();
        acquisition.Start(source);
        await WaitForSamples(channel, 3);
        await acquisition.StopAsync();

        var samples = channel.Buffer.ToArray();
        Assert.All(samples, point => Assert.True(point.Time < 100));
        Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time));

        acquisition.Reset();
        Assert.Equal(0, acquisition.GetMetrics().FrameCount);
        Assert.True(acquisition.Start(source));
        await WaitForSamples(channel, 3);
        await acquisition.StopAsync();
        samples = channel.Buffer.ToArray();
        Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time));
    }

    [Fact]
    public async Task ResetDiscardsAReadThatWasAlreadyInFlight()
    {
        await using var acquisition = new AcquisitionController();
        var channel = CreateChannel("signal");
        acquisition.SetChannels([channel]);
        acquisition.Reset();
        using var source = new BlockingSource();
        Assert.True(acquisition.Start(source));
        Assert.True(source.Entered.Wait(Timeout));

        acquisition.Reset();
        source.Release.Set();
        await WaitForSamples(channel, 2);
        await acquisition.StopAsync();

        Assert.DoesNotContain(channel.Buffer.ToArray(), point => point.Value == 1);
        Assert.All(channel.Buffer.ToArray(), point => Assert.True(point.Value >= 2));
    }

    [Fact]
    public async Task ReadFailureReleasesTheTaskAndAllowsRestart()
    {
        await using var acquisition = new AcquisitionController();
        var channel = CreateChannel("signal");
        acquisition.SetChannels([channel]);
        acquisition.Reset();
        using var source = new TestSource { FailNextRead = true };

        Assert.True(acquisition.Start(source));
        var failedRun = acquisition.RunId;
        await acquisition.Completion.WaitAsync(Timeout);
        Assert.False(acquisition.IsRunning);
        Assert.IsType<JLinkException>(acquisition.LastError);

        Assert.True(acquisition.Start(source));
        Assert.True(acquisition.RunId > failedRun);
        await WaitForSamples(channel, 2);
        await acquisition.StopAsync();
        Assert.Null(acquisition.LastError);
    }

    [Fact]
    public async Task RepeatedImmediateStartStopDoesNotDereferenceAReplacedCts()
    {
        await using var acquisition = new AcquisitionController();
        acquisition.SetChannels([CreateChannel("signal")]);
        using var source = new TestSource();
        for (var i = 0; i < 100; i++)
        {
            Assert.True(acquisition.Start(source));
            await acquisition.StopAsync().WaitAsync(Timeout);
            Assert.False(acquisition.IsRunning);
            Assert.Null(acquisition.LastError);
        }
    }

    [Fact]
    public async Task SnapshotsHaveCompleteAlignedFramesWhileSamplingContinues()
    {
        await using var acquisition = new AcquisitionController { SampleRateHz = 2000 };
        var first = CreateChannel("first");
        var second = CreateChannel("second");
        acquisition.SetChannels([first, second]);
        acquisition.Reset();
        using var source = new TestSource();
        acquisition.Start(source);
        await WaitForSamples(first, 10);

        var snapshot = acquisition.CaptureSnapshot();
        await WaitForSamples(first, snapshot[0].Samples.Length + 10);
        await acquisition.StopAsync();

        Assert.Equal(snapshot[0].Samples.Length, snapshot[1].Samples.Length);
        Assert.Equal(snapshot[0].Samples, snapshot[1].Samples);
        Assert.True(first.Buffer.Count > snapshot[0].Samples.Length);
    }

    private static SampleChannel CreateChannel(string name) => new(
        new SymbolVariable { Name = name, Address = 0x20000000, Size = 4, Kind = "int" }, 1024);

    [Fact]
    public async Task DisplayMetricsDoNotWaitBehindAnExportSnapshot()
    {
        await using var acquisition = new AcquisitionController();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        acquisition.SetChannels([new BlockingNameChannel(entered, release)]);
        var snapshot = Task.Run(() => acquisition.CaptureSnapshot());
        try
        {
            Assert.True(entered.Wait(Timeout));
            _ = await Task.Factory.StartNew(acquisition.GetMetrics, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            await snapshot;
        }
    }

    private sealed class BlockingNameChannel(ManualResetEventSlim entered, ManualResetEventSlim release) : ISampleChannel
    {
        public SymbolVariable Symbol { get; } = new() { Name = "signal" };
        public ChannelBuffer Buffer { get; } = new(2);
        public string Name
        {
            get
            {
                entered.Set();
                if (!release.Wait(Timeout)) throw new TimeoutException();
                return Symbol.Name;
            }
        }
    }

    private static async Task WaitForSamples(SampleChannel channel, int count)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (channel.Buffer.Count < count)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private class TestSource : IValueSource
    {
        private int _readCount;
        public bool FailNextRead { get; set; }
        public string Description => "test";
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public virtual double[] Read(IReadOnlyList<ISampleChannel> channels)
        {
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new JLinkException("simulated read failure");
            }

            return Enumerable.Repeat((double)Interlocked.Increment(ref _readCount), channels.Count).ToArray();
        }
    }

    private sealed class BlockingSource : TestSource
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        private bool _first = true;

        public override double[] Read(IReadOnlyList<ISampleChannel> channels)
        {
            if (_first)
            {
                _first = false;
                Entered.Set();
                if (!Release.Wait(Timeout))
                {
                    throw new TimeoutException("Test did not release the blocked read.");
                }
            }

            return base.Read(channels);
        }
    }
}
