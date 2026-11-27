"""Export form check, end to end and without a person (issue 177).

Builds small models in YOUR OWN SolidWorks, shows the real AssemblyExportForm of a private SW2URDF.dll (real
ExportHelper) on a desktop of its own, switches units, types, changes pages, exports through the real buttons and the
Save As dialog, and checks the written files against the typed inputs and against what SolidWorks itself reports.
No expectation comes from the exporter. Nothing waits for a click: dialogs are answered and recorded, and there is a
limit per scenario and for the whole run.

  python scripts/test_form_e2e.py --dll <folder with SW2URDF.dll> --launcher <launch_solidworks.py> --out <folder>
  python scripts/test_form_e2e.py --dll <folder with SW2URDF.dll> --pid <pid of an empty SolidWorks> --out <folder>
  python scripts/test_form_e2e.py --evaluate <folder of an earlier run>        checks the recorded files again
  python scripts/test_form_e2e.py --prove <folder of an earlier run>           proves that every check can fail

--prove changes one recorded thing per check (a number in the written URDF, a text in a box, a missing file) in a copy
of the run and requires that check to report FAIL. Checks that need a feature a build does not have (angle
unit, .xacro file name, 12 digit boxes) are SKIPPED, not passed.

Exit code: 0 nothing failed (skipped checks are listed), 1 at least one check failed, 3 the run hit its time limit.
Needs pywin32. The unit the user chose in the forms (HKCU\\Software\\SW2URDF) is saved and put back.
"""
import argparse
import datetime
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET

import check_xacro_equivalence as xacro_check
import regress_obj_export as obj

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE = "robot_pkg"
KEY, VALUE = r"Software\SW2URDF", "AngleUnit"
AUTO = "Automatically Generate"

# ------------------------------------------------------------------------------------------- scenarios

HINGE_TREE = [("base_link", "-", "LIM_BASE-1", "Origin_global", "", "", ""),
              ("arm", "base_link", "LIM_ARM-1", AUTO, AUTO, "Automatically Detect", "arm_joint")]
MULTI_TREE = [("base_link", "-", "LIM_BASE-1", "Origin_global", "", "", ""),
              ("arm", "base_link", "LIM_ARM-1", AUTO, AUTO, "Automatically Detect", "arm_joint"),
              ("block", "arm", "FORM_BLOCK-1", "CS_BLOCK", "", "fixed", "block_joint"),
              ("post", "base_link", "LIM_BASE-2", "CS_POST", "", "fixed", "post_joint"),
              ("slider", "post", "LIM_ARM-2", AUTO, AUTO, "Automatically Detect", "slide_joint")]
# the same model with both bases in the base link: the limit mate of the slide is with its second component
TWO_BASES_TREE = [("base_link", "-", "LIM_BASE-1,LIM_BASE-2", "Origin_global", "", "", ""), MULTI_TREE[1],
                  ("slider", "base_link", "LIM_ARM-2", AUTO, AUTO, "Automatically Detect", "slide_joint")]
FULL = [("export", "full", PACKAGE), ("read", "sw")]
UNIT_DEG, UNIT_RAD = ("unit", "deg"), ("unit", "rad")

# name: (model, tree, needs, steps). Steps of "unit" are left out on a build without the angle unit.
SCENARIOS = {
    "hinge_plain": ("LIM_HINGE", HINGE_TREE, None, [("open",), ("next",)] + FULL),
    "hinge_plain_again": ("LIM_HINGE", HINGE_TREE, None, [("open",), ("next",)] + FULL),
    "hinge_at_origin": ("LIM_ORIGIN", HINGE_TREE, None, [("open",), ("next",)] + FULL),
    "hinge_browse": ("LIM_HINGE", HINGE_TREE, None, [
        ("open",), ("joint", "arm_joint"), ("dump", "rad1"), UNIT_DEG, ("dump", "deg1"), UNIT_RAD, ("dump", "rad2"),
        UNIT_DEG, ("dump", "deg2"), ("next",), ("link", "arm"), ("dump", "link_deg"), UNIT_RAD, ("dump", "link_rad"),
        UNIT_DEG, ("link", "base_link"), ("link", "arm"), ("previous",), ("joint", "arm_joint"), ("dump", "deg3"),
        UNIT_RAD, ("dump", "rad3"), ("next",), ("link", "arm")] + FULL),
    "hinge_left_in_deg": ("LIM_HINGE", HINGE_TREE, "angle_unit", [
        ("open",), ("joint", "arm_joint"), UNIT_DEG, ("next",), ("link", "arm")] + FULL),
    "hinge_edit_deg": ("LIM_HINGE", HINGE_TREE, "angle_unit", [
        ("open",), ("joint", "arm_joint"), UNIT_DEG, ("type", "textBoxLimitLower", "-45.5"),
        ("type", "textBoxLimitUpper", "1.2345"), ("type", "textBoxLimitVelocity", "180"),
        ("type", "textBoxJointYaw", "12.5"), ("dump", "typed"), UNIT_RAD, UNIT_DEG, ("dump", "switched"),
        ("next",), ("link", "arm"), ("previous",), ("joint", "arm_joint"), ("dump", "back"), ("next",)] + FULL),
    "hinge_edit_rad": ("LIM_HINGE", HINGE_TREE, None, [
        ("open",), ("joint", "arm_joint"), ("type", "textBoxLimitLower", "-0.7"), ("type", "textBoxLimitUpper", "0.3"),
        ("dump", "typed"), ("next",), ("previous",), ("joint", "arm_joint"), ("dump", "back"), ("next",)] + FULL),
    "hinge_xacro": ("LIM_HINGE", HINGE_TREE, None, [("open",), ("next",), ("check", "checkBoxXacro", "on")] + FULL),
    "hinge_model_only": ("LIM_HINGE", HINGE_TREE, None, [("open",), ("next",), ("export", "model", PACKAGE)]),
    "slide_plain": ("LIM_SLIDE", HINGE_TREE, None, [("open",), ("next",)] + FULL),
    "slide_left_in_deg": ("LIM_SLIDE", HINGE_TREE, "angle_unit", [
        ("open",), ("joint", "arm_joint"), ("dump", "rad"), UNIT_DEG, ("dump", "deg"), ("next",), ("link", "arm")] + FULL),
    "slide_no_limit": ("LIM_FREE", HINGE_TREE, None, [
        ("open",), ("joint", "arm_joint"), ("dump", "page"), ("type", "textBoxLimitLower", "-0.02"), ("type", "textBoxLimitUpper", "0.03"),
        ("next",)] + FULL),
    "two_bases": ("FORM_MULTI", TWO_BASES_TREE, None, [("open",), ("next",)] + FULL),
    "multi_stl": ("FORM_MULTI", MULTI_TREE, None, [("open",), ("next",), ("radio", "radioButtonStl")] + FULL),
    "multi_obj": ("FORM_MULTI", MULTI_TREE, None, [("open",), ("next",), ("radio", "radioButtonObj")] + FULL),
    "multi_joints": ("FORM_MULTI", MULTI_TREE, None, [
        ("open",), ("joint", "arm_joint"), ("dump", "arm1"), ("joint", "slide_joint"), ("dump", "slide1"),
        ("joint", "block_joint"), ("dump", "block1"), ("joint", "arm_joint"), ("type", "textBoxLimitEffort", "7.5"),
        ("type", "textBoxLimitUpper", "0.25"), ("dump", "arm_typed"), ("joint", "slide_joint"), ("dump", "slide2"), UNIT_DEG,
        ("joint", "block_joint"), UNIT_RAD, ("dump", "block2"), ("joint", "arm_joint"), ("dump", "arm2"), ("next",)] + FULL),
    "unit_remembered": ("LIM_HINGE", HINGE_TREE, "angle_unit", [
        ("open",), UNIT_DEG, ("close",), ("open",), ("joint", "arm_joint"), ("dump", "deg"), UNIT_RAD, ("close",),
        ("open",), ("joint", "arm_joint"), ("dump", "rad"), ("close",)]),
}


