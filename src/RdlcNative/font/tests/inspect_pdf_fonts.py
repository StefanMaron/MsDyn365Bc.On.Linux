#!/usr/bin/env python3
"""Read-only SFNT/PDF font audit using qpdf and the Python standard library.

Nominal /W comparison is intended for the simple fixture, not a claim that a
contextually positioned run's advances must always equal unadjusted hmtx.
"""
import argparse
import json
import struct
import subprocess


def qpdf(pdf, *options):
    return subprocess.run(
        ["qpdf", *options, pdf], check=True, stdout=subprocess.PIPE
    ).stdout


def require(condition, message):
    if not condition:
        raise ValueError(message)


def checksum(data):
    padded = data + bytes((-len(data)) % 4)
    return sum(struct.unpack(">%dI" % (len(padded) // 4), padded)) & 0xFFFFFFFF


def tables(font):
    require(font[:4] == b"\0\1\0\0", "FontFile2 is not standalone TrueType")
    count = struct.unpack_from(">H", font, 4)[0]
    require(12 + count * 16 <= len(font), "Truncated SFNT table directory")
    result = {}
    for index in range(count):
        tag, expected, offset, length = struct.unpack_from(">4sIII", font, 12 + index * 16)
        require(offset % 4 == 0 and offset + length <= len(font), "Invalid SFNT table bounds")
        require(tag not in result, "Duplicate SFNT table")
        data = font[offset:offset + length]
        check_data = data[:8] + bytes(4) + data[12:] if tag == b"head" else data
        require(checksum(check_data) == expected, "Bad checksum: " + repr(tag))
        result[tag] = data
    require(checksum(font) == 0xB1B0AFBA, "Bad whole-font checksum adjustment")
    return result


def widths(values):
    result = {}
    index = 0
    while index < len(values):
        first, following = values[index:index + 2]
        index += 2
        if isinstance(following, list):
            for delta, width in enumerate(following):
                result[first + delta] = width
        else:
            width = values[index]
            index += 1
            for glyph in range(first, following + 1):
                result[glyph] = width
    return result


def inspect(pdf):
    objects = json.loads(qpdf(pdf, "--json", "--json-stream-data=none"))["qpdf"][1]
    reports = []
    for name, entry in objects.items():
        cid = entry.get("value")
        if not isinstance(cid, dict) or cid.get("/Subtype") != "/CIDFontType2":
            continue
        descriptor = objects["obj:" + cid["/FontDescriptor"]]["value"]
        ref = descriptor["/FontFile2"]
        stream = objects["obj:" + ref]["stream"]["dict"]
        font = qpdf(pdf, "--show-object=" + ref.split()[0], "--filtered-stream-data")
        require(len(font) == stream["/Length1"], "FontFile2 Length1 mismatch")
        table = tables(font)
        upem = struct.unpack_from(">H", table[b"head"], 18)[0]
        glyph_count = struct.unpack_from(">H", table[b"maxp"], 4)[0]
        horizontal_count = struct.unpack_from(">H", table[b"hhea"], 34)[0]
        require(0 < horizontal_count <= glyph_count, "Invalid hmtx metric count")
        require(len(table[b"hmtx"]) >= horizontal_count * 4 +
                (glyph_count - horizontal_count) * 2, "Truncated hmtx")
        pdf_widths = widths(cid.get("/W", []))
        largest_delta = 0.0
        for glyph, width in pdf_widths.items():
            require(0 <= glyph < glyph_count, "PDF CID is outside embedded glyph range")
            advance = struct.unpack_from(">H", table[b"hmtx"],
                                         min(glyph, horizontal_count - 1) * 4)[0]
            delta = abs(width - advance * 1000 / upem)
            largest_delta = max(largest_delta, delta)
            require(delta <= 0.001, "PDF /W differs from nominal hmtx for CID %d" % glyph)
        ascent, descent = struct.unpack_from(">hh", table[b"OS/2"], 68)
        require(descriptor["/Ascent"] == round(ascent * 1000 / upem), "Descriptor ascent mismatch")
        require(descriptor["/Descent"] == round(descent * 1000 / upem), "Descriptor descent mismatch")
        italic = struct.unpack_from(">i", table[b"post"], 4)[0] / 65536
        require(descriptor["/ItalicAngle"] == round(italic), "Descriptor italic angle mismatch")
        box = struct.unpack_from(">hhhh", table[b"head"], 36)
        scaled_box = [value * 1000 / upem for value in box]
        pdf_box = descriptor["/FontBBox"]
        require(all(pdf_box[i] <= scaled_box[i] + 1 for i in (0, 1)) and
                all(pdf_box[i] >= scaled_box[i] - 1 for i in (2, 3)),
                "Descriptor bbox does not enclose the embedded font bbox")
        require(cid.get("/CIDToGIDMap", "/Identity") == "/Identity",
                "Audit expects original identity CID-to-GID mapping")
        reports.append({
            "cid_object": name,
            "base_font": cid["/BaseFont"],
            "font_bytes": len(font),
            "units_per_em": upem,
            "embedded_glyph_slots": glyph_count,
            "pdf_width_entries": len(pdf_widths),
            "largest_width_delta": largest_delta,
            "ascent": descriptor["/Ascent"],
            "descent": descriptor["/Descent"],
            "bbox": pdf_box,
            "fsType": struct.unpack_from(">H", table[b"OS/2"], 8)[0],
            "table_checksums_and_font_checksum": "valid",
        })
    require(reports, "No embedded CID TrueType fonts found")
    return reports


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("pdf")
    print(json.dumps(inspect(parser.parse_args().pdf), indent=2))
