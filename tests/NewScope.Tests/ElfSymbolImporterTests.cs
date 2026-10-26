using NewScope.Services;

namespace NewScope.Tests;

public class ElfSymbolImporterTests
{
    [Fact]
    public void ImportsScalarTypesFromAPortableDwarfFixture()
    {
        var image = ElfTestFixture.Create(ElfTestFixture.Unit("scalars.c", unit =>
        {
            var integer = unit.Scalar("int32_t");
            var floating = unit.Scalar("float", 4, 4);
            var boolean = unit.Scalar("bool", 1, 2);
            var wide = unit.Scalar("uint64_t", 8, 7);
            unit.Variable("counter", ElfTestFixture.RamStart, integer);
            unit.Variable("temperature", ElfTestFixture.RamStart + 4, floating);
            unit.Variable("enabled", ElfTestFixture.RamStart + 8, boolean);
            unit.Variable("ticks", ElfTestFixture.RamStart + 16, wide);
        }));

        var catalog = Import(image);

        Assert.Equal("newscope-symbols-v1", catalog.Format);
        Assert.Equal(4, catalog.VariableCount);
        Assert.Equal(ElfTestFixture.RamStart, catalog.RamStart);
        Assert.Equal(ElfTestFixture.RamSize, catalog.RamSize);
        Assert.True(catalog.Variables.Single(variable => variable.Name == "counter").Signed);
        Assert.Equal("float", catalog.Variables.Single(variable => variable.Name == "temperature").Kind);
        Assert.Equal("bool", catalog.Variables.Single(variable => variable.Name == "enabled").Kind);
        var ticks = catalog.Variables.Single(variable => variable.Name == "ticks");
        Assert.Equal(8, ticks.Size);
        Assert.False(ticks.Signed);
    }

    [Fact]
    public void PreservesSameNameVariablesAtDifferentAddressesAcrossCompilationUnits()
    {
        byte[] Unit(string source, uint address) => ElfTestFixture.Unit(source, unit =>
        {
            var integer = unit.Scalar("int");
            unit.Variable("counter", address, integer);
        });

        var catalog = Import(ElfTestFixture.Create(
            Unit("first.c", ElfTestFixture.RamStart),
            Unit("second.c", ElfTestFixture.RamStart + 4),
            Unit("repeated-debug-info.c", ElfTestFixture.RamStart)));

        Assert.Equal(2, catalog.VariableCount);
        Assert.Equal(2, catalog.Variables.Select(variable => variable.Identity).Distinct().Count());
        Assert.All(catalog.Variables, variable => Assert.Equal("counter", variable.Name));
    }

    [Fact]
    public void FlattensStructuresAndSmallScalarArrays()
    {
        var catalog = Import(ElfTestFixture.Create(ElfTestFixture.Unit("motor.c", unit =>
        {
            var integer = unit.Scalar("int");
            var boolean = unit.Scalar("bool", 1, 2);
            var motor = unit.Structure("Motor", 8, ("speed", integer, 0), ("enabled", boolean, 4));
            var taps = unit.Array(integer, 3);
            unit.Variable("motor", ElfTestFixture.RamStart, motor);
            unit.Variable("taps", ElfTestFixture.RamStart + 16, taps);
        })));

        Assert.Equal(["motor.speed", "motor.enabled", "taps[0]", "taps[1]", "taps[2]"],
            catalog.Variables.Select(variable => variable.Name));
        Assert.Equal(ElfTestFixture.RamStart + 24, catalog.Variables[^1].Address);
    }

    [Fact]
    public void RejectsVariablesWhoseWholeValueDoesNotFitInRam()
    {
        var catalog = Import(ElfTestFixture.Create(ElfTestFixture.Unit("bounds.c", unit =>
        {
            var integer = unit.Scalar("int");
            unit.Variable("valid", ElfTestFixture.RamStart, integer);
            unit.Variable("crosses_end", ElfTestFixture.RamStart + ElfTestFixture.RamSize - 2, integer);
            unit.Variable("outside", ElfTestFixture.RamStart - 4, integer);
        })));

        Assert.Equal("valid", Assert.Single(catalog.Variables).Name);
    }

    [Fact]
    public void RejectsAFileWithoutDwarfInformation()
    {
        Assert.Throws<InvalidDataException>(() => Import(ElfTestFixture.Create(includeDebug: false)));
    }

    [Fact]
    public void RejectsTruncatedSectionTables()
    {
        var image = ElfTestFixture.Create(ElfTestFixture.Unit("empty.c", _ => { }));
        Assert.Throws<InvalidDataException>(() => Import(image[..^1]));
    }

    [Fact]
    public void RejectsNonElfInput()
    {
        Assert.Throws<InvalidDataException>(() => Import("not an ELF file"u8.ToArray()));
    }

    private static NewScope.Models.SymbolCatalog Import(byte[] image)
    {
        using var directory = new TestDirectory();
        var path = directory.File("fixture.elf");
        File.WriteAllBytes(path, image);
        return ElfSymbolImporter.Import(path);
    }
}