def all_scenarios():
    """The scenarios of this file and those of form_e2e_joints.py."""
    import form_e2e_joints
    return dict(SCENARIOS, **form_e2e_joints.SCENARIOS)


# ---------------------------------------------------------------------------------------- running them

class AngleSetting:
    """The unit the user chose in the forms: saved, changed by the run, put back."""

    def __init__(self):
        self.had_key, self.value = self.read()

    @staticmethod
    def read():
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, KEY) as key:
                try:
                    return True, winreg.QueryValueEx(key, VALUE)
                except FileNotFoundError:
                    return True, None
        except FileNotFoundError:
            return False, None

    @staticmethod
    def clear():
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, KEY, 0, winreg.KEY_SET_VALUE) as key:
                winreg.DeleteValue(key, VALUE)
        except FileNotFoundError:
            pass

    def restore(self):
        import winreg
        if self.value is not None:
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER, KEY) as key:
                winreg.SetValueEx(key, VALUE, 0, self.value[1], self.value[0])
            return
        self.clear()
        if not self.had_key:
            try:
                winreg.DeleteKey(winreg.HKEY_CURRENT_USER, KEY)   # only when empty
            except OSError:
                pass


def free_memory_gb():
    """Memory Windows can still hand out (commit limit minus commit charge)."""
    import ctypes

    class Status(ctypes.Structure):
        _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [(n, ctypes.c_ulonglong) for n in (
            "total", "free", "total_commit", "free_commit", "total_virtual", "free_virtual", "extended")]
    status = Status(length=ctypes.sizeof(Status))
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        raise ctypes.WinError()
    return status.free_commit / 2.0 ** 30


def enough_memory(args):
    """SolidWorks grows with every model it opens; other programs with unsaved work must not run out of memory."""
    free = free_memory_gb()
    if free < args.min_free_gb:
        raise RuntimeError("only %.1f GB of memory left (--min-free-gb %.1f): stopped" % (free, args.min_free_gb))


class FormRun(obj.Live):
    """The host program of the form check, started on a desktop of its own."""

    def __init__(self, args):
        super().__init__(args, None)
        self.desktop = None
        self.desktop_name = "sw2urdf_form_check_%d" % os.getpid()

    def host(self, mode, out, *arguments, timeout=1800):
        """Runs the host; returns its exit code (or 'timeout')."""
        import win32con
        import win32event
        import win32process
        import win32service
        if self.desktop is None:
            self.desktop = win32service.CreateDesktop(self.desktop_name, 0, win32con.MAXIMUM_ALLOWED, None)
        os.makedirs(out, exist_ok=True)
        startup = win32process.STARTUPINFO()
        startup.lpDesktop = self.desktop_name
        command = subprocess.list2cmdline([self.exe, mode, "--desktop", self.desktop_name] + [str(a) for a in arguments] + [out])
        # the exporter writes its log under the user profile named by these two variables
        environment = dict(os.environ)
        drive, tail = os.path.splitdrive(os.path.join(self.work, "home"))
        os.makedirs(drive + tail, exist_ok=True)
        environment.update(HOMEDRIVE=drive, HOMEPATH=tail)
        process, thread, _pid, _tid = win32process.CreateProcess(
            self.exe, command, None, None, False, win32con.CREATE_NO_WINDOW, environment, os.path.dirname(self.exe), startup)
        self.process = process
        try:
            if win32event.WaitForSingleObject(process, timeout * 1000) == win32event.WAIT_TIMEOUT:
                win32process.TerminateProcess(process, 3)
                return "timeout"
            return win32process.GetExitCodeProcess(process)
        finally:
            self.process = None
            thread.Close()

    def handover(self, app):
        """The SolidWorks interface in a file, for one host process (which cannot see the Running Object Table)."""
        import pythoncom
        stream = pythoncom.CreateStreamOnHGlobal()
        pythoncom.CoMarshalInterface(stream, pythoncom.IID_IUnknown, app._oleobj_.QueryInterface(pythoncom.IID_IUnknown),
                                     pythoncom.MSHCTX_LOCAL, pythoncom.MSHLFLAGS_NORMAL)
        size = stream.Seek(0, 1)
        stream.Seek(0, 0)
        path = os.path.join(self.work, "app.bin")
        with open(path, "wb") as file:
            file.write(stream.Read(size))
        return path

    def kill_own(self):
        process = getattr(self, "process", None)
        if process is not None:
            import win32process
            try:
                win32process.TerminateProcess(process, 3)
            except Exception:  # noqa: BLE001
                pass
        super().kill_own()


