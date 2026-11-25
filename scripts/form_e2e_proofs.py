"""Proof that every check of scripts/test_form_e2e.py can fail (test_form_e2e.py --prove <folder of a run>).

For each check that passed, one recorded thing is changed in a copy of the run, the way a product bug would change it
(a digit in the written URDF, a text in a box, a mesh that moved, a missing file), the run is evaluated again and the
check must report FAIL. A check that passed without a change made for it here, or that still passes, fails the proof.
Needs no SolidWorks and no exporter.
"""
import os
import shutil
import tempfile
import xml.etree.ElementTree as ET

import regress_obj_export as obj
import test_form_e2e as form

PKG = form.PACKAGE


class Copy:
    """A copy of a run that a change is made in."""

    def __init__(self, source, target):
        self.out = target
        shutil.copytree(source, target, ignore=shutil.ignore_patterns("models", "result.*"), dirs_exist_ok=True)

    def path(self, *parts):
        return os.path.join(self.out, *parts)

    def tsv(self, folder, **changes):
        """Changes values of a probe.tsv; a name here has __ for a dot."""
        path = self.path(folder, "probe.tsv")
        with open(path, encoding="utf-8") as stream:
            lines = stream.read().splitlines()
        for name, value in changes.items():
            key = name.replace("__", ".")
            lines = [l for l in lines if l.split("\t")[0] != key]
            lines.append(key + "\t" + value)
        with open(path, "w", encoding="utf-8") as stream:
            stream.write("\n".join(lines) + "\n")
        return True

    def dump(self, case, label, control, value):
        return self.tsv(case, **{"dump__%s__%s" % (label, control): value})

    def old(self, case, key):
        return obj.tsv(self.path(case, "probe.tsv"))[key]

    def urdf(self, case, change):
        """change(root) edits the written robot file."""
        path = form.Case(self.out, case).robot_file()
        tree = ET.parse(path)
        change(tree.getroot())
        tree.write(path, encoding="utf-8")
        return True

    def attribute(self, case, xpath, name, change):
        def edit(root):
            element = root.find(xpath)
            element.set(name, change(element.get(name)))
        return self.urdf(case, edit)


def other(text):
    """The same number with another last digit."""
    return text[:-1] + ("1" if text[-1] != "1" else "2")


def first_plus(amount):
    def change(text):
        values = text.split()
        values[0] = repr(float(values[0]) + amount)
        return " ".join(values)
    return change


def limit(case, joint, name, change=other):
    return lambda c: c.attribute(case, "joint[@name='%s']/limit" % joint, name, change)


def box(case, label, control, value):
    return lambda c: c.dump(case, label, control, value)


def write(*parts):
    def change(c):
        path = c.path(*parts)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as stream:
            stream.write("x")
    return change


def replace_text(old, new, *parts):
    def change(c):
        path = c.path(*parts)
        with open(path, encoding="utf-8-sig") as stream:
            text = stream.read()
        if old not in text:
            raise RuntimeError("%r not in %s" % (old, parts[-1]))
        with open(path, "w", encoding="utf-8") as stream:
            stream.write(text.replace(old, new))
    return change


def xacro_rename(c):
    path = form.Case(c.out, "hinge_xacro").robot_file()
    os.replace(path, os.path.join(os.path.dirname(path), PKG + ".urdf.xacro"))


def xacro_mass(c):
    c.attribute("hinge_xacro", "link[@name='arm']/inertial/mass", "value", lambda v: repr(float(v) * 1.001))


def launch_name(c):
    folder = c.path("hinge_xacro", "export", PKG, "launch")
    name = os.path.basename(form.Case(c.out, "hinge_xacro").robot_file())
    for file in os.listdir(folder)[:1]:
        replace_text("/urdf/" + name, "/urdf/" + PKG + ".urdf", "hinge_xacro", "export", PKG, "launch", file)(c)


def turn(c):
    c.tsv("multi_stl", sw__block__frame=c.old("multi_stl", "sw.block.component"))


MESH_JOINT = {"arm": "arm_joint", "block": "block_joint", "post": "post_joint", "slider": "slide_joint"}

