#!/usr/bin/env python3
"""Offline check that a xacro expansion equals the plain URDF the exporter writes.

For a plain URDF and the expansion of a matching xacro (default parameters) the two robots must be the same:
link names and count, joint names, types, parents and children, origins, axes, limits, inertial mass, origin
and inertia, visual and collision geometry and mesh filenames. Numbers are compared with a tolerance; whitespace,
attribute order, XML comments and number formatting (1e-3 vs 0.001) are ignored.

Modes (each prints one PASS / FAIL / SKIP line per file, a summary, and can write JSON / Markdown):

  (default)   every committed examples/**/urdf/*.urdf is expanded with xacro and compared with itself.
              Needs the external `xacro` command or `python -m xacro`. Without it every file is SKIPPED.
  --pair URDF XACRO       compare a plain URDF with the expansion of a matching xacro (repeatable)
  --expanded URDF FILE    compare a plain URDF with an already expanded file; needs no xacro (repeatable)
  --identity              compare each committed example with itself; needs no xacro, proves the parser
                          reads every example

Exit code: 0 everything checked passed; 1 at least one FAIL (or an input could not be read / expanded);
2 bad command line (argparse); 3 nothing failed but at least one check was SKIPPED because xacro is not
installed (the equivalence was NOT verified). Standard library only.

Examples:
  python scripts/check_xacro_equivalence.py
  python scripts/check_xacro_equivalence.py --identity
  python scripts/check_xacro_equivalence.py --pair robot.urdf robot.urdf.xacro
  python scripts/check_xacro_equivalence.py --expanded robot.urdf robot_expanded.urdf --tolerance 1e-6
"""
import argparse
import datetime
import glob
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

EXIT_OK, EXIT_FAIL, EXIT_SKIPPED = 0, 1, 3
MAX_DIFFS_SHOWN = 20


# --------------------------------------------------------------------------------------------- comparison

def _number(token):
    try:
        value = float(token)
    except ValueError:
        return None
    return value if value == value else None  # nan is not comparable; fall back to text


def _same_number(a, b, tolerance):
    return abs(a - b) <= tolerance * max(1.0, abs(a), abs(b))


def _same_text(a, b, tolerance):
    """Equal as text with whitespace collapsed, or token by token as numbers within the tolerance."""
    ta, tb = a.split(), b.split()
    if len(ta) != len(tb):
        return False
    for x, y in zip(ta, tb):
        if x == y:
            continue
        nx, ny = _number(x), _number(y)
        if nx is None or ny is None or not _same_number(nx, ny, tolerance):
            return False
    return True


def _local(tag):
    """Drops an XML namespace ({uri}name -> name)."""
    return tag.rsplit("}", 1)[-1] if isinstance(tag, str) else tag


def _attributes(element):
    # xmlns declarations are not attributes for ElementTree; xacro:* attributes on the root are bookkeeping
    return {_local(k): v for k, v in element.attrib.items() if not _local(k).startswith("xmlns")}


def _keyed_children(element):
    """{key: child}. A child is keyed by tag and name when the names among its same-tag siblings are all
    present and unique, otherwise by tag and position."""
    children = [c for c in element if isinstance(c.tag, str)]  # drops comments / processing instructions
    by_tag = {}
    for child in children:
        by_tag.setdefault(_local(child.tag), []).append(child)
    keyed = {}
    for tag, group in by_tag.items():
        names = [c.get("name") for c in group]
        use_name = len(group) > 1 and all(names) and len(set(names)) == len(names)
        use_name = use_name or (len(group) == 1 and bool(names[0]) and tag in ("link", "joint"))
        for index, child in enumerate(group):
            key = "%s[%s]" % (tag, child.get("name")) if use_name else (tag if len(group) == 1 else "%s#%d" % (tag, index + 1))
            keyed[key] = child
    return keyed


def compare_elements(a, b, tolerance, path, diffs):
    """Appends (path, description) to diffs for every semantic difference between two elements."""
    attrs_a, attrs_b = _attributes(a), _attributes(b)
    for name in sorted(set(attrs_a) | set(attrs_b)):
        if name not in attrs_b:
            diffs.append(("%s@%s" % (path, name), "only in original: %r" % attrs_a[name]))
        elif name not in attrs_a:
            diffs.append(("%s@%s" % (path, name), "only in expanded: %r" % attrs_b[name]))
        elif not _same_text(attrs_a[name], attrs_b[name], tolerance):
            diffs.append(("%s@%s" % (path, name), "%r != %r" % (attrs_a[name], attrs_b[name])))
    text_a, text_b = (a.text or "").strip(), (b.text or "").strip()
    if not _same_text(text_a, text_b, tolerance):
        diffs.append(("%s/text()" % path, "%r != %r" % (text_a, text_b)))
    children_a, children_b = _keyed_children(a), _keyed_children(b)
    for key in sorted(set(children_a) | set(children_b)):
        child_path = "%s/%s" % (path, key)
        if key not in children_b:
            diffs.append((child_path, "only in original"))
        elif key not in children_a:
            diffs.append((child_path, "only in expanded"))
        else:
            compare_elements(children_a[key], children_b[key], tolerance, child_path, diffs)