def run(args):
    import pythoncom
    import make_form_fixtures
    import make_test_fixtures as fixtures
    pythoncom.CoInitialize()
    enough_memory(args)
    out = obj.long_path(os.path.abspath(args.out))
    if os.path.isdir(out) and os.listdir(out):
        raise SystemExit("--out must be a new or empty folder")
    os.makedirs(out, exist_ok=True)
    live = FormRun(args)
    live.work = obj.long_path(tempfile.mkdtemp(prefix="sw2urdf_form_e2e_"))
    setting = AngleSetting()
    with open(os.path.join(out, "angle_unit_before.json"), "w", encoding="utf-8") as stream:
        json.dump({"key": KEY, "had_key": setting.had_key, "value": setting.value}, stream)

    def stop():
        print("FAIL  overall limit of %d s reached; stopping" % args.timeout, flush=True)
        live.kill_own()
        setting.restore()
        os._exit(3)
    deadline = threading.Timer(args.timeout, stop)
    deadline.daemon = True
    deadline.start()
    try:
        live.exe = live.compile_probe("FormE2eProbe", ("LimitLiveProbe", "ObjRegressProbe"))
        code = live.host("features", os.path.join(out, "features"), timeout=60)
        features = obj.tsv(os.path.join(out, "features", "probe.tsv"))
        if code != 0 or features.get("status") != "ok":
            raise RuntimeError("the host does not start: %s %s" % (code, features.get("error")))
        if args.pid:
            live.pid = args.pid
        else:
            live.pid = live.started = live.launch()
        app = fixtures.bind(live.pid)
        if live.started:
            app.Visible = False
        if app.GetDocuments:
            raise RuntimeError("the SolidWorks instance holds documents; use an empty one")
        models = os.path.join(out, "models")
        make_form_fixtures.build(app, models)
        code = live.host("models", os.path.join(out, "measured"), live.pid, live.handover(app), models)
        measured = obj.tsv(os.path.join(out, "measured", "probe.tsv"))
        if code != 0 or measured.get("status") != "ok":
            raise RuntimeError("the models could not be built: %s %s" % (code, measured.get("error")))
        import form_e2e_joints
        scenarios = all_scenarios()
        wanted = args.scenario or list(scenarios)
        for name in wanted:
            model, tree, needs, steps = scenarios[name]
            folder = os.path.join(out, name)
            if needs and features.get(needs) != "True":
                print("SKIP  scenario %s: this build has no %s" % (name, needs), flush=True)
                continue
            enough_memory(args)
            scratch = os.path.join(live.work, "models", name)
            os.makedirs(scratch)
            for file in os.listdir(models):
                if file.upper().endswith((".SLDPRT", ".SLDASM")) and not file.startswith("~"):
                    shutil.copy(os.path.join(models, file), scratch)
            os.makedirs(folder)
            if features.get("angle_unit") == "True":
                setting.clear()      # a user who never chose a unit
            used = [s for s in steps if s[0] != "unit" or features.get("angle_unit") == "True"]
            for file, lines in (("tree.txt", tree), ("steps.txt", used)):
                with open(os.path.join(folder, file), "w", encoding="utf-8") as stream:
                    stream.write("".join("\t".join(line) + "\n" for line in lines))
            started = time.time()
            code = live.host("run", folder, live.pid, live.handover(app), os.path.join(scratch, form_e2e_joints.model_file(model)),
                             os.path.join(folder, "tree.txt"), os.path.join(folder, "steps.txt"), timeout=args.case_timeout)
            values = obj.tsv(os.path.join(folder, "probe.tsv"))
            with open(os.path.join(folder, "registry.txt"), "w", encoding="utf-8") as stream:
                stored = AngleSetting.read()[1]
                stream.write(stored[0] if stored else "")
            print("%-5s scenario %s (%.0f s) %s" % ("ran" if code == 0 else "ERROR", name, time.time() - started,
                                                    values.get("error", "")[:300]), flush=True)
            app.CloseAllDocuments(True)
    finally:
        deadline.cancel()
        setting.restore()
        if live.started:
            live.exit_instance()
        if args.keep:
            print("Scratch folder kept: " + live.work)
        else:
            shutil.rmtree(live.work, ignore_errors=True)
    if AngleSetting.read() != (setting.had_key, setting.value):
        raise RuntimeError("the angle unit setting was not put back")
    return out


# ------------------------------------------------------------------------------------ small geometry

def vec(text):
    return [float(x) for x in text.split()]


def frame(text):
    """'origin, image of x, y, z' as (R, p) with R[row][column]."""
    v = vec(text)
    return [[v[3 + 3 * c + r] for c in range(3)] for r in range(3)], v[:3]


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def apply(m, v):
    return [sum(m[i][k] * v[k] for k in range(3)) for i in range(3)]


def transpose(m):
    return [[m[j][i] for j in range(3)] for i in range(3)]


def compose(a, b):
    return mul(a[0], b[0]), [x + y for x, y in zip(apply(a[0], b[1]), a[1])]


def rpy_of(m):
    """Roll, pitch, yaw (rad) of a rotation matrix, for a pitch that is not 90 degrees."""
    return [math.atan2(m[2][1], m[2][2]), math.asin(max(-1.0, min(1.0, -m[2][0]))), math.atan2(m[1][0], m[0][0])]


def worst(a, b):
    return max(abs(x - y) for x, y in zip(a, b))


def g15(value):
    """The text .NET writes for a double without a format (15 significant digits): the URDF number format."""
    return ("%.15g" % value).replace("e", "E")


def g12(value):
    return ("%.12g" % value).replace("e", "E")


def digits(text):
    """Number of significant digits of a number text."""
    return len(re.sub(r"[eE].*$", "", text).lstrip("+-").replace(".", "").lstrip("0"))


class Robot:
    """The written URDF, read without the exporter."""

    def __init__(self, path):
        root = ET.parse(path).getroot()
        self.name = root.get("name")
        self.joints = {j.get("name"): j for j in root.findall("joint")}
        self.links = {l.get("name"): l for l in root.findall("link")}

    @staticmethod
    def origin(element):
        o = element.find("origin") if element is not None else None
        if o is None:
            return [[1, 0, 0], [0, 1, 0], [0, 0, 1]], [0, 0, 0]
        return obj.rotation(vec(o.get("rpy") or "0 0 0")), vec(o.get("xyz") or "0 0 0")

    def limit(self, joint, name):
        element = self.joints[joint].find("limit")
        return None if element is None else element.get(name)

    def axis(self, joint):
        return vec(self.joints[joint].find("axis").get("xyz"))

    def place(self, link, base):
        """Frame of a link in the world, from the joints of the URDF alone (base: the base link in the world)."""
        for joint in self.joints.values():
            if joint.find("child").get("link") == link:
                return compose(self.place(joint.find("parent").get("link"), base), self.origin(joint))
        return base

    def mesh(self, link, kind):
        element = self.links[link].find(kind)
        return None if element is None else (element.find("geometry/mesh").get("filename"), self.origin(element))


# ------------------------------------------------------------------------------------------- checks

class Check:
    def __init__(self, build):
        self.build = build
        self.rows = []

    def add(self, ident, title, expected, actual, result, proof=""):
        actual = re.sub(r"[A-Za-z]:[\\/][^ |)\]]*", "<path>", str(actual))    # no local paths in the result
        self.rows.append({"id": ident, "check": title, "expected": str(expected), "actual": actual, "result": result, "proof": proof})

    def equal(self, ident, title, expected, actual, proof):
        self.add(ident, title, expected, actual, "PASS" if expected == actual else "FAIL", proof)

    def near(self, ident, title, expected, actual, tolerance, proof):
        """Lists of numbers within a tolerance."""
        difference = worst(expected, actual) if len(expected) == len(actual) else float("inf")
        self.add(ident, title, " ".join(g12(x) for x in expected), "%s (largest difference %.2g, limit %.2g)" % (
            " ".join(g12(x) for x in actual), difference, tolerance), "PASS" if difference <= tolerance else "FAIL", proof)

    def failed(self):
        return [r for r in self.rows if r["result"] == "FAIL"]


class Case:
    """The recorded files of one scenario."""

    def __init__(self, out, name):
        self.name = name
        self.folder = os.path.join(out, name)
        self.values = obj.tsv(os.path.join(self.folder, "probe.tsv"))
        self.package = os.path.join(self.folder, "export", PACKAGE)
        self.proof = name + "/probe.tsv"

    @property
    def ran(self):
        return bool(self.values)

    def box(self, label, control):
        return self.values.get("dump.%s.%s" % (label, control))

    def boxes(self, label):
        """Everything on display: name -> text (no unit choosers)."""
        prefix = "dump.%s." % label
        found = {k[len(prefix):]: v for k, v in self.values.items() if k.startswith(prefix) and "comboBoxAngleUnit" not in k}
        if not found:
            raise RuntimeError("the page was not recorded: " + label)
        return found

    def files(self, sub):
        folder = os.path.join(self.package, sub)
        return sorted(os.listdir(folder)) if os.path.isdir(folder) else []

    def robot_file(self):
        found = [f for f in self.files("urdf") if not f.lower().endswith(".csv")]
        return os.path.join(self.package, "urdf", found[0]) if len(found) == 1 else None

    def robot(self):
        path = self.robot_file()
        if path is None:
            raise RuntimeError("no robot file written")
        return Robot(path)

    def urdf_proof(self):
        path = self.robot_file()
        return self.name + "/export/" + PACKAGE + "/urdf/" + (os.path.basename(path) if path else "(none)")

    def text(self, name):
        with open(os.path.join(self.package, *name.split("/")), encoding="utf-8-sig") as stream:
            return stream.read()


