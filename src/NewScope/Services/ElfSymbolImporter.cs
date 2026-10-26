using System.Buffers.Binary;
using System.Text;
using NewScope.Models;

namespace NewScope.Services;

public static class ElfSymbolImporter
{
    public static SymbolCatalog Import(string path)
    {
        var image = ElfImage.Load(path);
        var ranges = image.WritableRanges();
        if (ranges.Count == 0)
        {
            throw new InvalidDataException("The ELF file has no writable allocated sections (RAM).");
        }

        var dwarf = DwarfReader.Read(image);
        if (dwarf.Units.Count == 0)
        {
            throw new InvalidDataException(
                "The ELF file has no DWARF debug information. Build the project with debug symbols and import the .axf/.elf output.");
        }

        var collector = new SymbolCollector(dwarf, ranges);
        var variables = collector.Collect();

        if (variables.Count == 0)
        {
            throw new InvalidDataException(
                "No plottable variables were found. Make sure the ELF contains DWARF debug info and globals live in RAM.");
        }

        var ramStart = ranges.Min(range => range.Start);
        var ramEnd = ranges.Max(range => range.End);
        var ramSize = ramEnd > ramStart ? ramEnd - ramStart : 0;
        if (ramStart > uint.MaxValue || ramSize > uint.MaxValue)
        {
            throw new InvalidDataException("Only 32-bit target addresses are supported.");
        }

        return new SymbolCatalog
        {
            Format = "newscope-symbols-v1",
            Elf = Path.GetFullPath(path),
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            RamStart = (uint)ramStart,
            RamSize = (uint)ramSize,
            VariableCount = variables.Count,
            Variables = variables
        };
    }

    private readonly record struct MemoryRange(ulong Start, ulong End)
    {
        public bool Contains(ulong address) => address >= Start && address < End;
    }

    private sealed class ElfSection
    {
        public required string Name { get; init; }
        public required uint Type { get; init; }
        public required ulong Flags { get; init; }
        public required ulong Address { get; init; }
        public required ulong Offset { get; init; }
        public required ulong Size { get; init; }
        public required byte[] Data { get; init; }
    }

    private sealed class ElfImage
    {
        private const uint ShtNobits = 8;
        private const ulong ShfWrite = 1;
        private const ulong ShfAlloc = 2;

        private ElfImage(List<ElfSection> sections, bool littleEndian)
        {
            Sections = sections;
            LittleEndian = littleEndian;
        }

        public List<ElfSection> Sections { get; }
        public bool LittleEndian { get; }

        public ElfSection? Section(string name) =>
            Sections.FirstOrDefault(section => string.Equals(section.Name, name, StringComparison.Ordinal));

        public List<MemoryRange> WritableRanges()
        {
            var ranges = Sections
                .Where(section => (section.Flags & (ShfWrite | ShfAlloc)) == (ShfWrite | ShfAlloc) && section.Size > 0)
                .Select(section => new MemoryRange(section.Address, section.Address + section.Size))
                .Where(range => range.End > range.Start)
                .OrderBy(range => range.Start)
                .ToList();

            var merged = new List<MemoryRange>();
            foreach (var range in ranges)
            {
                if (merged.Count == 0)
                {
                    merged.Add(range);
                    continue;
                }

                var last = merged[^1];
                if (range.Start <= last.End)
                {
                    merged[^1] = new MemoryRange(last.Start, Math.Max(last.End, range.End));
                }
                else
                {
                    merged.Add(range);
                }
            }

            return merged;
        }

        public static ElfImage Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 52 ||
                bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
            {
                throw new InvalidDataException("The selected file is not an ELF/AXF file.");
            }

            var elfClass = bytes[4];
            if (elfClass is not (1 or 2))
            {
                throw new InvalidDataException("Unsupported ELF class.");
            }

            var is64 = elfClass == 2;
            var littleEndian = bytes[5] switch
            {
                1 => true,
                2 => false,
                _ => throw new InvalidDataException("Unsupported ELF byte order.")
            };

            var reader = new ByteReader(bytes, littleEndian, 0, bytes.Length);
            var sectionOffset = is64 ? reader.ReadU64At(0x28) : reader.ReadU32At(0x20);
            var sectionEntrySize = is64 ? reader.ReadU16At(0x3A) : reader.ReadU16At(0x2E);
            var sectionCount = is64 ? reader.ReadU16At(0x3C) : reader.ReadU16At(0x30);
            var stringSectionIndex = is64 ? reader.ReadU16At(0x3E) : reader.ReadU16At(0x32);

            if (sectionOffset == 0 || sectionEntrySize == 0 || sectionCount == 0)
            {
                throw new InvalidDataException("The ELF file has no section table.");
            }

            if (sectionOffset + (ulong)sectionEntrySize * sectionCount > (ulong)bytes.Length)
            {
                throw new InvalidDataException("The ELF section table is outside the file.");
            }

            var rawSections = new List<(uint NameOffset, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size)>();
            for (var index = 0; index < sectionCount; index++)
            {
                var position = checked((int)(sectionOffset + (ulong)index * sectionEntrySize));
                if (is64)
                {
                    rawSections.Add((
                        reader.ReadU32At(position),
                        reader.ReadU32At(position + 4),
                        reader.ReadU64At(position + 8),
                        reader.ReadU64At(position + 16),
                        reader.ReadU64At(position + 24),
                        reader.ReadU64At(position + 32)));
                }
                else
                {
                    rawSections.Add((
                        reader.ReadU32At(position),
                        reader.ReadU32At(position + 4),
                        reader.ReadU32At(position + 8),
                        reader.ReadU32At(position + 12),
                        reader.ReadU32At(position + 16),
                        reader.ReadU32At(position + 20)));
                }
            }