CHANGES = {
    "hinge.motion": lambda c: c.tsv("measured", hinge__motion="-30 90"),
    "slide.motion": lambda c: c.tsv("measured", slide__motion="-20 10"),
    "hinge.limits": limit("hinge_plain", "arm_joint", "lower"),
    "origin.limits": limit("hinge_at_origin", "arm_joint", "lower"),
    "hinge.limits.deg": limit("hinge_left_in_deg", "arm_joint", "upper"),
    # what the combined build showed before its fix
    "hinge.page.deg": box("hinge_browse", "deg1", "textBoxLimitLower", "-89.9999999997"),
    "hinge.page.rad": box("hinge_browse", "rad1", "textBoxLimitUpper", "0.523598775597"),
    "hinge.page.rpy": box("hinge_browse", "rad1", "textBoxJointRoll", "1.5707963"),
    "hinge.page.same": box("hinge_browse", "deg3", "textBoxLimitUpper", "30.0000000001"),
    "hinge.repeat": limit("hinge_plain_again", "arm_joint", "upper"),
    "hinge.browse": limit("hinge_browse", "arm_joint", "upper"),
    "hinge.deg": limit("hinge_left_in_deg", "arm_joint", "lower"),
    "edit.deg.urdf": limit("hinge_edit_deg", "arm_joint", "upper"),
    "edit.deg.rpy": lambda c: c.attribute("hinge_edit_deg", "joint/origin", "rpy", other),
    "edit.deg.page.typed": box("hinge_edit_deg", "typed", "textBoxLimitLower", "-45.5000000001"),
    "edit.deg.page.switched": box("hinge_edit_deg", "switched", "textBoxLimitUpper", "1.23450000001"),
    "edit.deg.page.back": box("hinge_edit_deg", "back", "textBoxLimitVelocity", "179.999999999"),
    "edit.rad": limit("hinge_edit_rad", "arm_joint", "lower"),
    "slide.limits": limit("slide_plain", "arm_joint", "upper"),
    "slide.limits.deg": limit("slide_left_in_deg", "arm_joint", "lower"),
    "slide.page": box("slide_left_in_deg", "deg", "textBoxLimitLower", "-0.572957795131"),
    "slide.caption": box("slide_left_in_deg", "deg", "labelLowerLimit", "lower (deg)"),
    "slide.converted": box("slide_left_in_deg", "deg", "textBoxLimitUpper", "1.1459155903"),
    "slide.converted.textBoxJointRoll": box("slide_left_in_deg", "deg", "textBoxJointRoll", "89.9999999"),
    "slide.converted.textBoxJointPitch": box("slide_left_in_deg", "deg", "textBoxJointPitch", "89.9999999"),
    "slide.converted.textBoxJointYaw": box("slide_left_in_deg", "deg", "textBoxJointYaw", "89.9999999"),
    "free.page": box("slide_no_limit", "page", "textBoxLimitLower", "0"),
    "free.limits": limit("slide_no_limit", "arm_joint", "lower"),
    "two_bases.limits": limit("two_bases", "slide_joint", "upper"),
    "joints.kept": box("multi_joints", "slide2", "textBoxLimitUpper", "0.25"),
    "joints.shown": box("multi_joints", "slide1", "textBoxJointName", "arm_joint"),
    "joints.urdf": limit("multi_joints", "slide_joint", "upper", lambda v: "0.25"),
    "joints.urdf.values": limit("multi_joints", "arm_joint", "effort", lambda v: "10"),
    "unit.kept.deg": box("unit_remembered", "deg", "comboBoxAngleUnitJoint.index", "0"),
    "unit.kept.rad": box("unit_remembered", "rad", "comboBoxAngleUnitJoint.index", "1"),
    "xacro.file": write("hinge_xacro", "export", PKG, "urdf", PKG + ".urdf"),
    "xacro.name": xacro_rename,
    "xacro.content": xacro_mass,
    "xacro.equivalent": xacro_mass,
    "xacro.expanded": xacro_mass,
    "xacro.launch": launch_name,
    "model.only": write("hinge_model_only", "export", PKG, "meshes", "arm.STL"),
    "model.same": limit("hinge_model_only", "arm_joint", "upper"),
    "model.full.multi_stl": lambda c: os.remove(c.path("multi_stl", "export", PKG, "meshes", "block.STL")),
    "model.full.multi_obj": lambda c: os.remove(c.path("multi_obj", "export", PKG, "meshes", "block.mtl")),
    "sw.turned": turn,
    "multi.limits": limit("multi_stl", "slide_joint", "upper"),
    "digits": box("hinge_browse", "rad1", "textBoxJointX", "0.0300000000000001"),
}