def same_numbers(a, b):
    """Two texts are the same, numbers within the last-digit noise SolidWorks has between two opened copies of a model
    (2e-15 relative, or 1e-18). Rounding to 12 digits or a conversion through degrees text is far outside of that."""
    x, y = obj.NUMBER.split(a), obj.NUMBER.split(b)
    if len(x) != len(y):
        return False
    for index, (p, q) in enumerate(zip(x, y)):
        if p == q:
            continue
        if index % 2 == 0:
            return False
        u, v = float(p), float(q)
        if abs(u - v) > 1e-18 + 2e-15 * max(abs(u), abs(v)):
            return False
    return True


def joint_texts(case):
    """The joint elements of the written file as text: these must be equal to the last digit."""
    root = ET.parse(case.robot_file()).getroot()
    return [ET.tostring(j, encoding="unicode").strip() for j in root.findall("joint")]


def differing_files(a, b):
    """Files of two packages that differ: robot file and CSV see same_numbers, all others byte for byte (log excluded)."""
    result = []
    if joint_texts(a) != joint_texts(b):
        result.append("joint elements")
    for name in sorted(set(obj.package_files(a.package)) | set(obj.package_files(b.package))):
        if name.lower().endswith((".log", ".3dxml")):
            continue
        x, y = (os.path.join(c.package, *name.split("/")) for c in (a, b))
        if not os.path.isfile(x) or not os.path.isfile(y):
            result.append(name + " (missing)")
        elif name.startswith("urdf/"):
            if not same_numbers(a.text(name), b.text(name)):
                result.append(name)
        elif obj.sha256(x) != obj.sha256(y):
            result.append(name)
    return result


