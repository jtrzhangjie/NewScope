using System.Text.Json;
using NewScope.Models;

namespace NewScope.Services;

public static class SymbolCatalogLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "symbols", "stepperctrl.json");

    public static string? LastLoadedPath { get; private set; }

    public static SymbolCatalog Load(string? path = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.Add(path);
        }
        else if (!string.IsNullOrWhiteSpace(AppSettingsStore.Current.SymbolCatalogPath))
        {
            candidates.Add(AppSettingsStore.Current.SymbolCatalogPath);
        }

        candidates.Add(DefaultPath);
        candidates.Add(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "symbols", "stepperctrl.json")));

        var candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is null)
        {
            throw new FileNotFoundException("Symbol catalog was not found.", candidates[0]);
        }

        var json = File.ReadAllText(candidate);
        var catalog = JsonSerializer.Deserialize<SymbolCatalog>(json, JsonOptions)
                      ?? throw new InvalidDataException("Symbol catalog is empty or invalid.");
        catalog.Variables ??= [];
        catalog.VariableCount = catalog.Variables.Count;
        LastLoadedPath = candidate;
        return catalog;
    }
}
