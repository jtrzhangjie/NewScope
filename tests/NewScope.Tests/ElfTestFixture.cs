using System.Text;

namespace NewScope.Tests;

/// <summary>Small, deterministic ELF32 / DWARF4 images; no toolchain or local firmware is required.</summary>
internal static class ElfTestFixture
{
    public const uint RamStart = 0x20000000;
    public const uint RamSize = 4096;

    public static byte[] Unit(string source, Action<UnitBuilder> build)
    {
        using var unit = new UnitBuilder(source);
        build(unit);
        return unit.Complete();
    }

    public static byte[] Create(params byte[][] units) => Create(includeDebug: true, units);

    public static byte[] Create(bool includeDebug, params byte[][] units)
    {
        var sections = new List<Section>
        {
            new(".shstrtab", 3, 0, 0, []),
            new(".bss", 8, 3, RamStart, [], RamSize)
        };
        if (includeDebug)
        {
            sections.Add(new Section(".debug_abbrev", 1, 0, 0, Abbreviations()));
            sections.Add(new Section(".debug_info", 1, 0, 0, units.SelectMany(unit => unit).ToArray()));
        }

        var names = new Dictionary<string, uint>();
        using var namesStream = new MemoryStream();
        namesStream.WriteByte(0);
        foreach (var section in sections)
        {
            names[section.Name] = (uint)namesStream.Position;
            namesStream.Write(Encoding.UTF8.GetBytes(section.Name));
            namesStream.WriteByte(0);
        }
        sections[0] = sections[0] with { Data = namesStream.ToArray() };

        using var image = new MemoryStream();
        using var writer = new BinaryWriter(image, Encoding.UTF8, leaveOpen: true);
        writer.Write(new byte[52]);
        var offsets = new List<uint>();
        foreach (var section in sections)
        {
            offsets.Add((uint)image.Position);
            writer.Write(section.Data);
        }
        while (image.Position % 4 != 0)
        {
            writer.Write((byte)0);
        }

        var sectionTableOffset = (uint)image.Position;
        writer.Write(new byte[40]);
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            writer.Write(names[section.Name]);
            writer.Write(section.Type);
            writer.Write(section.Flags);
            writer.Write(section.Address);
            writer.Write(offsets[i]);
            writer.Write(section.VirtualSize ?? (uint)section.Data.Length);
            writer.Write(0u); // sh_link
            writer.Write(0u); // sh_info
            writer.Write(1u); // sh_addralign
            writer.Write(0u); // sh_entsize
        }

        image.Position = 0;
        writer.Write(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        writer.Write((ushort)2); // ET_EXEC
        writer.Write((ushort)40); // EM_ARM
        writer.Write(1u);
        writer.Write(0u); // entry
        writer.Write(0u); // program headers
        writer.Write(sectionTableOffset);
        writer.Write(0u); // flags
        writer.Write((ushort)52);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)40);
        writer.Write((ushort)(sections.Count + 1));
        writer.Write((ushort)1);
        return image.ToArray();
    }

    private static byte[] Abbreviations() =>
    [
        1, 0x11, 1, 0x03, 0x08, 0, 0, // CU: name/string
        2, 0x34, 0, 0x03, 0x08, 0x49, 0x13, 0x02, 0x18, 0, 0, // variable: name, type/ref4, location/exprloc
        3, 0x24, 0, 0x03, 0x08, 0x0b, 0x0b, 0x3e, 0x0b, 0, 0, // base type: name, size, encoding
        4, 0x13, 1, 0x03, 0x08, 0x0b, 0x0b, 0, 0, // struct: name, size
        5, 0x0d, 0, 0x03, 0x08, 0x49, 0x13, 0x38, 0x0b, 0, 0, // member: name, type, offset
        6, 0x01, 1, 0x49, 0x13, 0, 0, // array: element type
        7, 0x21, 0, 0x2f, 0x0b, 0, 0, // subrange: upper bound
        0
    ];

    private sealed record Section(string Name, uint Type, uint Flags, uint Address, byte[] Data, uint? VirtualSize = null);

    internal sealed class UnitBuilder : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public UnitBuilder(string source)
        {
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _writer.Write(0u);
            _writer.Write((ushort)4);
            _writer.Write(0u); // abbreviation offset
            _writer.Write((byte)4); // target address size
            _writer.Write((byte)1);
            WriteString(source);
        }

        public uint Scalar(string name, byte size = 4, byte encoding = 5)
        {
            var offset = (uint)_stream.Position;
            _writer.Write((byte)3);
            WriteString(name);
            _writer.Write(size);
            _writer.Write(encoding);
            return offset;
        }

        public uint Structure(string name, byte size, params (string Name, uint Type, byte Offset)[] members)
        {
            var offset = (uint)_stream.Position;
            _writer.Write((byte)4);
            WriteString(name);
            _writer.Write(size);
            foreach (var member in members)
            {
                _writer.Write((byte)5);
                WriteString(member.Name);
                _writer.Write(member.Type);
                _writer.Write(member.Offset);
            }
            _writer.Write((byte)0);
            return offset;
        }

        public uint Array(uint elementType, byte count)
        {
            var offset = (uint)_stream.Position;
            _writer.Write((byte)6);
            _writer.Write(elementType);
            _writer.Write((byte)7);
            _writer.Write((byte)(count - 1));
            _writer.Write((byte)0);
            return offset;
        }

        public void Variable(string name, uint address, uint type)
        {
            _writer.Write((byte)2);
            WriteString(name);
            _writer.Write(type);
            _writer.Write((byte)5); // expression length
            _writer.Write((byte)0x03); // DW_OP_addr
            _writer.Write(address);
        }

        public byte[] Complete()
        {
            _writer.Write((byte)0);
            var length = (uint)_stream.Length - 4;
            _stream.Position = 0;
            _writer.Write(length);
            return _stream.ToArray();
        }

        private void WriteString(string value)
        {
            _writer.Write(Encoding.UTF8.GetBytes(value));
            _writer.Write((byte)0);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