# ids of the checks, in the order of the table: id -> (title, needs)
def evaluate(out):
    features = obj.tsv(os.path.join(out, "features", "probe.tsv"))
    measured = obj.tsv(os.path.join(out, "measured", "probe.tsv"))
    check = Check(features.get("version", "?"))
    cases = {name: Case(out, name) for name in all_scenarios()}

    def guard(ident, title, function, needs=None, proof=""):
        if needs and features.get(needs) != "True":
            check.add(ident, title, "", "this build has no " + needs, "SKIPPED")
            return
        count = len(check.rows)
        try:
            function()
        except Exception as error:  # noqa: BLE001
            del check.rows[count:]
            check.add(ident, title, "", "%s: %s" % (type(error).__name__, error), "FAIL", proof)

    # ---- every scenario came to its end and nothing asked the user
    for name, (_model, _tree, needs, steps) in SCENARIOS.items():
        case = cases[name]

        def ran(case=case, steps=steps):
            dialogs = [v for k, v in case.values.items() if k.startswith("dialog.message") or k.startswith("dialog.stuck")]
            exports = sum(1 for s in steps if s[0] == "export")
            saved = case.values.get("dialog.save") is not None if exports else True
            closed = case.values.get("export.%s.closed" % PACKAGE, "True") == "True"
            actual = "status %s, %d unexpected dialogs%s%s" % (case.values.get("status"), len(dialogs), "" if saved else ", no Save As dialog",
                                                              "" if closed else ", form still open")
            if dialogs or case.values.get("status") != "ok":
                actual += ": " + (" / ".join(dialogs) + " " + case.values.get("error", ""))[:300]
            check.equal("run." + case.name, "scenario %s runs to its end and no dialog asks the user" % case.name,
                        "status ok, 0 unexpected dialogs", actual, case.proof)
        guard("run." + name, "scenario %s" % name, ran, needs)

    # ---- hinge: the limits
    lower, upper = -90, 30      # the typed values 60 and 180 against the value 90; hinge.motion checks the direction
    expected_text = (g15(math.radians(lower)), g15(math.radians(upper)))

    def limits(ident, title, name, expected, joint="arm_joint"):
        def test():
            actual = "no robot file written"
            if cases[name].robot_file():
                robot = cases[name].robot()
                actual = "lower %s upper %s" % (robot.limit(joint, "lower"), robot.limit(joint, "upper"))
            check.equal(ident, title, "lower %s upper %s" % expected, actual, cases[name].urdf_proof())
        return test

    def motion():
        check.equal("hinge.motion", "hinge: the two limits are the measured motion of the arm, in the direction of the written axis",
                    "-90 30", "%g %g" % tuple(hinge_limits(measured, cases["hinge_plain"])), "measured/probe.tsv")
    guard("hinge.motion", "hinge: measured motion", motion)
    title = "hinge 60..180 at 90 deg: exact limit text in the URDF, form not touched"
    guard("hinge.limits", title, limits("hinge.limits", title, "hinge_plain", expected_text))
    title = "hinge whose arm lies on the assembly origin, not turned: exact limit text in the URDF"
    guard("origin.limits", title, limits("origin.limits", title, "hinge_at_origin", expected_text))
    title = "hinge: exact limit text in the URDF, form left in deg"
    guard("hinge.limits.deg", title, limits("hinge.limits.deg", title, "hinge_left_in_deg", expected_text), "angle_unit")

    browse = cases["hinge_browse"]

    def page_deg():
        check.equal("hinge.page.deg", "hinge: joint page in deg shows lower, upper, R and the velocity limit of 1 rad/s",
                    "-90 | 30 | 90 | 57.2957795131", " | ".join(browse.box("deg1", b) or "" for b in (
                        "textBoxLimitLower", "textBoxLimitUpper", "textBoxJointRoll", "textBoxLimitVelocity")), browse.proof)
    guard("hinge.page.deg", "hinge: joint page in deg", page_deg, "angle_unit")

    def page_rad():
        label = "rad1"
        texts = [browse.box(label, b) or "" for b in ("textBoxLimitLower", "textBoxLimitUpper")]
        if features.get("digits12") == "True":
            check.equal("hinge.page.rad", "hinge: joint page in rad shows the limits (12 digits)",
                        "%s | %s" % (g12(math.radians(lower)), g12(math.radians(upper))), " | ".join(texts), browse.proof)
        else:
            check.equal("hinge.page.rad", "hinge: joint page in rad shows the limits (to 15 digits)", "%s | %s" % expected_text,
                        " | ".join(g15(float(t)) for t in texts), browse.proof)
    guard("hinge.page.rad", "hinge: joint page in rad", page_rad)

    def page_roll():
        sw = cases["hinge_plain"].values
        base, arm = frame(sw["sw.Origin_global"]), frame(sw["sw.arm.frame"])
        expected = rpy_of(mul(transpose(base[0]), arm[0]))
        shown = [float(browse.box("rad1", b)) for b in ("textBoxJointRoll", "textBoxJointPitch", "textBoxJointYaw")]
        check.near("hinge.page.rpy", "hinge: R, P, Y on the joint page are the arm frame in the base frame (SolidWorks)", expected, shown, 1e-9, browse.proof)
    guard("hinge.page.rpy", "hinge: R, P, Y on the joint page", page_roll)

    def same_pages():
        changed = []
        for first, others in (("rad1", ("rad2", "rad3")), ("deg1", ("deg2", "deg3"))):
            for other in others:
                a, b = browse.boxes(first), browse.boxes(other)
                # rad3/deg3 come after the link page, whose boxes are filled by then: compare the joint page only
                names = [n for n in a if n in JOINT_PAGE]
                changed += ["%s %s: %r -> %r" % (other, n, a[n], b.get(n)) for n in names if a[n] != b.get(n)]
        check.equal("hinge.page.same", "hinge: every joint box shows the same text after switching rad/deg and changing pages", "no change",
                    "; ".join(changed) or "no change", browse.proof)
    guard("hinge.page.same", "hinge: joint boxes after switching", same_pages, "angle_unit")

    # ---- untouched = untouched again = switched and browsed
    def same_package(ident, title, name):
        def test():
            case, plain = cases[name], cases["hinge_plain"]
            if not obj.package_files(plain.package) or not obj.package_files(case.package):
                raise RuntimeError("no package")
            check.equal(ident, title, "no file differs", ", ".join(differing_files(plain, case)) or "no file differs",
                        "hinge_plain/export, %s/export" % name)
        return test
    same = "the package is the same (joints and other files exactly, masses within SolidWorks' last-digit noise)"
    title = "hinge: two exports with the form not touched: " + same
    guard("hinge.repeat", title, same_package("hinge.repeat", title, "hinge_plain_again"))
    title = "hinge: joint and link pages visited, forwards and backwards%s, no edit: %s"
    title = title % (", unit switched several times" if features.get("angle_unit") == "True" else " (no unit to switch on this build)", same)
    guard("hinge.browse", title, same_package("hinge.browse", title, "hinge_browse"))
    title = "hinge: form left in deg, no edit: " + same
    guard("hinge.deg", title, same_package("hinge.deg", title, "hinge_left_in_deg"), "angle_unit")

    # ---- edits
    edit = cases["hinge_edit_deg"]

    def edited_deg():
        robot = edit.robot()
        check.equal("edit.deg.urdf", "edit in deg (-45.5, 1.2345, 180 deg/s): exact radian text in the URDF",
                    " | ".join(g15(math.radians(x)) for x in (-45.5, 1.2345, 180)),
                    " | ".join(robot.limit("arm_joint", n) or "" for n in ("lower", "upper", "velocity")), edit.urdf_proof())
    guard("edit.deg.urdf", "edit in deg: URDF", edited_deg, "angle_unit")

    def edited_yaw():
        check.equal("edit.deg.rpy", "edit in deg: yaw of 12.5 deg of the joint origin, exact radian text in the URDF", g15(math.radians(12.5)),
                    edit.robot().joints["arm_joint"].find("origin").get("rpy").split()[2], edit.urdf_proof())
    guard("edit.deg.rpy", "edit in deg: yaw", edited_yaw, "angle_unit")

    def edited_page():
        for label, when in (("typed", "after typing"), ("switched", "after deg-rad-deg"), ("back", "after the link page and back")):
            check.equal("edit.deg.page." + label, "edit in deg: the page shows what was typed, " + when, "-45.5 | 1.2345 | 180 | 12.5", " | ".join(
                edit.box(label, b) or "" for b in ("textBoxLimitLower", "textBoxLimitUpper", "textBoxLimitVelocity", "textBoxJointYaw")), edit.proof)
    guard("edit.deg.page.typed", "edit in deg: page", edited_page, "angle_unit")

    def edited_rad():
        case = cases["hinge_edit_rad"]
        robot = case.robot()
        check.equal("edit.rad", "edit in rad (-0.7, 0.3): text in the URDF, and on the page after the link page and back", "-0.7 | 0.3 | -0.7 | 0.3",
                    " | ".join([robot.limit("arm_joint", "lower") or "", robot.limit("arm_joint", "upper") or "",
                                case.box("back", "textBoxLimitLower") or "", case.box("back", "textBoxLimitUpper") or ""]), case.urdf_proof())
    guard("edit.rad", "edit in rad", edited_rad)

    # ---- slide
    slide_expected = (g15(-10 / 1000), g15(20 / 1000))    # 0 and 30 mm against the value 10 mm

    def slide_motion():
        check.equal("slide.motion", "slide: the two limits are the measured motion of the arm, in the direction of the written axis",
                    "-10 20", "%g %g" % tuple(slide_limits(measured, cases["slide_plain"])), "measured/probe.tsv")
    guard("slide.motion", "slide: measured motion", slide_motion)
    title = "slide 0..30 at 10 mm: exact limit text in the URDF (m), form not touched"
    guard("slide.limits", title, limits("slide.limits", title, "slide_plain", slide_expected))
    title = "slide: exact limit text in the URDF (m), form left in deg"
    guard("slide.limits.deg", title, limits("slide.limits.deg", title, "slide_left_in_deg", slide_expected), "angle_unit")
    slide = cases["slide_left_in_deg"]

    def slide_page():
        got = [slide.box(l, b) or "" for l in ("rad", "deg") for b in ("textBoxLimitLower", "textBoxLimitUpper")]
        check.equal("slide.page", "slide: limit boxes in m before and after switching to deg", "%s | %s | %s | %s" % (slide_expected * 2), " | ".join(got), slide.proof)
    guard("slide.page", "slide: limit boxes", slide_page, "angle_unit")

    def slide_caption():
        texts = [slide.box(l, b) or "" for l in ("rad", "deg") for b in ("labelLowerLimit", "labelLimitUpper")]
        ok = all("(m)" in t for t in texts)
        check.equal("slide.caption", "slide: the limit captions name m in both units", "(m) in 4 of 4", "(m) in %d of 4: %s" % (sum("(m)" in t for t in texts), texts)
                    if not ok else "(m) in 4 of 4", slide.proof)
    guard("slide.caption", "slide: captions", slide_caption, "angle_unit")

    def slide_converted():
        a, b = slide.boxes("rad"), slide.boxes("deg")
        changed = {n for n in a if n.startswith("textBox") and a[n] != b.get(n)}
        turned = {n for n in ("textBoxJointRoll", "textBoxJointPitch", "textBoxJointYaw") if float(a[n]) != 0}
        check.equal("slide.converted", "slide: switching to deg changes the number boxes of roll, pitch and yaw (those that are not 0) and no other",
                    ", ".join(sorted(turned)), ", ".join(sorted(changed)), slide.proof)
        # the base frame has its z along Y of SolidWorks, the arm frame along Z: a roll of 90 degrees by construction
        check.equal("slide.converted.roll", "slide: roll in deg, exact text", "90", b["textBoxJointRoll"], slide.proof)
    guard("slide.converted", "slide: converted boxes", slide_converted, "angle_unit")

    def no_limit_page():
        case = cases["slide_no_limit"]
        check.equal("free.page", "slide without a limit mate: the joint page opens, is filled to its last box, and the limit boxes are blank",
                    "arm_joint, coordinate system chosen: yes, limits '' ''", "%s, coordinate system chosen: %s, limits %r %r" % (
                        case.box("page", "textBoxJointName"), "yes" if case.box("page", "comboBoxOrigin") else "no", case.box("page", "textBoxLimitLower"), case.box("page", "textBoxLimitUpper")), case.proof)
    guard("free.page", "slide without a limit mate: page", no_limit_page)
    title = "slide without a limit mate: typed limits reach the URDF"
    guard("free.limits", title, limits("free.limits", title, "slide_no_limit", ("-0.02", "0.03")))
    title = "limit mate with the second component of the parent link: limits in the URDF"
    guard("two_bases.limits", title, limits("two_bases.limits", title, "two_bases", slide_expected, "slide_joint"))

    # ---- several joints
    joints = cases["multi_joints"]

    def kept():
        changed = []
        for first, second in (("slide1", "slide2"), ("block1", "block2"), ("arm_typed", "arm2")):
            a, b = joints.boxes(first), joints.boxes(second)
            changed += ["%s %s: %r -> %r" % (second, n, a[n], b.get(n)) for n in a if n in JOINT_PAGE and a[n] != b.get(n)]
        check.equal("joints.kept", "three joints: all boxes of a joint show the same after other joints were visited and one was edited", "no change",
                    "; ".join(changed) or "no change", joints.proof)
    guard("joints.kept", "three joints: boxes", kept)

    def distinct():
        names = [joints.box(l, "textBoxJointName") for l in ("arm1", "slide1", "block1")]
        check.equal("joints.shown", "three joints: each page shows its own joint", ["arm_joint", "slide_joint", "block_joint"], names, joints.proof)
    guard("joints.shown", "three joints: names", distinct)

    def only_edited():
        robot, plain = joints.robot(), cases["multi_stl"].robot()
        changed = []
        for name in plain.joints:
            for element in ("origin", "axis", "limit", "parent", "child"):
                a, b = plain.joints[name].find(element), robot.joints[name].find(element)
                x, y = ({} if e is None else dict(e.attrib) for e in (a, b))
                changed += ["%s %s %s" % (name, element, k) for k in sorted(set(x) | set(y)) if x.get(k) != y.get(k)]
        check.equal("joints.urdf", "three joints: an edit of effort and upper limit of one joint changes exactly these two values in the URDF",
                    "arm_joint limit effort, arm_joint limit upper", ", ".join(changed), joints.urdf_proof())
        check.equal("joints.urdf.values", "three joints: the edited values in the URDF", "effort 7.5 upper 0.25",
                    "effort %s upper %s" % (robot.limit("arm_joint", "effort"), robot.limit("arm_joint", "upper")), joints.urdf_proof())
    guard("joints.urdf", "three joints: URDF", only_edited)

    # ---- the unit is remembered
    remembered = cases["unit_remembered"]

    def unit_kept():
        for label, index, text in (("deg", "1", "-90"), ("rad", "0", None)):
            shown = remembered.box(label, "textBoxLimitLower")
            expected = "unit box %s, lower %s" % (index, text or (g12 if features.get("digits12") == "True" else g15)(math.radians(lower)))
            if text is None and features.get("digits12") != "True":
                shown = g15(float(shown))
            check.equal("unit.kept." + label, "the unit chosen in one form (%s) is the unit of the next form" % label, expected, "unit box %s, lower %s" % (
                remembered.box(label, "comboBoxAngleUnitJoint.index"), shown), remembered.proof)
    guard("unit.kept.deg", "the unit is remembered", unit_kept, "angle_unit")

    # ---- xacro
    xacro = cases["hinge_xacro"]

    def xacro_file():
        robots = [f for f in xacro.files("urdf") if not f.lower().endswith(".csv")]
        check.equal("xacro.file", "xacro: exactly one robot file is written, not a plain .urdf", "1 file, plain .urdf: no",
                    "%d file, plain .urdf: %s" % (len(robots), "yes" if any(f.lower().endswith(".urdf") for f in robots) else "no"), "hinge_xacro/export")
    guard("xacro.file", "xacro: file", xacro_file)

    def xacro_name():
        if features.get("xacro_extension") != ".xacro":
            check.add("xacro.name", "xacro: the file is named <robot>.xacro", "", "this build names it <robot>%s" % features.get("xacro_extension"), "SKIPPED")
            return
        check.equal("xacro.name", "xacro: the file is named <robot>.xacro", [PACKAGE + ".xacro"], [f for f in xacro.files("urdf") if not f.lower().endswith(".csv")],
                    "hinge_xacro/export")
    guard("xacro.name", "xacro: file name", xacro_name)

    def xacro_content():
        path, plain = xacro.robot_file(), cases["hinge_plain"].robot_file()
        a, b = (open(p, encoding="utf-8-sig").read() for p in (plain, path))
        declaration = re.compile(r'\s+xmlns:xacro="http://www\.ros\.org/wiki/xacro"')
        found = len(declaration.findall(b))
        same = same_numbers(re.sub(r"\s+", " ", a), re.sub(r"\s+", " ", declaration.sub("", b)))
        check.equal("xacro.content", "xacro: the text equals the plain URDF with one xacro namespace declaration added", "1 declaration, rest equal",
                    "%d declaration, rest %s" % (found, "equal" if same else "different"), xacro.urdf_proof())
        differences, facts = xacro_check.compare_robots(xacro_check.parse_file(plain), xacro_check.parse_file(path))
        check.equal("xacro.equivalent", "xacro: same robot as the plain URDF (check_xacro_equivalence.py, file not expanded)", "0 differences",
                    "%d differences%s" % (len(differences), " " + str(differences[:3]) if differences else ""), xacro.urdf_proof())
    guard("xacro.content", "xacro: content", xacro_content)

    def xacro_expanded():
        command = xacro_check.find_xacro()
        if command is None:
            check.add("xacro.expanded", "xacro: the expansion by the xacro tool equals the plain URDF", "", "the xacro tool is not installed", "SKIPPED")
            return
        root = xacro_check.expand_xacro(command, xacro.robot_file())
        differences, _facts = xacro_check.compare_robots(xacro_check.parse_file(cases["hinge_plain"].robot_file()), root)
        check.equal("xacro.expanded", "xacro: the expansion by the xacro tool equals the plain URDF", "0 differences", "%d differences" % len(differences), xacro.urdf_proof())
    guard("xacro.expanded", "xacro: expansion", xacro_expanded)

    def xacro_launch():
        name = os.path.basename(xacro.robot_file())
        texts = {f: xacro.text("launch/" + f) for f in xacro.files("launch")}
        missing = [f for f, t in texts.items() if "/urdf/" + name not in t]
        check.equal("xacro.launch", "xacro: every launch file names the written file", "2 launch files, 0 without it", "%d launch files, %d without it" % (len(texts), len(missing)),
                    "hinge_xacro/export/%s/launch" % PACKAGE)
    guard("xacro.launch", "xacro: launch files", xacro_launch)

    # ---- model file only / full
    def model_only():
        case = cases["hinge_model_only"]
        check.equal("model.only", "Export Model File Only: robot file written, no mesh written", "robot file yes, meshes []",
                    "robot file %s, meshes %s" % ("yes" if case.robot_file() else "no", case.files("meshes")), "hinge_model_only/export")
    guard("model.only", "Export Model File Only", model_only)

    def model_same():
        a, b = (cases[n].text("urdf/%s.urdf" % PACKAGE) for n in ("hinge_plain", "hinge_model_only"))
        check.equal("model.same", "Export Model File Only: the URDF equals the one of the full export", "equal", "equal" if same_numbers(a, b) else "different", cases["hinge_model_only"].urdf_proof())
    guard("model.same", "Export Model File Only: URDF", model_same)

    def model_full():
        for name, wanted in (("multi_stl", [l[0] + ".STL" for l in MULTI_TREE]), ("multi_obj", [l[0] + e for l in MULTI_TREE for e in (".mtl", ".obj")])):
            got = [f for f in cases[name].files("meshes") if not f.endswith(".3dxml")]
            check.equal("model.full." + name, "full export (%s): one mesh per link and nothing else" % name, sorted(wanted), sorted(got), name + "/export")
    guard("model.full.multi_stl", "full export", model_full)

    # ---- the written URDF against SolidWorks
    for name in ("hinge_plain", "slide_plain", "multi_stl", "multi_obj"):
        case = cases[name]
        tree = SCENARIOS[name][1]
        for line in tree:
            guard("sw.%s.%s" % (name, line[0]), "%s: link %s against SolidWorks" % (name, line[0]), lambda c=case, l=line: against_solidworks(check, c, l))

    def turned():
        sw = cases["multi_stl"].values
        link, component = frame(sw["sw.block.frame"])[0], frame(sw["sw.block.component"])[0]
        between = mul(transpose(link), component)
        angle = math.degrees(math.acos(max(-1.0, min(1.0, (sum(between[i][i] for i in range(3)) - 1) / 2))))
        check.add("sw.turned", "the checks above cover a link whose frame is turned against its part (block)", "more than 10 deg", "%.1f deg" % angle,
                  "PASS" if angle > 10 else "FAIL", "multi_stl/probe.tsv")
    guard("sw.turned", "turned link", turned)

    for name, joint, key, kind in (("hinge_plain", "arm_joint", "hinge", "revolute"), ("slide_plain", "arm_joint", "slide", "prismatic"),
                                   ("multi_stl", "arm_joint", "multi.hinge", "revolute"), ("multi_stl", "slide_joint", "multi.slide", "prismatic")):
        guard("axis.%s.%s" % (name, joint), "joint axis", lambda n=name, j=joint, k=key, t=kind: axis_on_boss(check, cases[n], j, measured[k + ".axis"], t))

    def multi_limits():
        robot = cases["multi_stl"].robot()
        base = frame(cases["multi_stl"].values["sw.Origin_global"])
        arm = expected_limits(measured["multi.hinge.motion"], [60 - 90, 180 - 90], world_axis(robot, "arm_joint", base)[2])
        slider = expected_limits(measured["multi.slide.motion"], [0 - 10, 30 - 10], world_axis(robot, "slide_joint", base)[2])
        check.equal("multi.limits", "two limit mates in one assembly: exact limit texts of both joints",
                    " | ".join([g15(math.radians(x)) for x in arm] + [g15(x / 1000) for x in slider]),
                    " | ".join(robot.limit(j, n) or "" for j in ("arm_joint", "slide_joint") for n in ("lower", "upper")), cases["multi_stl"].urdf_proof())
    guard("multi.limits", "two limit mates", multi_limits)

    # ---- 12 digit boxes
    def twelve():
        too_long = []
        for case in (browse, joints):
            for key, value in case.values.items():
                if key.startswith("dump.") and (".textBox" in key or ".domainUpDown" in key) and re.match(r"^[-+]?[0-9.]+(E[-+]?\d+)?$", value) and digits(value) > 12:
                    too_long.append("%s %s=%s" % (case.name, key, value))
        check.equal("digits", "no number box shows more than 12 significant digits", "0 boxes", "%d boxes%s" % (len(too_long), " " + "; ".join(too_long[:4]) if too_long else ""), browse.proof)
    guard("digits", "12 digit boxes", twelve, "digits12")

    import form_e2e_joints
    form_e2e_joints.evaluate(check, cases, measured, guard)
    return check