            if (stringSectionIndex >= rawSections.Count)
            {
                throw new InvalidDataException("The ELF section-name table is invalid.");
            }

            var stringSection = rawSections[stringSectionIndex];
            if (stringSection.Offset + stringSection.Size > (ulong)bytes.Length)
            {
                throw new InvalidDataException("The ELF section-name table is outside the file.");
            }

            var stringData = bytes.AsSpan((int)stringSection.Offset, (int)stringSection.Size);
            var sections = new List<ElfSection>(rawSections.Count);
            foreach (var raw in rawSections)
            {
                var name = ReadCString(stringData, raw.NameOffset);
                byte[] data;
                if (raw.Type == ShtNobits || raw.Size == 0)
                {
                    data = [];
                }
                else
                {
                    if (raw.Offset + raw.Size > (ulong)bytes.Length)
                    {
                        throw new InvalidDataException($"ELF section '{name}' is outside the file.");
                    }

                    data = bytes.AsSpan((int)raw.Offset, (int)raw.Size).ToArray();
                }

                sections.Add(new ElfSection
                {
                    Name = name,
                    Type = raw.Type,
                    Flags = raw.Flags,
                    Address = raw.Address,
                    Offset = raw.Offset,
                    Size = raw.Size,
                    Data = data
                });
            }

            return new ElfImage(sections, littleEndian);
        }