def _robot(root, label):
    if _local(root.tag) != "robot":
        raise ValueError("%s: root element is <%s>, expected <robot>" % (label, _local(root.tag)))
    return root


def compare_robots(original, expanded, tolerance=1e-9):
    """Compares two <robot> elements. Returns (differences, facts) where differences is a list of
    (where, what) and facts is a one-line description of the original ("22 links, 13 joints")."""
    original, expanded = _robot(original, "original"), _robot(expanded, "expanded")
    diffs = []
    links_a = [c.get("name") for c in original if _local(c.tag) == "link"]
    links_b = [c.get("name") for c in expanded if _local(c.tag) == "link"]
    if len(links_a) != len(links_b):
        diffs.append(("robot", "link count %d != %d" % (len(links_a), len(links_b))))
    joints_a = [c.get("name") for c in original if _local(c.tag) == "joint"]
    joints_b = [c.get("name") for c in expanded if _local(c.tag) == "joint"]
    if len(joints_a) != len(joints_b):
        diffs.append(("robot", "joint count %d != %d" % (len(joints_a), len(joints_b))))
    compare_elements(original, expanded, tolerance, "robot[%s]" % original.get("name"), diffs)
    return diffs, "%d links, %d joints" % (len(links_a), len(joints_a))


def compare_strings(original_text, expanded_text, tolerance=1e-9):
    return compare_robots(ET.fromstring(original_text), ET.fromstring(expanded_text), tolerance)


def parse_file(path):
    return ET.parse(path).getroot()  # ElementTree drops comments by default


# ------------------------------------------------------------------------------------------------- xacro

def find_xacro():
    """Returns the command list that runs xacro, or None."""
    exe = shutil.which("xacro")
    if exe:
        return [exe]
    if importlib.util.find_spec("xacro") is not None:
        return [sys.executable, "-m", "xacro"]
    return None


def expand_xacro(command, path):
    """Runs xacro on a file and returns the expanded root element. Raises on failure."""
    done = subprocess.run(command + [path], capture_output=True, text=True, timeout=300)
    if done.returncode != 0:
        raise RuntimeError("xacro exited %d: %s" % (done.returncode, done.stderr.strip()[:300]))
    return ET.fromstring(done.stdout)


# -------------------------------------------------------------------------------------------------- results

class Results:
    """PASS / FAIL / SKIP per file. FAIL makes the run fail; SKIP means 'not verified' and has its own exit code."""

    def __init__(self):
        self.rows = []

    def add(self, case, status, detail="", differences=()):
        detail = re.sub(r"[A-Za-z]:[\\/][^ |)\]]*", "<path>", str(detail))
        self.rows.append({"case": case, "check": "xacro expansion equals URDF", "status": status,
                          "detail": detail, "differences": [{"where": w, "what": d} for w, d in differences]})
        text = "%-4s  %s" % (status, case)
        if detail:
            text += "  (%s)" % detail
        print(text, flush=True)
        for where, what in list(differences)[:MAX_DIFFS_SHOWN]:
            print("        - %s: %s" % (where, what), flush=True)
        if len(differences) > MAX_DIFFS_SHOWN:
            print("        ... and %d more" % (len(differences) - MAX_DIFFS_SHOWN), flush=True)

    def count(self, status):
        return sum(1 for r in self.rows if r["status"] == status)

    def table(self):
        lines = ["", "%-110s %s" % ("Case", "Result"), "-" * 120]
        lines += ["%-110s %s" % (r["case"][:110], r["status"]) for r in self.rows]
        lines.append("-" * 120)
        lines.append("PASS %d   FAIL %d   SKIP %d" % (self.count("PASS"), self.count("FAIL"), self.count("SKIP")))
        return "\n".join(lines)

    def markdown(self):
        lines = ["xacro equivalence check", "", "| Case | Result |", "|------|--------|"]
        for r in self.rows:
            extra = "; ".join("%s: %s" % (d["where"], d["what"]) for d in r["differences"][:MAX_DIFFS_SHOWN])
            lines.append("| %s | %s%s |" % (r["case"], r["status"], (" - " + extra.replace("|", "/")) if extra else ""))
        return "\n".join(lines) + "\n"

    def exit_code(self):
        if self.count("FAIL"):
            return EXIT_FAIL
        if self.count("SKIP"):
            return EXIT_SKIPPED
        return EXIT_OK


