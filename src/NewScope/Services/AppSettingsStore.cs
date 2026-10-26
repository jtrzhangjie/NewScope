using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewScope.Models;

namespace NewScope.Services;

public sealed class AppSettings
{
    public string? SymbolCatalogPath { get; set; }
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "zh";
    public string JLinkDevice { get; set; } = "Cortex-M0";
    public int SampleRateHz { get; set; } = 100;
    public int SpeedKHz { get; set; } = 5000;
    public double WindowSeconds { get; set; } = 5;
    public int PaneCount { get; set; } = 1;
    public int SelectedPane { get; set; }
    public bool AutoFollow { get; set; } = true;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool WindowMaximized { get; set; }
    public List<ChannelWorkspaceEntry> Channels { get; set; } = [];
}

public sealed class ChannelWorkspaceEntry
{
    public string Name { get; set; } = "";
    public uint? Address { get; set; }
    public int PaneIndex { get; set; }
    public bool Visible { get; set; } = true;
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CatalogOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NewScope");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Current { get; } = Load();

    public static AppSettings Load() => LoadFrom(FilePath);

    public static AppSettings LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                return Normalize(loaded);
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Could not load settings '{path}': {exception.Message}");
        }

        return new AppSettings();
    }

    public static bool Save() => SaveTo(Current, FilePath);

    public static bool SaveTo(AppSettings settings, string path)
    {
        try
        {
            AtomicFile.Write(path, stream =>
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            });
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not save settings '{path}'", exception);
            return false;
        }
    }

    public static string SaveSymbolCatalog(SymbolCatalog catalog, string sourcePath, string? directoryPath = null)
    {
        var symbolsDirectory = Path.Combine(directoryPath ?? DirectoryPath, "symbols");

        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        var safeName = string.Concat(sourceName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "symbols";
        }

        var hashInput = Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hashInput))[..8];
        var target = Path.Combine(symbolsDirectory, $"{safeName}-{hash}.json");

        AtomicFile.Write(target, stream => JsonSerializer.Serialize(stream, catalog, CatalogOptions));
        return target;
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        settings.Language = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh";
        settings.JLinkDevice = string.IsNullOrWhiteSpace(settings.JLinkDevice)
            ? "Cortex-M0"
            : settings.JLinkDevice.Trim();

        settings.SampleRateHz = new[] { 10, 20, 50, 100, 200, 500, 1000, 2000 }
            .Contains(settings.SampleRateHz)
            ? settings.SampleRateHz
            : 100;
        settings.SpeedKHz = new[] { 1000, 2000, 4000, 5000, 10000, 15000, 20000, 25000, 30000, 40000, 50000 }
            .Contains(settings.SpeedKHz)
            ? settings.SpeedKHz
            : 5000;
        settings.WindowSeconds = new[] { 1.0, 2, 5, 10, 20, 30, 60 }
            .Contains(settings.WindowSeconds)
            ? settings.WindowSeconds
            : 5;
        settings.PaneCount = Math.Clamp(settings.PaneCount, 1, 4);
        settings.SelectedPane = Math.Clamp(settings.SelectedPane, 0, settings.PaneCount - 1);
        settings.Channels = (settings.Channels ?? [])
            .Where(channel => channel is not null && !string.IsNullOrWhiteSpace(channel.Name))
            .DistinctBy(channel => (channel.Name, channel.Address))
            .ToList();
        return settings;
    }
}