JOINT_PAGE = {"textBoxJointName", "comboBoxAxis", "comboBoxOrigin", "comboBoxJointType", "textBoxAxisX", "textBoxAxisY", "textBoxAxisZ",
              "textBoxJointX", "textBoxJointY", "textBoxJointZ", "textBoxJointPitch", "textBoxJointRoll", "textBoxJointYaw",
              "textBoxLimitLower", "textBoxLimitUpper", "textBoxLimitEffort", "textBoxLimitVelocity", "textBoxDamping", "textBoxFriction",
              "textBoxCalibrationFalling", "textBoxCalibrationRising", "textBoxSoftLower", "textBoxSoftUpper", "textBoxKPosition",
              "textBoxKVelocity", "labelLowerLimit", "labelLimitUpper", "labelVelocity", "labelParent", "labelChild"}


def world_axis(robot, joint, base):
    child = robot.joints[joint].find("child").get("link")
    return apply(robot.place(child, base)[0], robot.axis(joint))


def expected_limits(motion, typed, axis_z):
    """Limits along the written axis, from the typed values (relative to the exported pose). The measured motion about
    world Z only says which way the typed values count; axis_z is the written axis along world Z (+1 / -1)."""
    lo, hi = vec(motion)
    options = [sorted(typed), sorted(-x for x in typed)]
    best = min(options, key=lambda o: worst(o, (lo, hi)))
    if worst(best, (lo, hi)) > 0.01:
        raise RuntimeError("measured motion %s does not fit the typed limits %s" % (motion, typed))
    return best if axis_z > 0 else sorted(-x for x in best)


