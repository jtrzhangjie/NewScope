#!/usr/bin/env python3
"""Extract plottable global variables from an ARM ELF/DWARF file.

The output is a compact JSON catalog consumed by NewScope.  It intentionally
keeps only absolute-address scalar leaves (including small scalar arrays and
members of structs/unions) because those are what a live memory scope can read.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from elftools.elf.elffile import ELFFile


@dataclass
class TypeInfo:
    display: str
    size: int
    kind: str
    signed: bool = False


def attr(die: Any, name: str) -> Any | None:
    return die.attributes.get(name)


def attr_int(die: Any, name: str, default: int | None = None) -> int | None:
    value = attr(die, name)
    if value is None:
        return default
    raw = value.value
    return raw if isinstance(raw, int) else default


def attr_name(die: Any) -> str | None:
    value = attr(die, "DW_AT_name")
    if value is None:
        return None
    raw = value.value
    if isinstance(raw, bytes):
        return raw.decode(errors="replace")
    return str(raw)


def ref_type(die: Any) -> Any | None:
    if attr(die, "DW_AT_type") is None:
        return None
    try:
        return die.get_DIE_from_attribute("DW_AT_type")
    except Exception:
        return None


def underlying(die: Any, limit: int = 16) -> Any:
    """Strip typedef/qualifier wrappers, returning the real type DIE."""
    for _ in range(limit):
        if die.tag in (
            "DW_TAG_typedef",
            "DW_TAG_const_type",
            "DW_TAG_volatile_type",
            "DW_TAG_restrict_type",
            "DW_TAG_atomic_type",
        ):
            child = ref_type(die)
            if child is None:
                break
            die = child
        else:
            break
    return die


def array_count(die: Any) -> int | None:
    count = 1
    found = False
    for child in die.iter_children():
        if child.tag != "DW_TAG_subrange_type":
            continue
        found = True
        upper = attr_int(child, "DW_AT_upper_bound")
        explicit = attr_int(child, "DW_AT_count")
        if explicit is not None:
            count *= explicit
        elif upper is not None:
            count *= upper + 1
        else:
            return None
    return count if found else None


def describe_type(die: Any, depth: int = 0) -> TypeInfo:
    if depth > 24:
        return TypeInfo("unknown", 4, "unknown")

    if die.tag == "DW_TAG_typedef":
        name = attr_name(die) or "typedef"
        child = ref_type(die)
        if child is None:
            return TypeInfo(name, attr_int(die, "DW_AT_byte_size", 0) or 0, "unknown")
        inner = describe_type(child, depth + 1)
        return TypeInfo(name, inner.size, inner.kind, inner.signed)

    if die.tag in (
        "DW_TAG_const_type",
        "DW_TAG_volatile_type",
        "DW_TAG_restrict_type",
        "DW_TAG_atomic_type",
    ):
        child = ref_type(die)
        return describe_type(child, depth + 1) if child is not None else TypeInfo("unknown", 4, "unknown")

    if die.tag == "DW_TAG_base_type":
        name = attr_name(die) or "base"
        size = attr_int(die, "DW_AT_byte_size", 4) or 4
        encoding = attr_int(die, "DW_AT_encoding", 0) or 0
        if encoding in (5, 6, 13):  # signed, signed char, signed fixed
            kind, signed = "int", True
        elif encoding in (1, 7, 8, 14):  # address, unsigned, unsigned char, unsigned fixed
            kind, signed = "int", False
        elif encoding == 2:  # boolean
            kind, signed = "bool", False
        elif encoding in (3, 4):  # complex float, float
            kind, signed = "float", True
        else:
            kind, signed = "unknown", False
        return TypeInfo(name, size, kind, signed)

    if die.tag == "DW_TAG_enumeration_type":
        return TypeInfo(attr_name(die) or "enum", attr_int(die, "DW_AT_byte_size", 4) or 4, "int", True)

    if die.tag in ("DW_TAG_structure_type", "DW_TAG_class_type", "DW_TAG_union_type"):
        return TypeInfo(
            attr_name(die) or "(anonymous)",
            attr_int(die, "DW_AT_byte_size", 0) or 0,
            "struct" if die.tag != "DW_TAG_union_type" else "union",
        )

    if die.tag == "DW_TAG_pointer_type":
        return TypeInfo(attr_name(die) or "pointer", attr_int(die, "DW_AT_byte_size", 4) or 4, "pointer")

    if die.tag == "DW_TAG_array_type":
        element_die = ref_type(die)
        if element_die is None:
            return TypeInfo("array", 0, "array")
        element = describe_type(element_die, depth + 1)
        count = array_count(die)
        if count is None:
            return TypeInfo(f"{element.display}[]", 0, "array")
        return TypeInfo(f"{element.display}[{count}]", element.size * count, "array")

    return TypeInfo(attr_name(die) or "unknown", attr_int(die, "DW_AT_byte_size", 4) or 4, "unknown")


def member_offset(die: Any) -> int | None:
    value = attr(die, "DW_AT_data_member_location")
    if value is None:
        return None
    raw = value.value
    if isinstance(raw, int):
        return raw
    if isinstance(raw, (list, bytes, bytearray)):
        data = list(raw)
        if not data:
            return None
        # DW_OP_plus_uconst is the common encoding for a member offset.
        if data[0] == 0x23:
            result = 0
            shift = 0
            for byte in data[1:]:
                result |= (byte & 0x7F) << shift
                if not (byte & 0x80):
                    return result
                shift += 7
        # Some producers emit a bare ULEB128 expression.
        if len(data) == 1 and data[0] < 0x80:
            return data[0]
    return None


def location_address(die: Any) -> int | None:
    value = attr(die, "DW_AT_location")
    if value is None:
        return None
    raw = value.value
    if isinstance(raw, int):
        return raw
    if isinstance(raw, (list, bytes, bytearray)):
        data = list(raw)
        if len(data) >= 5 and data[0] == 0x03:  # DW_OP_addr, 32-bit target
            return int.from_bytes(bytes(data[1:5]), "little")
    return None


def is_string_array(die: Any) -> bool:
    real = underlying(die)
    if real.tag != "DW_TAG_array_type":
        return False
    element = ref_type(real)
    if element is None:
        return False
    element = underlying(element)
    if element.tag != "DW_TAG_base_type":
        return False
    name = (attr_name(element) or "").lower()
    return "char" in name or "int8" in name or "uint8" in name


def extract(elf_path: str, ram_start: int, ram_size: int) -> dict[str, Any]:
    variables: dict[str, dict[str, Any]] = {}
    duplicate_count = 0
    skipped = 0
    ram_end = ram_start + ram_size

    def add(name: str, address: int, type_die: Any) -> None:
        if not name or address < ram_start or address >= ram_end:
            return
        info = describe_type(type_die)
        if info.kind not in ("int", "float", "bool", "enum"):
            return
        if info.size not in (1, 2, 4, 8):
            return
        if name in variables:
            nonlocal duplicate_count
            duplicate_count += 1
            return
        variables[name] = {
            "name": name,
            "address": address,
            "size": info.size,
            "type": info.display,
            "signed": bool(info.signed),
            "kind": info.kind,
        }

    def emit_array(name: str, address: int, type_die: Any) -> None:
        real = underlying(type_die)
        element_die = ref_type(real)
        count = array_count(real)
        if element_die is None or count is None or count <= 0 or count > 16:
            return
        if is_string_array(real):
            return
        element_info = describe_type(element_die)
        element_real = underlying(element_die)
        if element_info.kind not in ("int", "float", "bool", "enum"):
            return
        if element_info.size not in (1, 2, 4, 8):
            return
        for index in range(count):
            add(f"{name}[{index}]", address + index * element_info.size, element_die)

    def flatten(prefix: str, address: int, type_die: Any, depth: int = 0) -> bool:
        if depth > 8:
            return False
        real = underlying(type_die)

        if real.tag == "DW_TAG_array_type":
            emit_array(prefix, address, type_die)
            return True

        if real.tag not in ("DW_TAG_structure_type", "DW_TAG_class_type", "DW_TAG_union_type"):
            return False

        any_member = False
        for child in real.iter_children():
            if child.tag != "DW_TAG_member":
                continue
            any_member = True
            if attr(child, "DW_AT_bit_size") is not None:
                continue
            member_type = ref_type(child)
            if member_type is None:
                continue
            offset = member_offset(child)
            if offset is None:
                continue
            name = attr_name(child)
            member_real = underlying(member_type)

            full_name = f"{prefix}.{name}" if name else prefix
            if member_real.tag in ("DW_TAG_structure_type", "DW_TAG_class_type", "DW_TAG_union_type"):
                flatten(full_name, address + offset, member_type, depth + 1)
            elif member_real.tag == "DW_TAG_array_type":
                if name:
                    emit_array(full_name, address + offset, member_type)
            else:
                if name:
                    add(full_name, address + offset, member_type)
                elif member_real.tag not in ("DW_TAG_structure_type", "DW_TAG_class_type", "DW_TAG_union_type"):
                    skipped += 1
        return any_member

    with open(elf_path, "rb") as handle:
        elf = ELFFile(handle)
        if not elf.has_dwarf_info():
            raise RuntimeError("ELF has no DWARF information")
        dwarf = elf.get_dwarf_info()
        for cu in dwarf.iter_CUs():
            for die in cu.iter_DIEs():
                if die.tag != "DW_TAG_variable":
                    continue
                name = attr_name(die)
                if not name:
                    continue
                address = location_address(die)
                if address is None:
                    continue
                type_die = ref_type(die)
                if type_die is None:
                    continue
                real = underlying(type_die)
                if real.tag in (
                    "DW_TAG_structure_type",
                    "DW_TAG_class_type",
                    "DW_TAG_union_type",
                    "DW_TAG_array_type",
                ):
                    flatten(name, address, type_die)
                else:
                    add(name, address, type_die)

    ordered = sorted(variables.values(), key=lambda item: (item["address"], item["name"].lower()))
    return {
        "format": "newscope-symbols-v1",
        "elf": os.path.abspath(elf_path),
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "ramStart": ram_start,
        "ramSize": ram_size,
        "variableCount": len(ordered),
        "duplicatesSkipped": duplicate_count,
        "anonymousMembersSkipped": skipped,
        "variables": ordered,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("elf", help="path to the .axf/.elf file")
    parser.add_argument("-o", "--output", help="output JSON path")
    parser.add_argument("--ram-start", default="0x10000000", help="start of target RAM (default: 0x10000000)")
    parser.add_argument("--ram-size", default="0x1000", help="size of target RAM (default: 0x1000)")
    parser.add_argument("--summary", action="store_true", help="print the first 40 extracted names")
    args = parser.parse_args()

    catalog = extract(args.elf, int(args.ram_start, 0), int(args.ram_size, 0))
    output = args.output or os.path.splitext(args.elf)[0] + ".symbols.json"
    with open(output, "w", encoding="utf-8") as handle:
        json.dump(catalog, handle, ensure_ascii=False, indent=2)
    print(f"wrote {catalog['variableCount']} variables to {output}")
    if args.summary:
        for item in catalog["variables"][:40]:
            print(f"  {item['address']:#010x} {item['size']:>2} {item['type']:<24} {item['name']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
