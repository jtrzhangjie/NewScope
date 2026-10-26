using System.Runtime.CompilerServices;
using NewScope.Models;
using NewScope.Services;
using NewScope.ViewModels;

namespace NewScope.Tests;

[Collection("Application state")]
public class MainViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartAfterAReadFailureActuallyRestartsAcquisition()
    {
        var source = new TestSource { FailNextRead = true };
        var model = CreateModel(() => source);
        try
        {
            await model.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => model.HasError);
            Assert.False(model.IsRunning);
            Assert.True(model.ConnectionRetryAvailable);

            await model.ToggleRunCommand.ExecuteAsync(null);
            await WaitFor(() => model.Channels[0].Buffer.Count >= 2);

            Assert.True(model.IsRunning);
            Assert.False(model.HasError);
            Assert.False(model.ConnectionRetryAvailable);
        }
        finally
        {
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ReconnectingStartsWithOnlyTheNewSessionsSamples()
    {
        var epoch = 0;
        var model = CreateModel(() => new TestSource { Value = ++epoch });
        try
        {
            await model.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => model.Channels[0].Buffer.Count >= 3);
            await model.DisconnectCommand.ExecuteAsync(null);
            Assert.All(model.Channels[0].Buffer.ToArray(), point => Assert.Equal(1, point.Value));

            await model.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => model.Channels[0].Buffer.Count >= 3);
            await model.PauseCommand.ExecuteAsync(null);

            var samples = model.Channels[0].Buffer.ToArray();
            Assert.All(samples, point => Assert.Equal(2, point.Value));
            Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time));
        }
        finally
        {
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SearchingReleasesTheOldTreeWhileSymbolsRemainLoaded()
    {
        var model = CreateModel(() => new TestSource());
        try
        {
            var oldNode = ReplaceTree(model);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(oldNode.IsAlive);
            Assert.NotEmpty(model.AllVariables);
            for (var i = 0; i < 20; i++)
            {
                model.SearchText = "group";
                model.SearchText = "";
            }
        }
        finally
        {
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SameNameVariablesCanBeSelectedAndRestoredIndependently()
    {
        var catalog = CreateCatalog("counter", "counter");
        var settings = new AppSettings { PaneCount = 2 };
        var model = CreateModel(() => new TestSource(), settings, catalog);
        try
        {
            Assert.Equal(2, model.Channels.Count);
            model.Channels[1].MovePaneCommand.Execute(null);
            model.Channels[1].IsVisible = false;
            model.SaveWorkspace();

            Assert.Equal(2, settings.Channels.Select(channel => channel.Address).Distinct().Count());
            var restored = CreateModel(() => new TestSource(), settings, catalog);
            try
            {
                Assert.Equal(2, restored.Channels.Count);
                Assert.Equal(model.Channels[1].Symbol.Address, restored.Channels[1].Symbol.Address);
                Assert.Equal(1, restored.Channels[1].PaneIndex);
                Assert.False(restored.Channels[1].IsVisible);
            }
            finally
            {
                await restored.ShutdownAsync();
            }
        }
        finally
        {
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task LegacyWorkspaceWithoutAddressesStillRestoresByName()
    {
        var settings = new AppSettings
        {
            Channels = [new ChannelWorkspaceEntry { Name = "group.second" }]
        };
        var model = CreateModel(() => new TestSource(), settings);
        try
        {
            Assert.Equal("group.second", Assert.Single(model.Channels).Name);
        }
        finally
        {
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AUniqueVariableStillRestoresWhenFirmwareMovesItsAddress()
    {
        var settings = new AppSettings
        {
            Channels = [new ChannelWorkspaceEntry { Name = "group.second", Address = 0x20000100 }]
        };
        var model = CreateModel(() => new TestSource(), settings);
        try
        {
            Assert.Equal("group.second", Assert.Single(model.Channels).Name);
        }
        finally
        {
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConcurrentConnectAndF5DoNotOpenTwoSources()
    {
        var source = new BlockingOpenSource();
        var created = 0;
        var model = CreateModel(() => { created++; return source; });
        try
        {
            var connect = model.ConnectCommand.ExecuteAsync(null);
            await source.Entered.Task.WaitAsync(Timeout);
            var toggle = model.ToggleRunCommand.ExecuteAsync(null);
            source.Release.TrySetResult();
            await Task.WhenAll(connect, toggle).WaitAsync(Timeout);

            Assert.Equal(1, created);
            Assert.True(model.IsConnected);
            Assert.True(model.IsRunning);
        }
        finally
        {
            source.Release.TrySetResult();
            await model.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ShutdownDuringOpenWaitsForTheSourceToBeReleased()
    {
        var source = new BlockingOpenSource();
        var model = CreateModel(() => source);
        var connect = model.ConnectCommand.ExecuteAsync(null);
        await source.Entered.Task.WaitAsync(Timeout);
        var shutdown = model.ShutdownAsync();
        source.Release.TrySetResult();
        await Task.WhenAll(connect, shutdown).WaitAsync(Timeout);

        Assert.Equal(1, source.DisposeCount);
        Assert.False(model.IsConnected);
        Assert.False(model.IsRunning);
        Assert.Equal(0, source.ReadCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReplaceTree(MainViewModel model)
    {
        var leaf = model.FilteredVariables[0].Children[0];
        var reference = new WeakReference(leaf);
        model.SearchText = "does-not-match-any-variable";
        return reference;
    }

    private static MainViewModel CreateModel(
        Func<IValueSource> createSource, AppSettings? settings = null, SymbolCatalog? catalog = null) =>
        new(true, settings ?? new AppSettings(),
            () => catalog ?? CreateCatalog("group.first", "group.second"), (_, _) => createSource());

    private static SymbolCatalog CreateCatalog(params string[] names) => new()
    {
        Elf = "test.elf",
        RamStart = 0x20000000,
        RamSize = 4096,
        VariableCount = names.Length,
        Variables = names.Select((name, index) => new SymbolVariable
        {
            Name = name, Address = 0x20000000 + (uint)index * 4,
            Size = 4, Type = "int", Kind = "int", Signed = true
        }).ToList()
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private class TestSource : IValueSource
    {
        private int _disposeCount;
        private int _readCount;
        public double Value { get; init; } = 42;
        public bool FailNextRead { get; set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int ReadCount => Volatile.Read(ref _readCount);
        public string Description => "test source";
        public virtual void Open() { }
        public void Close() { }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public double[] Read(IReadOnlyList<ISampleChannel> channels)
        {
            Interlocked.Increment(ref _readCount);
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new JLinkException("simulated failure");
            }

            return Enumerable.Repeat(Value, channels.Count).ToArray();
        }
    }

    private sealed class BlockingOpenSource : TestSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Open()
        {
            Entered.TrySetResult();
            Release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        }
    }
}