        private static string ReadCString(ReadOnlySpan<byte> data, uint offset)
        {
            if (offset >= data.Length)
            {
                return "";
            }

            var start = (int)offset;
            var end = start;
            while (end < data.Length && data[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(data[start..end]);
        }
    }

    private sealed class DwarfContext
    {
        public required ElfImage Image { get; init; }
        public required byte[] Info { get; init; }
        public required byte[] Abbrev { get; init; }
        public required byte[] Strings { get; init; }
        public required byte[] LineStrings { get; init; }
        public required byte[] Addresses { get; init; }
        public required byte[] StringOffsets { get; init; }
        public Dictionary<uint, DwarfAbbrevTable> AbbrevTables { get; } = [];
        public List<DwarfUnit> Units { get; } = [];
        public Dictionary<ulong, DwarfDie> Dies { get; } = [];

        public DwarfAbbrevTable GetAbbrevTable(uint offset)
        {
            if (AbbrevTables.TryGetValue(offset, out var table))
            {
                return table;
            }

            table = DwarfAbbrevTable.Parse(Abbrev, offset, Image.LittleEndian);
            AbbrevTables[offset] = table;
            return table;
        }

        public string GetString(uint offset, bool lineString = false)
        {
            var data = lineString ? LineStrings : Strings;
            if (offset >= data.Length)
            {
                return "";
            }

            var start = (int)offset;
            var end = start;
            while (end < data.Length && data[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(data, start, end - start);
        }

        public string ResolveIndexedString(DwarfUnit unit, ulong index)
        {
            var offset = unit.StringOffsetsBase + index * unit.OffsetSize;
            if (offset + unit.OffsetSize > (ulong)StringOffsets.Length)
            {
                return "";
            }

            var value = ByteReader.ReadUnsignedAt(StringOffsets, (int)offset, unit.OffsetSize, Image.LittleEndian);
            return GetString(checked((uint)value));
        }

        public ulong ResolveIndexedAddress(DwarfUnit unit, ulong index)
        {
            var offset = unit.AddressBase + index * unit.AddressSize;
            if (offset + unit.AddressSize > (ulong)Addresses.Length)
            {
                return 0;
            }

            return ByteReader.ReadUnsignedAt(Addresses, (int)offset, unit.AddressSize, Image.LittleEndian);
        }
    }

    private sealed class DwarfUnit
    {
        public required ulong Offset { get; init; }
        public required int End { get; init; }
        public required ushort Version { get; init; }
        public required byte AddressSize { get; init; }
        public required byte OffsetSize { get; init; }
        public required uint AbbrevOffset { get; init; }
        public ulong StringOffsetsBase { get; set; } = 8;
        public ulong AddressBase { get; set; } = 8;
        public DwarfDie? Root { get; set; }
    }

    private sealed class DwarfDie
    {
        public required ulong Offset { get; init; }
        public required DwarfUnit Unit { get; init; }
        public required uint Tag { get; init; }
        public required bool HasChildren { get; init; }
        public DwarfDie? Parent { get; set; }
        public List<DwarfDie> Children { get; } = [];
        public Dictionary<uint, DwarfAttribute> Attributes { get; } = [];

        public DwarfAttribute? Attribute(uint name) =>
            Attributes.TryGetValue(name, out var attribute) ? attribute : null;

        public string? Text(uint name) => Attribute(name)?.Text;
    }

    private sealed class DwarfAttribute
    {
        public uint Form { get; init; }
        public ulong Value { get; init; }
        public long SignedValue { get; init; }
        public bool IsSigned { get; init; }
        public string? Text { get; init; }
        public byte[]? Block { get; init; }
        public ulong Reference { get; init; }
    }

    private sealed class DwarfAbbrevTable
    {
        private readonly Dictionary<uint, DwarfAbbrevDecl> _declarations;

        private DwarfAbbrevTable(Dictionary<uint, DwarfAbbrevDecl> declarations)
        {
            _declarations = declarations;
        }

        public DwarfAbbrevDecl? Get(uint code) =>
            _declarations.TryGetValue(code, out var declaration) ? declaration : null;

        public static DwarfAbbrevTable Parse(byte[] data, uint offset, bool littleEndian)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("DWARF abbreviation table offset is invalid.");
            }

            var reader = new ByteReader(data, littleEndian, (int)offset, data.Length);
            var declarations = new Dictionary<uint, DwarfAbbrevDecl>();
            while (reader.Position < reader.Length)
            {
                var code = checked((uint)reader.ReadUleb());
                if (code == 0)
                {
                    break;
                }

                var tag = checked((uint)reader.ReadUleb());
                var hasChildren = reader.ReadU8() != 0;
                var attributes = new List<DwarfAbbrevAttribute>();
                while (reader.Position < reader.Length)
                {
                    var name = checked((uint)reader.ReadUleb());
                    var form = checked((uint)reader.ReadUleb());
                    if (name == 0 && form == 0)
                    {
                        break;
                    }

                    var implicitConst = form == DwarfForms.ImplicitConst ? reader.ReadSleb() : 0;
                    attributes.Add(new DwarfAbbrevAttribute(name, form, implicitConst));
                }

                declarations[code] = new DwarfAbbrevDecl(tag, hasChildren, attributes);
            }

            return new DwarfAbbrevTable(declarations);
        }
    }

    private readonly record struct DwarfAbbrevAttribute(uint Name, uint Form, long ImplicitConst);

    private sealed record DwarfAbbrevDecl(uint Tag, bool HasChildren, List<DwarfAbbrevAttribute> Attributes);

    private static class DwarfForms
    {
        public const uint Addr = 0x01;
        public const uint Block2 = 0x03;
        public const uint Block4 = 0x04;
        public const uint Data2 = 0x05;
        public const uint Data4 = 0x06;
        public const uint Data8 = 0x07;
        public const uint String = 0x08;
        public const uint Block = 0x09;
        public const uint Block1 = 0x0A;
        public const uint Data1 = 0x0B;
        public const uint Flag = 0x0C;
        public const uint SData = 0x0D;
        public const uint StrP = 0x0E;
        public const uint UData = 0x0F;
        public const uint RefAddr = 0x10;
        public const uint Ref1 = 0x11;
        public const uint Ref2 = 0x12;
        public const uint Ref4 = 0x13;
        public const uint Ref8 = 0x14;
        public const uint RefUData = 0x15;
        public const uint Indirect = 0x16;
        public const uint SecOffset = 0x17;
        public const uint ExprLoc = 0x18;
        public const uint FlagPresent = 0x19;
        public const uint StrX = 0x1A;
        public const uint AddrX = 0x1B;
        public const uint RefSup4 = 0x1C;
        public const uint StrPSup = 0x1D;
        public const uint Data16 = 0x1E;
        public const uint LineStrP = 0x1F;
        public const uint RefSig8 = 0x20;
        public const uint ImplicitConst = 0x21;
        public const uint LocListX = 0x22;
        public const uint RngListX = 0x23;
        public const uint RefSup8 = 0x24;
        public const uint StrX1 = 0x25;
        public const uint StrX2 = 0x26;
        public const uint StrX3 = 0x27;
        public const uint StrX4 = 0x28;
        public const uint AddrX1 = 0x29;
        public const uint AddrX2 = 0x2A;
        public const uint AddrX3 = 0x2B;
        public const uint AddrX4 = 0x2C;
    }

    private static class DwarfTags
    {
        public const uint ArrayType = 0x01;
        public const uint ClassType = 0x02;
        public const uint EnumerationType = 0x04;
        public const uint Member = 0x0D;
        public const uint PointerType = 0x0F;
        public const uint StructureType = 0x13;
        public const uint Typedef = 0x16;
        public const uint UnionType = 0x17;
        public const uint SubrangeType = 0x21;
        public const uint BaseType = 0x24;
        public const uint ConstType = 0x26;
        public const uint Variable = 0x34;
        public const uint VolatileType = 0x35;
        public const uint RestrictType = 0x37;
        public const uint AtomicType = 0x47;
    }

    private static class DwarfAttributes
    {
        public const uint Location = 0x02;
        public const uint Name = 0x03;
        public const uint ByteSize = 0x0B;
        public const uint BitSize = 0x0D;
        public const uint UpperBound = 0x2F;
        public const uint AbstractOrigin = 0x31;
        public const uint Count = 0x37;
        public const uint DataMemberLocation = 0x38;
        public const uint Declaration = 0x3C;
        public const uint Encoding = 0x3E;
        public const uint Type = 0x49;
        public const uint Specification = 0x47;
        public const uint StringOffsetsBase = 0x72;
        public const uint AddressBase = 0x73;
    }

    private sealed class DwarfReader
    {
        public static DwarfContext Read(ElfImage image)
        {
            var info = image.Section(".debug_info")
                       ?? throw new InvalidDataException("The ELF file has no .debug_info section.");
            var abbrev = image.Section(".debug_abbrev")
                         ?? throw new InvalidDataException("The ELF file has no .debug_abbrev section.");
            var strings = image.Section(".debug_str")?.Data ?? [];
            var lineStrings = image.Section(".debug_line_str")?.Data ?? [];
            var addresses = image.Section(".debug_addr")?.Data ?? [];
            var stringOffsets = image.Section(".debug_str_offsets")?.Data ?? [];

            var context = new DwarfContext
            {
                Image = image,
                Info = info.Data,
                Abbrev = abbrev.Data,
                Strings = strings,
                LineStrings = lineStrings,
                Addresses = addresses,
                StringOffsets = stringOffsets
            };

            var reader = new ByteReader(context.Info, image.LittleEndian, 0, context.Info.Length);
            while (reader.Position + 4 <= reader.Length)
            {
                var unitStart = reader.Position;
                var initialLength = reader.ReadU32();
                if (initialLength == 0)
                {
                    break;
                }

                var is64 = initialLength == uint.MaxValue;
                var contentLength = is64 ? reader.ReadU64() : initialLength;
                var contentStart = reader.Position;
                var contentEnd = checked(contentStart + (int)contentLength);
                if (contentLength > int.MaxValue || contentEnd > reader.Length)
                {
                    throw new InvalidDataException("DWARF compilation unit extends outside .debug_info.");
                }

                var version = reader.ReadU16();
                var offsetSize = (byte)(is64 ? 8 : 4);
                byte addressSize;
                uint abbrevOffset;
                if (version >= 5)
                {
                    var unitType = reader.ReadU8();
                    addressSize = reader.ReadU8();
                    abbrevOffset = checked((uint)(is64 ? reader.ReadU64() : reader.ReadU32()));

                    if (unitType is 2 or 6)
                    {
                        reader.Seek(contentEnd);
                        continue;
                    }
                }
                else
                {
                    abbrevOffset = checked((uint)(is64 ? reader.ReadU64() : reader.ReadU32()));
                    addressSize = reader.ReadU8();
                }

                if (addressSize is not (4 or 8))
                {
                    reader.Seek(contentEnd);
                    continue;
                }

                var unit = new DwarfUnit
                {
                    Offset = (ulong)unitStart,
                    End = contentEnd,
                    Version = version,
                    AddressSize = addressSize,
                    OffsetSize = offsetSize,
                    AbbrevOffset = abbrevOffset
                };

                var table = context.GetAbbrevTable(abbrevOffset);
                var root = ReadDie(reader, unit, context, table, null);
                if (root is null)
                {
                    reader.Seek(contentEnd);
                    continue;
                }

                unit.Root = root;
                context.Units.Add(unit);
                context.Dies[root.Offset] = root;
                ReadBases(unit, root);

                if (root.HasChildren && reader.Position < contentEnd)
                {
                    ReadChildren(reader, unit, context, table, root);
                }

                reader.Seek(contentEnd);
            }

            return context;
        }

        private static void ReadBases(DwarfUnit unit, DwarfDie root)
        {
            var stringBase = root.Attribute(DwarfAttributes.StringOffsetsBase);
            if (stringBase is not null && stringBase.Form == DwarfForms.SecOffset)
            {
                unit.StringOffsetsBase = stringBase.Value;
            }

            var addressBase = root.Attribute(DwarfAttributes.AddressBase);
            if (addressBase is not null && addressBase.Form == DwarfForms.SecOffset)
            {
                unit.AddressBase = addressBase.Value;
            }
        }

        private static void ReadChildren(
            ByteReader reader,
            DwarfUnit unit,
            DwarfContext context,
            DwarfAbbrevTable table,
            DwarfDie parent)
        {
            while (reader.Position < unit.End)
            {
                var die = ReadDie(reader, unit, context, table, parent);
                if (die is null)
                {
                    return;
                }

                parent.Children.Add(die);
                context.Dies[die.Offset] = die;
                if (die.HasChildren)
                {
                    ReadChildren(reader, unit, context, table, die);
                }
            }
        }

        private static DwarfDie? ReadDie(
            ByteReader reader,
            DwarfUnit unit,
            DwarfContext context,
            DwarfAbbrevTable table,
            DwarfDie? parent)
        {
            var offset = (ulong)reader.Position;
            var code = reader.ReadUleb();
            if (code == 0)
            {
                return null;
            }

            var declaration = table.Get(checked((uint)code))
                              ?? throw new InvalidDataException($"Unknown DWARF abbreviation code {code}.");
            var die = new DwarfDie
            {
                Offset = offset,
                Unit = unit,
                Tag = declaration.Tag,
                HasChildren = declaration.HasChildren,
                Parent = parent
            };

            foreach (var attribute in declaration.Attributes)
            {
                var form = attribute.Form;
                if (form == DwarfForms.Indirect)
                {
                    form = checked((uint)reader.ReadUleb());
                }

                var value = ReadAttribute(reader, unit, context, form, attribute.ImplicitConst);
                die.Attributes[attribute.Name] = value;
            }

            return die;
        }

        private static DwarfAttribute ReadAttribute(
            ByteReader reader,
            DwarfUnit unit,
            DwarfContext context,
            uint form,
            long implicitConst)
        {
            switch (form)
            {
                case DwarfForms.Addr:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = reader.ReadUnsigned(unit.AddressSize)
                    };

                case DwarfForms.Block1:
                    return BlockAttribute(form, reader.ReadBytes(reader.ReadU8()));

                case DwarfForms.Block2:
                    return BlockAttribute(form, reader.ReadBytes(checked((int)reader.ReadU16())));

                case DwarfForms.Block4:
                    return BlockAttribute(form, reader.ReadBytes(checked((int)reader.ReadU32())));

                case DwarfForms.Block:
                    return BlockAttribute(form, reader.ReadBytes(checked((int)reader.ReadUleb())));

                case DwarfForms.ExprLoc:
                    return BlockAttribute(form, reader.ReadBytes(checked((int)reader.ReadUleb())));

                case DwarfForms.Data16:
                    return BlockAttribute(form, reader.ReadBytes(16));

                case DwarfForms.Data1:
                    return new DwarfAttribute { Form = form, Value = reader.ReadU8() };

                case DwarfForms.Data2:
                    return new DwarfAttribute { Form = form, Value = reader.ReadU16() };

                case DwarfForms.Data4:
                    return new DwarfAttribute { Form = form, Value = reader.ReadU32() };

                case DwarfForms.Data8:
                    return new DwarfAttribute { Form = form, Value = reader.ReadU64() };

                case DwarfForms.UData:
                    return new DwarfAttribute { Form = form, Value = reader.ReadUleb() };

                case DwarfForms.SData:
                {
                    var signed = reader.ReadSleb();
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = unchecked((ulong)signed),
                        SignedValue = signed,
                        IsSigned = true
                    };
                }

                case DwarfForms.ImplicitConst:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = unchecked((ulong)implicitConst),
                        SignedValue = implicitConst,
                        IsSigned = true
                    };

                case DwarfForms.String:
                    return new DwarfAttribute { Form = form, Text = reader.ReadCString() };

                case DwarfForms.StrP:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.GetString(checked((uint)reader.ReadUnsigned(unit.OffsetSize)))
                    };

