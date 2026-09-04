#!/usr/bin/env python3
"""Re-align PE files (DLL/EXE) to a 4096-byte file alignment so that Wine can map them from disk.

Wine maps a PE image straight from the file only when its sections start at page-aligned file offsets. Most of
Chromium's DLLs are linked with FileAlignment 512, so Wine reads them into anonymous memory instead: every process
that loads libcef.dll (272 MB) gets its own fully resident copy, nothing is shared or paged on demand. With
FileAlignment 4096 the images are mapped and shared between the browser host, its renderer and its utility
processes: measured 1.5 GB -> 0.86 GB of resident memory for an idle page (docs/CEF-UPGRADE.md).

Only the file layout changes: sections keep their virtual addresses and bytes, headers are padded to a page,
each section's raw data is moved to a page-aligned offset and padded, FileAlignment/SizeOfHeaders are updated,
the checksum is cleared and an Authenticode signature (file-offset based) is dropped. Windows loads such files
just as well, so packages are re-aligned once, at build time.

Usage: pe-realign.py [--check] <file or directory>...   (directories: every *.dll and *.exe in them, not recursive)
"""
import os
import struct
import sys

PAGE = 4096


def realign_bytes(data):
    """Returns the re-aligned image, or None when the file is not a PE that needs it."""
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return None
    coff = pe + 4
    nsections = struct.unpack_from("<H", data, coff + 2)[0]
    opt_size = struct.unpack_from("<H", data, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    if magic not in (0x10B, 0x20B):
        return None
    section_align = struct.unpack_from("<I", data, opt + 32)[0]
    file_align = struct.unpack_from("<I", data, opt + 36)[0]
    if file_align >= PAGE or section_align < PAGE or section_align % PAGE:
        return None  # already fine (or an unusual layout we leave alone)

    size_of_headers_off = opt + 60
    checksum_off = opt + 64
    ndirs_off = opt + (108 if magic == 0x20B else 92)
    dirs_off = ndirs_off + 4
    ndirs = struct.unpack_from("<I", data, ndirs_off)[0]
    sections_off = opt + opt_size

    out = bytearray(data[:sections_off + nsections * 40])
    headers_end = len(out)
    headers_size = (headers_end + PAGE - 1) // PAGE * PAGE
    out += bytes(headers_size - headers_end)

    for i in range(nsections):
        s = sections_off + i * 40
        raw_size, raw_ptr = struct.unpack_from("<II", data, s + 16)
        if raw_size and raw_ptr:
            chunk = data[raw_ptr:raw_ptr + raw_size]
            new_ptr = len(out)
            out += chunk
            pad = (-len(chunk)) % PAGE
            out += bytes(pad)
            struct.pack_into("<II", out, s + 16, len(chunk) + pad, new_ptr)
        else:
            struct.pack_into("<II", out, s + 16, 0, 0)

    struct.pack_into("<I", out, opt + 36, PAGE)                 # FileAlignment
    struct.pack_into("<I", out, size_of_headers_off, headers_size)
    struct.pack_into("<I", out, checksum_off, 0)                # nobody verifies it for user-mode images
    if ndirs > 4:                                               # IMAGE_DIRECTORY_ENTRY_SECURITY: file offsets, dropped
        struct.pack_into("<II", out, dirs_off + 4 * 8, 0, 0)
    return bytes(out)


def check_bytes(data):
    """(file alignment, section alignment) of a PE, or None."""
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return None
    opt = pe + 24
    return struct.unpack_from("<I", data, opt + 36)[0], struct.unpack_from("<I", data, opt + 32)[0]


def files_of(paths):
    for p in paths:
        if os.path.isdir(p):
            for name in sorted(os.listdir(p)):
                if name.lower().endswith((".dll", ".exe")):
                    yield os.path.join(p, name)
        else:
            yield p


def main(argv):
    check_only = "--check" in argv
    paths = [a for a in argv if a != "--check"]
    if not paths:
        print(__doc__)
        return 2
    changed = 0
    for path in files_of(paths):
        with open(path, "rb") as f:
            data = f.read()
        info = check_bytes(data)
        if info is None:
            continue
        if check_only:
            fa, sa = info
            if fa < PAGE and sa >= PAGE:
                print("%s: FileAlignment %d (Wine copies it into memory per process)" % (path, fa))
                changed += 1
            continue
        new = realign_bytes(data)
        if new is None:
            continue
        tmp = path + ".realign.tmp"
        with open(tmp, "wb") as f:
            f.write(new)
        os.replace(tmp, path)
        changed += 1
        print("%s: FileAlignment %d -> 4096 (%.1f -> %.1f MB)" % (path, info[0], len(data) / 1048576, len(new) / 1048576))
    if check_only:
        return 1 if changed else 0
    print("%d file(s) re-aligned" % changed)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