def check_pair(results, case, original_path, expanded_loader, tolerance):
    """expanded_loader is a function returning the expanded root; any exception is a FAIL, never a crash."""
    try:
        original = parse_file(original_path)
        expanded = expanded_loader()
        diffs, facts = compare_robots(original, expanded, tolerance)
    except Exception as error:  # noqa: BLE001
        results.add(case, "FAIL", "%s: %s" % (type(error).__name__, error))
        return
    results.add(case, "FAIL" if diffs else "PASS", ("%d differences" % len(diffs)) if diffs else facts, diffs)


def rel(path):
    try:
        return os.path.relpath(path, REPO).replace("\\", "/")
    except ValueError:
        return path


def example_urdfs(folder):
    return sorted(glob.glob(os.path.join(folder, "**", "urdf", "*.urdf"), recursive=True))


# ------------------------------------------------------------------------------------------------------ main

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pair", nargs=2, action="append", default=[], metavar=("URDF", "XACRO"),
                        help="compare a plain URDF with the xacro expansion of a matching file (needs xacro; repeatable)")
    parser.add_argument("--expanded", nargs=2, action="append", default=[], metavar=("URDF", "EXPANDED"),
                        help="compare a plain URDF with an already expanded file (needs no xacro; repeatable)")
    parser.add_argument("--identity", action="store_true",
                        help="compare each committed example with itself (needs no xacro)")
    parser.add_argument("--examples", help="examples folder to check (default: examples/ of this checkout)")
    parser.add_argument("--tolerance", type=float, default=1e-9, help="number tolerance, absolute for values below 1 and relative above (default 1e-9)")
    parser.add_argument("--json", help="write the result as JSON to this file")
    parser.add_argument("--markdown", help="write the result as a Markdown table to this file")
    args = parser.parse_args(argv)

    started = time.time()
    results = Results()
    explicit = bool(args.pair or args.expanded or args.identity)
    tolerance = args.tolerance
    examples = args.examples or os.path.join(REPO, "examples")

    for original, expanded in args.expanded:
        check_pair(results, "%s vs %s (expanded)" % (rel(original), rel(expanded)), original,
                   lambda p=expanded: parse_file(p), tolerance)

    if args.identity or not explicit:
        files = example_urdfs(examples)
        if not files:
            results.add("examples", "FAIL", "no examples/**/urdf/*.urdf found under %s" % examples)
        if args.identity:
            for path in files:
                check_pair(results, "%s vs itself (parser identity)" % rel(path), path, lambda p=path: parse_file(p), tolerance)

    needs_xacro = list(args.pair)
    if not explicit:
        needs_xacro += [(p, p) for p in example_urdfs(examples)]
    if needs_xacro:
        command = find_xacro()
        if command is None:
            print("xacro is not installed (no `xacro` command, no `python -m xacro`): the xacro-dependent checks "
                  "are SKIPPED, not passed. Install it (for example from the ROS packages or `pip install xacro`) "
                  "and run again; --expanded and --identity work without it.", flush=True)
        for original, source in needs_xacro:
            label = rel(original) if original == source else "%s vs %s" % (rel(original), rel(source))
            case = "%s (xacro expansion)" % label
            if command is None:
                results.add(case, "SKIP", "xacro not installed")
            else:
                check_pair(results, case, original, lambda s=source: expand_xacro(command, s), tolerance)

    print(results.table())
    code = results.exit_code()
    print("exit code %d (%s)" % (code, {EXIT_OK: "all checked passed", EXIT_FAIL: "failures",
                                        EXIT_SKIPPED: "nothing failed, but checks were SKIPPED: equivalence NOT verified"}[code]))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as stream:
            json.dump({"mode": "xacro-equivalence", "date": datetime.datetime.now().isoformat(timespec="seconds"),
                       "seconds": round(time.time() - started, 1), "failed": results.count("FAIL"),
                       "skipped": results.count("SKIP"), "checks": results.rows}, stream, indent=2)
    if args.markdown:
        with open(args.markdown, "w", encoding="utf-8") as stream:
            stream.write(results.markdown())
    return code


if __name__ == "__main__":
    sys.exit(main())