                case DwarfForms.LineStrP:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.GetString(checked((uint)reader.ReadUnsigned(unit.OffsetSize)), lineString: true)
                    };

                case DwarfForms.StrX:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.ResolveIndexedString(unit, reader.ReadUleb())
                    };

                case DwarfForms.StrX1:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.ResolveIndexedString(unit, reader.ReadU8())
                    };

                case DwarfForms.StrX2:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.ResolveIndexedString(unit, reader.ReadU16())
                    };

                case DwarfForms.StrX3:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.ResolveIndexedString(unit, reader.ReadUnsigned(3))
                    };

                case DwarfForms.StrX4:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Text = context.ResolveIndexedString(unit, reader.ReadU32())
                    };

                case DwarfForms.AddrX:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = context.ResolveIndexedAddress(unit, reader.ReadUleb())
                    };

                case DwarfForms.AddrX1:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = context.ResolveIndexedAddress(unit, reader.ReadU8())
                    };

                case DwarfForms.AddrX2:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = context.ResolveIndexedAddress(unit, reader.ReadU16())
                    };

                case DwarfForms.AddrX3:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = context.ResolveIndexedAddress(unit, reader.ReadUnsigned(3))
                    };

                case DwarfForms.AddrX4:
                    return new DwarfAttribute
                    {
                        Form = form,
                        Value = context.ResolveIndexedAddress(unit, reader.ReadU32())
                    };

                case DwarfForms.Flag:
                    return new DwarfAttribute { Form = form, Value = reader.ReadU8() };

                case DwarfForms.FlagPresent:
                    return new DwarfAttribute { Form = form, Value = 1 };

                case DwarfForms.Ref1:
                    return ReferenceAttribute(form, unit.Offset + reader.ReadU8());

                case DwarfForms.Ref2:
                    return ReferenceAttribute(form, unit.Offset + reader.ReadU16());

                case DwarfForms.Ref4:
                    return ReferenceAttribute(form, unit.Offset + reader.ReadU32());

                case DwarfForms.Ref8:
                    return ReferenceAttribute(form, unit.Offset + reader.ReadU64());

                case DwarfForms.RefUData:
                    return ReferenceAttribute(form, unit.Offset + reader.ReadUleb());

                case DwarfForms.RefAddr:
                    return ReferenceAttribute(form, reader.ReadUnsigned(unit.OffsetSize));

                case DwarfForms.SecOffset:
                    return new DwarfAttribute { Form = form, Value = reader.ReadUnsigned(unit.OffsetSize) };

                case DwarfForms.RefSig8:
                    reader.ReadU64();
                    return new DwarfAttribute { Form = form };

                case DwarfForms.RefSup4:
                    reader.ReadU32();
                    return new DwarfAttribute { Form = form };

                case DwarfForms.RefSup8:
                    reader.ReadU64();
                    return new DwarfAttribute { Form = form };

                case DwarfForms.StrPSup:
                    reader.ReadUnsigned(unit.OffsetSize);
                    return new DwarfAttribute { Form = form, Text = "" };

                case DwarfForms.LocListX:
                case DwarfForms.RngListX:
                    return new DwarfAttribute { Form = form, Value = reader.ReadUleb() };

                default:
                    throw new InvalidDataException($"Unsupported DWARF form 0x{form:X2}.");
            }
        }

        private static DwarfAttribute BlockAttribute(uint form, byte[] data) =>
            new() { Form = form, Block = data };

        private static DwarfAttribute ReferenceAttribute(uint form, ulong reference) =>
            new() { Form = form, Reference = reference, Value = reference };
    }

    private sealed record TypeInfo(string Display, int Size, string Kind, bool Signed)
    {
        public static readonly TypeInfo Unknown = new("unknown", 0, "unknown", false);
    }

    private sealed class SymbolCollector
    {
        private readonly DwarfContext _context;
        private readonly List<MemoryRange> _ranges;
        private readonly Dictionary<(string Name, ulong Address), SymbolVariable> _variables = [];

        public SymbolCollector(DwarfContext context, List<MemoryRange> ranges)
        {
            _context = context;
            _ranges = ranges;
        }

        public List<SymbolVariable> Collect()
        {
            foreach (var die in _context.Dies.Values.OrderBy(item => item.Offset))
            {
                if (die.Tag != DwarfTags.Variable || IsDeclaration(die))
                {
                    continue;
                }

                var name = NameOf(die);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var location = LocationAddress(die);
                if (location is null)
                {
                    continue;
                }

                var type = ResolveType(die, 0);
                if (type is null)
                {
                    continue;
                }

                var real = Underlying(type);
                if (real is not null && real.Tag is DwarfTags.StructureType or DwarfTags.ClassType or
                        DwarfTags.UnionType or DwarfTags.ArrayType)
                {
                    Flatten(name, location.Value, type, 0);
                }
                else
                {
                    Add(name, location.Value, type);
                }
            }

            return _variables.Values
                .OrderBy(variable => variable.Address)
                .ThenBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsDeclaration(DwarfDie die)
        {
            var declaration = die.Attribute(DwarfAttributes.Declaration);
            return declaration is not null && declaration.Value != 0;
        }

        private DwarfDie? ResolveType(DwarfDie die, int depth)
        {
            if (depth > 24)
            {
                return null;
            }

            var direct = die.Attribute(DwarfAttributes.Type);
            if (direct is not null && _context.Dies.TryGetValue(direct.Reference, out var type))
            {
                return type;
            }

            var specification = Specification(die);
            if (specification is not null && !ReferenceEquals(specification, die))
            {
                return ResolveType(specification, depth + 1);
            }

            return null;
        }

        private DwarfDie? Specification(DwarfDie die)
        {
            foreach (var attributeName in new[] { DwarfAttributes.Specification, DwarfAttributes.AbstractOrigin })
            {
                var attribute = die.Attribute(attributeName);
                if (attribute is not null && _context.Dies.TryGetValue(attribute.Reference, out var target))
                {
                    return target;
                }
            }

            return null;
        }

        private string? NameOf(DwarfDie die, int depth = 0)
        {
            var name = die.Text(DwarfAttributes.Name);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            if (depth > 8)
            {
                return null;
            }

            var specification = Specification(die);
            return specification is null || ReferenceEquals(specification, die)
                ? null
                : NameOf(specification, depth + 1);
        }

        private ulong? LocationAddress(DwarfDie die, int depth = 0)
        {
            if (depth > 24)
            {
                return null;
            }

            var location = die.Attribute(DwarfAttributes.Location);
            if (location?.Block is not { Length: > 0 } block)
            {
                var specification = Specification(die);
                if (specification is not null && !ReferenceEquals(specification, die))
                {
                    return LocationAddress(specification, depth + 1);
                }

                return null;
            }

            if (block[0] == 0x03 && block.Length >= 1 + die.Unit.AddressSize)
            {
                return ByteReader.ReadUnsignedAt(block, 1, die.Unit.AddressSize, _context.Image.LittleEndian);
            }

            if (block[0] == 0xA1)
            {
                var reader = new ByteReader(block, _context.Image.LittleEndian, 1, block.Length);
                return _context.ResolveIndexedAddress(die.Unit, reader.ReadUleb());
            }

            return null;
        }

        private DwarfDie? Underlying(DwarfDie? die, int depth = 0)
        {
            if (die is null || depth > 16)
            {
                return die;
            }

            if (die.Tag is DwarfTags.Typedef or DwarfTags.ConstType or DwarfTags.VolatileType or
                DwarfTags.RestrictType or DwarfTags.AtomicType)
            {
                var child = ResolveType(die, 0);
                return child is null ? die : Underlying(child, depth + 1);
            }

            return die;
        }

        private TypeInfo Describe(DwarfDie? die, int depth = 0)
        {
            if (die is null || depth > 24)
            {
                return TypeInfo.Unknown;
            }

            var size = ByteSize(die) ?? 0;
            switch (die.Tag)
            {
                case DwarfTags.Typedef:
                {
                    var child = ResolveType(die, 0);
                    var inner = Describe(child, depth + 1);
                    return new TypeInfo(NameOf(die) ?? "typedef", inner.Size, inner.Kind, inner.Signed);
                }

                case DwarfTags.ConstType:
                case DwarfTags.VolatileType:
                case DwarfTags.RestrictType:
                case DwarfTags.AtomicType:
                    return Describe(ResolveType(die, 0), depth + 1);

                case DwarfTags.BaseType:
                {
                    var encoding = (int)(die.Attribute(DwarfAttributes.Encoding)?.Value ?? 0);
                    var (kind, signed) = encoding switch
                    {
                        5 or 6 or 13 => ("int", true),
                        1 or 7 or 8 or 14 => ("int", false),
                        2 => ("bool", false),
                        3 or 4 => ("float", true),
                        _ => ("unknown", false)
                    };
                    return new TypeInfo(NameOf(die) ?? "base", size > 0 ? (int)size : 4, kind, signed);
                }

                case DwarfTags.EnumerationType:
                    return new TypeInfo(NameOf(die) ?? "enum", size > 0 ? (int)size : 4, "int", true);

                case DwarfTags.StructureType:
                case DwarfTags.ClassType:
                case DwarfTags.UnionType:
                    return new TypeInfo(
                        NameOf(die) ?? "(anonymous)",
                        (int)size,
                        die.Tag == DwarfTags.UnionType ? "union" : "struct",
                        false);

                case DwarfTags.PointerType:
                    return new TypeInfo(NameOf(die) ?? "pointer", size > 0 ? (int)size : 4, "pointer", false);

                case DwarfTags.ArrayType:
                {
                    var elementDie = ResolveType(die, 0);
                    if (elementDie is null)
                    {
                        return new TypeInfo("array", 0, "array", false);
                    }

                    var element = Describe(elementDie, depth + 1);
                    var count = ArrayCount(die);
                    if (count is null)
                    {
                        return new TypeInfo($"{element.Display}[]", 0, "array", false);
                    }

                    return new TypeInfo(
                        $"{element.Display}[{count.Value}]",
                        checked(element.Size * (int)count.Value),
                        "array",
                        false);
                }

                default:
                    return new TypeInfo(NameOf(die) ?? "unknown", size > 0 ? (int)size : 4, "unknown", false);
            }
        }

        private ulong? ByteSize(DwarfDie die)
        {
            var attribute = die.Attribute(DwarfAttributes.ByteSize);
            return attribute?.Value;
        }

        private ulong? ArrayCount(DwarfDie array)
        {
            ulong count = 1;
            var found = false;
            foreach (var child in array.Children)
            {
                if (child.Tag != DwarfTags.SubrangeType)
                {
                    continue;
                }

                found = true;
                var explicitCount = child.Attribute(DwarfAttributes.Count);
                if (explicitCount is not null)
                {
                    count = checked(count * explicitCount.Value);
                    continue;
                }

                var upper = child.Attribute(DwarfAttributes.UpperBound);
                if (upper is not null)
                {
                    count = checked(count * (upper.Value + 1));
                    continue;
                }

                return null;
            }

            return found ? count : null;
        }

        private bool IsStringArray(DwarfDie array)
        {
            var element = ResolveType(array, 0);
            var real = Underlying(element);
            if (real?.Tag != DwarfTags.BaseType)
            {
                return false;
            }

            var name = (NameOf(real) ?? "").ToLowerInvariant();
            return name.Contains("char") || name.Contains("int8") || name.Contains("uint8");
        }

        private void EmitArray(string name, ulong address, DwarfDie type)
        {
            var real = Underlying(type);
            if (real?.Tag != DwarfTags.ArrayType || IsStringArray(real))
            {
                return;
            }

            var elementDie = ResolveType(real, 0);
            var count = ArrayCount(real);
            if (elementDie is null || count is null or 0 or > 16)
            {
                return;
            }

            var element = Describe(elementDie);
            if (element.Kind is not ("int" or "float" or "bool" or "enum") ||
                element.Size is not (1 or 2 or 4 or 8))
            {
                return;
            }

            for (ulong index = 0; index < count.Value; index++)
            {
                var elementAddress = address + index * (ulong)element.Size;
                Add($"{name}[{index}]", elementAddress, elementDie);
            }
        }

        private bool Flatten(string prefix, ulong address, DwarfDie type, int depth)
        {
            if (depth > 8)
            {
                return false;
            }

            var real = Underlying(type);
            if (real is null)
            {
                return false;
            }

            if (real.Tag == DwarfTags.ArrayType)
            {
                EmitArray(prefix, address, type);
                return true;
            }

            if (real.Tag is not (DwarfTags.StructureType or DwarfTags.ClassType or DwarfTags.UnionType))
            {
                return false;
            }

            var anyMember = false;
            foreach (var member in real.Children)
            {
                if (member.Tag != DwarfTags.Member)
                {
                    continue;
                }

                anyMember = true;
                if (member.Attribute(DwarfAttributes.BitSize) is not null)
                {
                    continue;
                }

                var memberType = ResolveType(member, 0);
                if (memberType is null)
                {
                    continue;
                }

                var offset = MemberOffset(member) ?? (real.Tag == DwarfTags.UnionType ? 0UL : null);
                if (offset is null)
                {
                    continue;
                }

                var name = NameOf(member);
                var memberReal = Underlying(memberType);
                var fullName = string.IsNullOrEmpty(name) ? prefix : $"{prefix}.{name}";

                if (memberReal?.Tag is DwarfTags.StructureType or DwarfTags.ClassType or DwarfTags.UnionType)
                {
                    Flatten(fullName, address + offset.Value, memberType, depth + 1);
                }
                else if (memberReal?.Tag == DwarfTags.ArrayType)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        EmitArray(fullName, address + offset.Value, memberType);
                    }
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    Add(fullName, address + offset.Value, memberType);
                }
            }

            return anyMember;
        }

        private ulong? MemberOffset(DwarfDie member)
        {
            var attribute = member.Attribute(DwarfAttributes.DataMemberLocation);
            if (attribute is null)
            {
                return null;
            }

            if (attribute.Block is { Length: > 0 } block)
            {
                if (block[0] == 0x23)
                {
                    var reader = new ByteReader(block, _context.Image.LittleEndian, 1, block.Length);
                    return reader.ReadUleb();
                }

                if (block[0] == 0x10)
                {
                    var reader = new ByteReader(block, _context.Image.LittleEndian, 1, block.Length);
                    var value = reader.ReadUleb();
                    if (reader.Position < reader.Length && block[reader.Position] == 0x22)
                    {
                        return value;
                    }
                }

                if (block.Length == 1 && block[0] < 0x80)
                {
                    return block[0];
                }

                return null;
            }

            return attribute.IsSigned
                ? attribute.SignedValue >= 0 ? (ulong)attribute.SignedValue : null
                : attribute.Value;
        }

        private void Add(string name, ulong address, DwarfDie type)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var info = Describe(type);
            if (info.Kind is not ("int" or "float" or "bool" or "enum") ||
                info.Size is not (1 or 2 or 4 or 8) ||
                !_ranges.Any(range => range.Contains(address) && (ulong)info.Size <= range.End - address))
            {
                return;
            }

            var identity = (name, address);
            if (_variables.ContainsKey(identity))
            {
                return;
            }

            _variables[identity] = new SymbolVariable
            {
                Name = name,
                Address = checked((uint)address),
                Size = info.Size,
                Type = info.Display,
                Signed = info.Signed,
                Kind = info.Kind
            };
        }
    }

    private sealed class ByteReader
    {
        private readonly byte[] _data;
        private readonly bool _littleEndian;

        public ByteReader(byte[] data, bool littleEndian, int offset, int length)
        {
            _data = data;
            _littleEndian = littleEndian;
            Position = offset;
            Length = length;
        }

        public int Position { get; private set; }
        public int Length { get; }

        public void Seek(int position)
        {
            if (position < 0 || position > Length)
            {
                throw new InvalidDataException("DWARF offset is outside the section.");
            }

            Position = position;
        }

        public byte ReadU8()
        {
            EnsureAvailable(1);
            return _data[Position++];
        }

        public ushort ReadU16()
        {
            EnsureAvailable(2);
            var span = _data.AsSpan(Position, 2);
            Position += 2;
            return _littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(span)
                : BinaryPrimitives.ReadUInt16BigEndian(span);
        }

        public uint ReadU32()
        {
            EnsureAvailable(4);
            var span = _data.AsSpan(Position, 4);
            Position += 4;
            return _littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(span)
                : BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        public ulong ReadU64()
        {
            EnsureAvailable(8);
            var span = _data.AsSpan(Position, 8);
            Position += 8;
            return _littleEndian
                ? BinaryPrimitives.ReadUInt64LittleEndian(span)
                : BinaryPrimitives.ReadUInt64BigEndian(span);
        }

        public ulong ReadUnsigned(int bytes) => ReadUnsigned(_data, Position, bytes, _littleEndian, advance: true);

        public ulong ReadUleb()
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                if (shift >= 64)
                {
                    throw new InvalidDataException("Invalid ULEB128 value.");
                }

                var value = ReadU8();
                result |= (ulong)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }
        }

        public long ReadSleb()
        {
            long result = 0;
            var shift = 0;
            byte value;
            do
            {
                if (shift >= 64)
                {
                    throw new InvalidDataException("Invalid SLEB128 value.");
                }

                value = ReadU8();
                result |= (long)(value & 0x7F) << shift;
                shift += 7;
            } while ((value & 0x80) != 0);

            if (shift < 64 && (value & 0x40) != 0)
            {
                result |= -1L << shift;
            }

            return result;
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw new InvalidDataException("Invalid DWARF block length.");
            }

            EnsureAvailable(count);
            var result = _data.AsSpan(Position, count).ToArray();
            Position += count;
            return result;
        }

        public string ReadCString()
        {
            var start = Position;
            while (Position < Length && _data[Position] != 0)
            {
                Position++;
            }

            var result = Encoding.UTF8.GetString(_data, start, Position - start);
            if (Position < Length)
            {
                Position++;
            }

            return result;
        }

        public uint ReadU32At(int offset) => checked((uint)ReadUnsigned(_data, offset, 4, _littleEndian));

        public ushort ReadU16At(int offset) => checked((ushort)ReadUnsigned(_data, offset, 2, _littleEndian));

        public ulong ReadU64At(int offset) => ReadUnsigned(_data, offset, 8, _littleEndian);

        private ulong ReadUnsigned(byte[] data, int offset, int bytes, bool littleEndian, bool advance = false)
        {
            if (bytes is not (1 or 2 or 3 or 4 or 8))
            {
                throw new InvalidDataException($"Unsupported integer width {bytes}.");
            }

            if (offset < 0 || offset + bytes > Length)
            {
                throw new InvalidDataException("DWARF value is outside the section.");
            }

            ulong value = 0;
            if (littleEndian)
            {
                for (var index = bytes - 1; index >= 0; index--)
                {
                    value = (value << 8) | data[offset + index];
                }
            }
            else
            {
                for (var index = 0; index < bytes; index++)
                {
                    value = (value << 8) | data[offset + index];
                }
            }

            if (advance)
            {
                Position += bytes;
            }

            return value;
        }

        public static ulong ReadUnsignedAt(byte[] data, int offset, int bytes, bool littleEndian)
        {
            if (bytes is not (1 or 2 or 3 or 4 or 8))
            {
                throw new InvalidDataException($"Unsupported integer width {bytes}.");
            }

            if (offset < 0 || offset + bytes > data.Length)
            {
                throw new InvalidDataException("DWARF value is outside the section.");
            }

            ulong value = 0;
            if (littleEndian)
            {
                for (var index = bytes - 1; index >= 0; index--)
                {
                    value = (value << 8) | data[offset + index];
                }
            }
            else
            {
                for (var index = 0; index < bytes; index++)
                {
                    value = (value << 8) | data[offset + index];
                }
            }

            return value;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || Position + count > Length)
            {
                throw new InvalidDataException("Unexpected end of DWARF data.");
            }
        }
    }
}