def hinge_limits(measured, case, key="hinge"):
    return expected_limits(measured[key + ".motion"], [60 - 90, 180 - 90], world_axis(case.robot(), "arm_joint", frame(case.values["sw.Origin_global"]))[2])


def slide_limits(measured, case):
    return expected_limits(measured["slide.motion"], [0 - 10, 30 - 10], world_axis(case.robot(), "arm_joint", frame(case.values["sw.Origin_global"]))[2])


def against_solidworks(check, case, line):
    """One link of a written URDF: frame, mass, centre of mass, inertia and the place of both meshes, all in the world."""
    link, sw = line[0], case.values
    where = "sw.%s.%s." % (case.name, link)
    title = "%s, link %s: " % (case.name, link)
    robot = case.robot()
    base = frame(sw["sw.Origin_global"])
    placed = robot.place(link, base)
    target = frame(sw["sw.%s.frame" % link])
    proof = case.urdf_proof()
    if line[1] != "-":    # the base link is placed at Origin_global itself: nothing to compare
        check.near(where + "frame", title + "frame through the joints of the URDF = coordinate system in SolidWorks (origin m, axes)",
                   target[1] + [x for row in target[0] for x in row], placed[1] + [x for row in placed[0] for x in row], 1e-8, proof)
    inertial = robot.links[link].find("inertial")
    mass = float(inertial.find("mass").get("value"))
    check.near(where + "mass", title + "mass (kg)", [float(sw["sw.%s.mass" % link])], [mass], 1e-6 * float(sw["sw.%s.mass" % link]), proof)
    centre = compose(placed, Robot.origin(inertial))
    check.near(where + "com", title + "centre of mass in the world (m)", vec(sw["sw.%s.com" % link]), centre[1], 1e-8, proof)
    values = {k: float(inertial.find("inertia").get(k)) for k in ("ixx", "ixy", "ixz", "iyy", "iyz", "izz")}
    tensor = [[values["ixx"], values["ixy"], values["ixz"]], [values["ixy"], values["iyy"], values["iyz"]], [values["ixz"], values["iyz"], values["izz"]]]
    world = mul(mul(centre[0], tensor), transpose(centre[0]))
    reference = vec(sw["sw.%s.inertia" % link])
    check.near(where + "inertia", title + "inertia about the centre of mass, in the world (kg m2, 9 values)", reference, [x for row in world for x in row],
               1e-6 * max(abs(x) for x in reference), proof)
    vertices = vec(sw["sw.%s.vertices" % link])
    corners = [vertices[i:i + 3] for i in range(0, len(vertices), 3)]
    for kind in ("visual", "collision"):
        uri, origin = robot.mesh(link, kind)
        path = obj.resolve_mesh(case.package, uri)
        points = [transformed(compose(placed, origin), p) for p in mesh_points(path)]
        far = max(min(math.dist(c, p) for p in points) for c in corners)
        off = worst(obj.bbox(points), vec(sw["sw.%s.box" % link]))
        check.add(where + kind, title + "%s mesh placed by the URDF (%s) against the bodies in SolidWorks: corners and bounding box in the world" % (
            kind, os.path.basename(path)), "%d corners and the box within 1e-5 m" % len(corners), "farthest corner %.2g m, box off by %.2g m" % (far, off),
            "PASS" if corners and far <= 1e-5 and off <= 1e-5 else "FAIL", case.name + "/export/" + PACKAGE + "/meshes/" + os.path.basename(path))


