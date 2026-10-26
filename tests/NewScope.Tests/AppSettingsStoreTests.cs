using NewScope.Services;

namespace NewScope.Tests;

public class AppSettingsStoreTests
{
    [Fact]
    public void FailedSaveReturnsFalseAndKeepsThePreviousSettings()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        Assert.True(AppSettingsStore.SaveTo(new AppSettings { Language = "en" }, path));

        Assert.False(AppSettingsStore.SaveTo(new AppSettings { WindowWidth = double.NaN }, path));

        Assert.Equal("en", AppSettingsStore.LoadFrom(path).Language);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void FailedSaveToAnInvalidDirectoryIsObservable()
    {
        using var directory = new TestDirectory();
        var blocker = directory.File("blocker");
        File.WriteAllText(blocker, "not a directory");

        Assert.False(AppSettingsStore.SaveTo(new AppSettings(), Path.Combine(blocker, "settings.json")));
        Assert.Equal("not a directory", File.ReadAllText(blocker));
    }

    [Fact]
    public void WorkspacePersistsAddressesForSameNameVariables()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var settings = new AppSettings
        {
            Channels =
            [
                new ChannelWorkspaceEntry { Name = "counter", Address = 0x20000000 },
                new ChannelWorkspaceEntry { Name = "counter", Address = 0x20000004 }
            ]
        };

        Assert.True(AppSettingsStore.SaveTo(settings, path));
        var restored = AppSettingsStore.LoadFrom(path);

        Assert.Equal(2, restored.Channels.Count);
        Assert.Equal(settings.Channels.Select(channel => channel.Address), restored.Channels.Select(channel => channel.Address));
    }

    [Fact]
    public void SymbolCatalogIsSavedAtomicallyWithoutTemporaryFiles()
    {
        using var directory = new TestDirectory();
        var catalog = new NewScope.Models.SymbolCatalog { Format = "newscope-symbols-v1", Elf = "test.elf" };

        var path = AppSettingsStore.SaveSymbolCatalog(catalog, "test.elf", directory.Path);
        catalog.GeneratedAt = "updated";
        Assert.Equal(path, AppSettingsStore.SaveSymbolCatalog(catalog, "test.elf", directory.Path));

        Assert.Contains("updated", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void SaveToAndLoadFromRoundTripWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings
        {
            Theme = "Light",
            Language = "en",
            SampleRateHz = 2_000,
            SpeedKHz = 4_000,
            JLinkDevice = "Cortex-M4",
            PaneCount = 2,
            AutoFollow = false,
            WindowWidth = 1280,
            WindowHeight = 720,
            WindowX = 120,
            WindowY = 80,
            WindowMaximized = true,
            Channels =
            [
                new ChannelWorkspaceEntry { Name = "motor.speed", PaneIndex = 1, Visible = false }
            ]
        };

        try
        {
            AppSettingsStore.SaveTo(settings, path);
            var loaded = AppSettingsStore.LoadFrom(path);

            Assert.True(File.Exists(path));
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal("en", loaded.Language);
            Assert.Equal(2_000, loaded.SampleRateHz);
            Assert.Equal(4_000, loaded.SpeedKHz);
            Assert.Equal("Cortex-M4", loaded.JLinkDevice);
            Assert.Equal(2, loaded.PaneCount);
            Assert.False(loaded.AutoFollow);
            Assert.Equal("Cortex-M4", loaded.JLinkDevice);
            Assert.Equal(1280, loaded.WindowWidth);
            Assert.Equal(720, loaded.WindowHeight);
            Assert.Equal(120, loaded.WindowX);
            Assert.Equal(80, loaded.WindowY);
            Assert.True(loaded.WindowMaximized);
            var channel = Assert.Single(loaded.Channels);
            Assert.Equal("motor.speed", channel.Name);
            Assert.Equal(1, channel.PaneIndex);
            Assert.False(channel.Visible);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void InvalidFileFallsBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ not json");
            var loaded = AppSettingsStore.LoadFrom(path);

            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal("zh", loaded.Language);
            Assert.Equal(100, loaded.SampleRateHz);
            Assert.Equal("Cortex-M0", loaded.JLinkDevice);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void MissingFileFallsBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"), "settings.json");

        var loaded = AppSettingsStore.LoadFrom(path);

        Assert.Equal(5, loaded.WindowSeconds);
        Assert.Equal("Cortex-M0", loaded.JLinkDevice);
        Assert.Empty(loaded.Channels);
    }

    [Fact]
    public void InvalidValuesAreNormalizedOnLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "SampleRateHz": 123,
                  "SpeedKHz": 999,
                  "WindowSeconds": 7,
                  "PaneCount": 8,
                  "SelectedPane": 9,
                  "JLinkDevice": "  Cortex-M7  ",
                  "Channels": [
                    { "Name": "ok", "PaneIndex": 4 },
                    { "Name": "  " }
                  ]
                }
                """);

            var loaded = AppSettingsStore.LoadFrom(path);

            Assert.Equal(100, loaded.SampleRateHz);
            Assert.Equal(5000, loaded.SpeedKHz);
            Assert.Equal(5, loaded.WindowSeconds);
            Assert.Equal(4, loaded.PaneCount);
            Assert.Equal(3, loaded.SelectedPane);
            Assert.Equal("Cortex-M7", loaded.JLinkDevice);
            Assert.Single(loaded.Channels);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SaveToIsAtomicAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            AppSettingsStore.SaveTo(new AppSettings { Language = "en" }, path);

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Equal("en", AppSettingsStore.LoadFrom(path).Language);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
