#!/usr/bin/env python3
"""Checks the three seams no compiler and no test covers.

Every one of these has already broken this system once on the warehouse floor:

  1. A getString() called with the wrong number of arguments. Compiles, and
     throws the moment that branch runs -- which was the first time an unknown
     roll went past the gate.

  2. A findViewById naming an id that exists in some other layout. Compiles,
     returns null, and crashes when that screen opens.

  3. The reader app and the API disagreeing about a route or a field name.
     Both builds are green; the gate fails at the barrier.

Run before shipping an APK:

    python scripts/preflight.py

Exits non-zero if anything is wrong, so it can gate a build.
"""

from __future__ import annotations

import glob
import io
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, "android", "denim-rolls", "app", "src", "main")
API = os.path.join(ROOT, "src")


def read(path: str) -> str:
    return io.open(path, encoding="utf-8", errors="replace").read()


def java_files(root: str) -> list[str]:
    return glob.glob(os.path.join(root, "java", "**", "*.java"), recursive=True)


# --------------------------------------------------------------------- 1


def check_format_strings() -> list[str]:
    """Every getString(R.string.x, ...) matches the placeholders in strings.xml."""
    wanted: dict[str, int] = {}

    for f in glob.glob(os.path.join(APP, "res", "values*", "strings.xml")):
        for node in ET.parse(f).getroot().findall("string"):
            text = "".join(node.itertext())
            positional = [int(n) for n in re.findall(r"%(\d+)\$", text)]
            wanted[node.get("name")] = max(positional) if positional else len(
                re.findall(r"%[sd]", text))

    problems = []

    for f in java_files(APP):
        src = read(f)

        for call in re.finditer(r"getString\(\s*R\.string\.(\w+)", src):
            name = call.group(1)

            if name not in wanted:
                continue

            # Walk to the matching close paren rather than trusting a regex: a
            # nested getString in the arguments is one argument, not the end of
            # the call.
            i, depth, args = call.end(), 1, 0

            while i < len(src) and depth:
                ch = src[i]

                if ch in "([":
                    depth += 1
                elif ch in ")]":
                    depth -= 1
                elif ch == "," and depth == 1:
                    args += 1

                i += 1

            if args != wanted[name]:
                line = src[:call.start()].count("\n") + 1
                problems.append(
                    f"{os.path.basename(f)}:{line}  {name} takes {wanted[name]} "
                    f"value(s), given {args}")

    return problems


# --------------------------------------------------------------------- 2


def check_view_ids() -> list[str]:
    """Every findViewById names an id the screen's own layout defines."""
    layouts = {
        os.path.basename(f)[:-4]: set(re.findall(r'android:id="@\+id/(\w+)"', read(f)))
        for f in glob.glob(os.path.join(APP, "res", "layout", "*.xml"))
    }
    contents = {
        os.path.basename(f)[:-4]: read(f)
        for f in glob.glob(os.path.join(APP, "res", "layout", "*.xml"))
    }

    problems = []

    for f in java_files(APP):
        src = read(f)
        used = set(re.findall(r"findViewById\(\s*R\.id\.(\w+)\s*\)", src))
        named = set(re.findall(r"R\.layout\.(\w+)", src))

        if not used or not named:
            continue

        available: set[str] = set()

        for layout in named:
            available |= layouts.get(layout, set())

            # <include>d and inflated child layouts count as well
            for other, ids in layouts.items():
                if f'@layout/{other}"' in contents.get(layout, ""):
                    available |= ids

        for missing in sorted(used - available):
            problems.append(
                f"{os.path.basename(f)}  R.id.{missing} is not in "
                f"{', '.join(sorted(named))}")

    return problems


# --------------------------------------------------------------------- 3


def check_api_contract() -> list[str]:
    """Routes and field names the reader uses exist on the API side."""
    app_src = "\n".join(
        read(f) for f in glob.glob(
            os.path.join(APP, "java", "com", "smatechnology", "denimrolls", "data", "*.java")))
    api_src = "\n".join(
        read(f) for f in glob.glob(os.path.join(API, "**", "*.cs"), recursive=True))

    problems = []

    for route in sorted(set(re.findall(r'"(/api/[a-z0-9\-/]+)', app_src))):
        segments = [p for p in route.strip("/").split("/") if p != "api"]

        if not segments:
            continue

        controller, tail = segments[0], segments[-1]

        if not re.search(r'"api/' + re.escape(controller), api_src, re.I):
            problems.append(f"no controller serves {route}")
            continue

        if len(segments) > 1 and not re.search(
                r'Http(Get|Post|Put|Delete)\("[^"]*' + re.escape(tail), api_src, re.I):
            problems.append(f"no action serves {route}")

    # ASP.NET serialises properties as camelCase, so compare case-insensitively
    # against every property the API declares.
    declared = {m.lower() for m in re.findall(r"public\s+[\w\?<>,\[\]\s]+\s+(\w+)\s*\{\s*get", api_src)}

    for body in re.findall(r"record\s+\w+\s*\(([^)]*)\)", api_src, re.S):
        declared |= {m.lower() for m in re.findall(r"(\w+)\s*(?:,|$)", body)}

    # RFC 7807, served by the framework rather than declared by this code.
    declared |= {"detail", "title", "status", "type", "instance"}

    used = set(re.findall(
        r'(?:put|optString|optInt|optBoolean|optJSONArray|getJSONArray'
        r'|getJSONObject|optJSONObject|has)\("([a-zA-Z]\w*)"', app_src))

    # declared is lower-cased, because the API's C# names are PascalCase and
    # reach the wire as camelCase; compare on that footing, report the name
    # the app actually wrote.
    for field in sorted(f for f in used if f.lower() not in declared):
        problems.append(f'no API type declares the field "{field}"')

    return problems


# ---------------------------------------------------------------------


def main() -> int:
    checks = (
        ("message arguments", check_format_strings),
        ("view ids", check_view_ids),
        ("reader/API contract", check_api_contract),
    )

    failed = 0

    for name, run in checks:
        problems = run()

        if problems:
            failed += len(problems)
            print(f"FAIL  {name}")
            for problem in problems:
                print(f"        {problem}")
        else:
            print(f"ok    {name}")

    print()
    print("nothing to fix" if not failed else f"{failed} problem(s) to fix")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