def transformed(place, point):
    return [a + b for a, b in zip(apply(place[0], point), place[1])]


def mesh_points(path):
    """The different vertices (m) of a mesh file, read without the exporter."""
    if path.lower().endswith(".3dxml"):
        import form_e2e_joints
        data = form_e2e_joints.read_3dxml(path)
    else:
        data = obj.read_obj(path)["vertices"] if path.lower().endswith(".obj") else obj.read_stl(path)
    return set(tuple(p) for p in data)


def axis_on_boss(check, case, joint, boss, kind):
    """The written axis against the boss of the base: it is parallel to the boss, and goes through it (hinge)."""
    robot = case.robot()
    child = robot.joints[joint].find("child").get("link")
    placed = robot.place(child, frame(case.values["sw.Origin_global"]))
    axis = apply(placed[0], robot.axis(joint))
    line = vec(boss)
    offset = [a - b for a, b in zip(placed[1], line[:3])]
    along = sum(a * b for a, b in zip(offset, line[3:]))
    away = math.sqrt(max(0.0, sum(x * x for x in offset) - along * along))
    parallel = abs(sum(a * b for a, b in zip(axis, line[3:])))
    through = kind == "prismatic" or away <= 1e-7
    check.add("axis.%s.%s" % (case.name, joint), "%s, %s: type %s, axis parallel to the boss%s" % (case.name, joint, kind, "" if kind == "prismatic" else " and through its centre"),
              "%s, parallel 1, distance 0 m" % kind, "%s, parallel %.9f, distance %.2g m" % (robot.joints[joint].get("type"), parallel, away),
              "PASS" if robot.joints[joint].get("type") == kind and abs(parallel - 1) < 1e-9 and through else "FAIL", case.urdf_proof())


# ------------------------------------------------------------------------------------------ the report

def report(check, out, quiet=False):
    counts = {r: sum(1 for x in check.rows if x["result"] == r) for r in ("PASS", "FAIL", "SKIPPED")}
    summary = "build %s: PASS %d, FAIL %d, SKIPPED %d" % (check.build, counts["PASS"], counts["FAIL"], counts["SKIPPED"])
    if not quiet:
        for r in check.rows:
            line = "%-8s %-34s %s" % (r["result"], r["id"], r["check"])
            if r["result"] != "PASS":
                line += "\n           expected: %s\n           actual:   %s" % (r["expected"][:400], r["actual"][:400])
            print(line)
        print(summary)
    with open(os.path.join(out, "result.json"), "w", encoding="utf-8") as stream:
        json.dump({"build": check.build, "date": datetime.datetime.now().isoformat(timespec="seconds"), "counts": counts, "checks": check.rows}, stream, indent=1)
    with open(os.path.join(out, "result.md"), "w", encoding="utf-8") as stream:
        stream.write("Export form check, %s\n\n| Check | Expected | Actual | Result | Proof |\n|---|---|---|---|---|\n" % summary)
        for r in check.rows:
            cells = ["%s: %s" % (r["id"], r["check"]), r["expected"], r["actual"], r["result"], r["proof"]]
            stream.write("| " + " | ".join(c.replace("|", "/")[:400] for c in cells) + " |\n")
    return 1 if counts["FAIL"] else 0


def main():
    sys.modules.setdefault("test_form_e2e", sys.modules[__name__])    # form_e2e_joints.py uses this module, not a second copy
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dll", help="folder with a built SW2URDF.dll; the host program is compiled against a copy of it")
    parser.add_argument("--out", help="new or empty folder for the models, the exported packages and the result")
    parser.add_argument("--launcher", default=os.environ.get("SW2URDF_SW_LAUNCHER"),
                        help="guarded launcher script that starts a new SolidWorks (or env SW2URDF_SW_LAUNCHER)")
    parser.add_argument("--launcher-python", help="Python used to run the launcher (default: this one)")
    parser.add_argument("--pid", type=int, help="use this SolidWorks instance, which must hold no documents; it is left running")
    parser.add_argument("--timeout", type=int, default=3000, help="overall limit in seconds (default 3000)")
    parser.add_argument("--case-timeout", type=int, default=1800, help="limit per scenario in seconds (default 1800)")
    parser.add_argument("--min-free-gb", type=float, default=4.0, help="stop when Windows has less memory left than this (default 4)")
    parser.add_argument("--exe-hook", help="command run on the compiled host program before it starts; {exe} is the file")
    parser.add_argument("--scenario", action="append", choices=list(all_scenarios()), help="only this scenario (repeatable; checks of the others fail)")
    parser.add_argument("--keep", action="store_true", help="keep the scratch folder")
    parser.add_argument("--evaluate", metavar="FOLDER", help="check the recorded files of an earlier run again")
    parser.add_argument("--prove", metavar="FOLDER", help="prove on an earlier run that every check can fail")
    args = parser.parse_args()
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    if args.prove:
        import form_e2e_proofs
        return form_e2e_proofs.prove(obj.long_path(os.path.abspath(args.prove)))
    if args.evaluate:
        out = obj.long_path(os.path.abspath(args.evaluate))
        return report(evaluate(out), out)
    if not args.dll or not os.path.isfile(os.path.join(args.dll, "SW2URDF.dll")):
        parser.error("needs --dll <folder with SW2URDF.dll>")
    if not args.out:
        parser.error("needs --out <folder>")
    if not args.pid and not args.launcher:
        parser.error("needs --launcher (to start an instance) or --pid (to use one you own)")
    out = run(args)
    return report(evaluate(out), out)


if __name__ == "__main__":
    sys.exit(main())
