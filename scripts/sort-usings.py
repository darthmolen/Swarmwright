#!/usr/bin/env python3
"""Sort the leading using-directive block of C# files to satisfy StyleCop SA1208/SA1210.

System.* directives come first, then the rest, each group ordinal-sorted by namespace
(the trailing ';' is ignored so a shorter namespace that is a prefix sorts first, e.g.
System.Text before System.Text.Json). Preserves UTF-8 BOM and CRLF line endings.

Usage: python3 scripts/sort-usings.py <file.cs> [<file.cs> ...]
"""
import sys


def sort_file(path: str) -> None:
    data = open(path, "rb").read()
    bom = b""
    if data.startswith(b"\xef\xbb\xbf"):
        bom = b"\xef\xbb\xbf"
        data = data[3:]
    lines = data.decode("utf-8").split("\n")

    i = 0
    while i < len(lines) and lines[i].strip().rstrip("\r") == "":
        i += 1
    start = i
    while i < len(lines) and lines[i].lstrip().startswith("using "):
        i += 1
    block = lines[start:i]
    if not block:
        return

    def ns(line: str) -> str:
        s = line.strip().rstrip("\r")
        if s.startswith("using "):
            s = s[len("using "):]
        return s.rstrip(";")

    system = sorted((l for l in block if ns(l).startswith("System")), key=ns)
    rest = sorted((l for l in block if not ns(l).startswith("System")), key=ns)
    lines[start:i] = system + rest
    open(path, "wb").write(bom + "\n".join(lines).encode("utf-8"))


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        sort_file(arg)