def change_for(ident):
    if ident in CHANGES:
        return CHANGES[ident]
    parts = ident.split(".")
    if parts[0] == "run":
        # a message box that a user would have had to answer
        return lambda c: c.tsv(parts[1], dialog__message__0="[SolidWorks] something went wrong")
    if parts[0] == "axis":
        return lambda c: c.attribute(parts[1], "joint[@name='%s']/axis" % parts[2], "xyz", lambda v: "0.0001 0.0001 0.99999999")
    if parts[0] == "sw":
        case, link, what = parts[1:]
        base = "link[@name='%s']/" % link
        if what == "frame":
            return lambda c: c.attribute(case, "joint[@name='%s']/origin" % MESH_JOINT[link], "xyz", first_plus(1e-6))
        if what == "mass":
            return lambda c: c.attribute(case, base + "inertial/mass", "value", lambda v: repr(float(v) * 1.00001))
        if what == "com":
            return lambda c: c.attribute(case, base + "inertial/origin", "xyz", first_plus(1e-6))
        if what == "inertia":
            # the wrong sign of a product of inertia (a link that has one), or a moment that is slightly off
            def wrong(c):
                def edit(root):
                    inertia = root.find(base + "inertial/inertia")
                    if abs(float(inertia.get("ixy"))) > 0.01 * float(inertia.get("ixx")):
                        inertia.set("ixy", repr(-float(inertia.get("ixy"))))
                    else:
                        inertia.set("ixx", repr(float(inertia.get("ixx")) * 1.001))
                return c.urdf(case, edit)
            return wrong
        if what in ("visual", "collision"):
            return lambda c: c.attribute(case, base + what + "/origin", "xyz", first_plus(2e-5))
    return None


def prove(out):
    original = form.evaluate(out)
    bad = 0
    lines = []
    for row in original.rows:
        ident = row["id"]
        if row["result"] == "SKIPPED":
            lines.append((ident, "not run (skipped on this build)", ""))
            continue
        if row["result"] == "FAIL":
            lines.append((ident, "fails on this run", "expected %s, got %s" % (row["expected"], row["actual"])))
            continue
        change = change_for(ident)
        if change is None:
            lines.append((ident, "NO CHANGE WRITTEN", ""))
            bad += 1
            continue
        scratch = obj.long_path(tempfile.mkdtemp(prefix="sw2urdf_form_proof_"))
        try:
            change(Copy(out, scratch))
            after = {r["id"]: r for r in form.evaluate(scratch).rows}
            result = after.get(ident, {"result": "missing", "actual": ""})
            failed = sum(1 for r in after.values() if r["result"] == "FAIL")
            if result["result"] == "FAIL":
                lines.append((ident, "can fail", "%s (checks that fail with this change: %d)" % (result["actual"], failed)))
            else:
                lines.append((ident, "STILL %s" % result["result"], result["actual"]))
                bad += 1
        except Exception as error:  # noqa: BLE001
            lines.append((ident, "CHANGE FAILED", "%s: %s" % (type(error).__name__, error)))
            bad += 1
        finally:
            shutil.rmtree(scratch, ignore_errors=True)
    for ident, verdict, detail in lines:
        print("%-34s %-32s %s" % (ident, verdict, detail[:160]))
    print("%d checks proven, %d fail on this run, %d skipped, %d not proven" % (
        sum(1 for l in lines if l[1] == "can fail"), sum(1 for l in lines if l[1].startswith("fails")), sum(1 for l in lines if l[1].startswith("not run")), bad))
    with open(os.path.join(out, "proofs.md"), "w", encoding="utf-8") as stream:
        stream.write("Export form check, proof that the checks can fail (build %s)\n\n| Check | Proof | After the change |\n|---|---|---|\n" % original.build)
        for line in lines:
            stream.write("| " + " | ".join(x.replace("|", "/")[:300] for x in line) + " |\n")
    return 1 if bad else 0
