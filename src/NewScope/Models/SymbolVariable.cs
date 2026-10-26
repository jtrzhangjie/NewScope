using System.Text.Json.Serialization;

namespace NewScope.Models;

public sealed class SymbolCatalog
{
    public string Format { get; set; } = "";
    public string Elf { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public uint RamStart { get; set; }
    public uint RamSize { get; set; }
    public int VariableCount { get; set; }
    public List<SymbolVariable> Variables { get; set; } = [];
}

public sealed class SymbolVariable
{
    public string Name { get; set; } = "";
    public uint Address { get; set; }
    public int Size { get; set; }
    public string Type { get; set; } = "";
    public bool Signed { get; set; }
    public string Kind { get; set; } = "";

    [JsonIgnore]
    public string Identity => $"{Name}@{Address:X8}";

    [JsonIgnore]
    public string AddressText => $"0x{Address:X8}";

    [JsonIgnore]
    public string Group => Name.Contains('.') ? Name[..Name.IndexOf('.')] : Name;

    [JsonIgnore]
    public string Tooltip => $"{Name}\n{Type} · {Size} byte{(Size == 1 ? "" : "s")} · {AddressText}";
}
