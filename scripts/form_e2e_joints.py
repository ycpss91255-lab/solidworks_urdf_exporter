"""More scenarios and checks of scripts/test_form_e2e.py: where a limit mate can be, joints on the assembly origin, the
joint type the user chose, boxes left blank, the axis of a fixed joint, Cancel, names with Chinese characters, 3DXML and
the part form. Run by test_form_e2e.py, like its own scenarios; expectations come from the typed inputs, the measured
motion and SolidWorks itself.
"""
import math
import os
import re
import xml.etree.ElementTree as ET
import zipfile

import regress_obj_export as obj
import test_form_e2e as form

AUTO, PACKAGE, FULL = form.AUTO, form.PACKAGE, form.FULL
CHINESE_AXIS = "旋轉軸"     # FormE2eProbe.cs makes the axis
CHINESE_FRAME = "\u5ea7\u6a19_1"         # make_form_fixtures.py makes the frame
DETECT = "Automatically Detect"
NOT_BUILT = [("try", "first")]
LIMIT_BOXES = ("textBoxLimitLower", "textBoxLimitUpper", "textBoxLimitEffort", "textBoxLimitVelocity")
AXIS_BOXES = ("textBoxAxisX", "textBoxAxisY", "textBoxAxisZ")


def tree(components="LIM_ARM-1", axis=AUTO, kind=DETECT, limits=None):
    arm = ("arm", "base_link", components, AUTO, axis, kind, "arm_joint")
    return [("base_link", "-", "LIM_BASE-1", "Origin_global", "", "", ""), arm + ((limits,) if limits else ())]


def opened(*steps):
    return [("open",)] + list(steps)


def nested(number):
    """One wheel of FORM_NEST: axis and frame are in the part inside the sub-assembly."""
    inside = " <FORM_WHEEL_SUB-%d/FORM_WHEEL-1>" % number
    return ("wheel%d" % number, "base_link", "FORM_WHEEL_SUB-%d" % number, CHINESE_FRAME + inside, CHINESE_AXIS + inside, "continuous", "wheel%d_joint" % number)


NEST_TREE = [("base_link", "-", "LIM_BASE-1,LIM_BASE-2", "Origin_global", "", "", ""), nested(1), nested(2)]
PLAIN = opened(("next",)) + FULL
EDITED = (("X", 0.05), ("Y", -0.02), ("Z", 0.01), ("Yaw", 0.5))
EDIT_ORIGIN = [("link", "block")] + [("type", "textBoxVisualOrigin" + name, repr(value)) for name, value in EDITED] + [("link", "arm")]
MODEL_ONLY = ("export", "model", PACKAGE)
# name: (model, tree, needs, steps), as in test_form_e2e.py
SCENARIOS = {
    # where the limit mate is
    "child2": ("FORM_CHILD2", tree("FORM_BLOCK-1,LIM_ARM-1"), None, PLAIN),
    "sub": ("FORM_SUB", tree("FORM_ARM_SUB-1"), None, PLAIN),
    # the child link on the assembly origin
    "origin_full": ("FORM_FULL", tree(), None, PLAIN),
    "origin_loose": ("FORM_LOOSE", tree(), None, NOT_BUILT),
    "origin_plane": ("FORM_PLANE", tree(), None, NOT_BUILT),
    # the joint type the user chose
    "type_hinge_revolute": ("LIM_HINGE", tree(kind="revolute"), None, PLAIN),
    "type_hinge_continuous": ("LIM_HINGE", tree(kind="continuous"), None, PLAIN),
    "type_hinge_fixed": ("LIM_HINGE", tree(kind="fixed"), None, PLAIN),
    "type_hinge_prismatic": ("LIM_HINGE", tree(kind="prismatic"), None, NOT_BUILT),
    "type_slide_prismatic": ("LIM_SLIDE", tree(kind="prismatic"), None, PLAIN),
    "type_slide_revolute": ("LIM_SLIDE", tree(kind="revolute"), None, NOT_BUILT),
    "type_slide_continuous": ("LIM_SLIDE", tree(kind="continuous"), None, NOT_BUILT),
    "type_loose_continuous": ("FORM_LOOSE", tree(kind="continuous"), None, NOT_BUILT),
    # a child that can turn and slide
    "type_cyl_detect": ("FORM_CYL", tree(), None, PLAIN),
    "type_cyl_continuous": ("FORM_CYL", tree(kind="continuous"), None, PLAIN),
    "type_cyl_prismatic": ("FORM_CYL", tree(kind="prismatic"), None, opened(
        ("joint", "arm_joint"), ("type", "textBoxLimitLower", "-0.02"), ("type", "textBoxLimitUpper", "0.03"), ("next",)) + FULL),
    # an axis with a Chinese name, for a child that can turn and slide; the settings are read back from the file
    "chinese_axis": ("FORM_CYL", tree(axis=CHINESE_AXIS, kind="continuous"), None,
                     opened(("joint", "arm_joint"), ("dump", "page"), ("next",)) + FULL + [("saved", "open"), ("reopen",), ("saved", "file")]),
    # Cancel and a new preview
    "cancel_joint": ("LIM_HINGE", tree(), None, opened(("joint", "arm_joint"), ("click", "buttonJointCancel"), ("saved", "after")) + PLAIN),
    "cancel_link": ("LIM_HINGE", tree(), None, opened(("next",), ("click", "buttonLinksCancel"), ("saved", "after")) + PLAIN),
    # effort and velocity left out, limits from the limit mate
    "blank_effort": ("LIM_HINGE", tree(limits="unset"), None, opened(
        ("joint", "arm_joint"), ("dump", "page"), ("next",), ("page", "after"))),
    "blank_effort_typed": ("LIM_HINGE", tree(limits="unset"), None, opened(
        ("joint", "arm_joint"), ("type", "textBoxLimitEffort", "3.5"), ("type", "textBoxLimitVelocity", "0.25"), ("next",)) + FULL),
    "blank_effort_message": ("LIM_HINGE", tree(limits="unset"), None, opened(
        ("joint", "arm_joint"), ("next",), ("type", "textBoxLimitEffort", "3.5"), ("type", "textBoxLimitVelocity", "0.25"), ("next",), ("page", "after"))),
    "blank_lower": ("LIM_HINGE", tree(), None, opened(("joint", "arm_joint"), ("type", "textBoxLimitLower", ""), ("next",), ("page", "after"))),
    # the axis of a joint that is made fixed and made revolute again
    "fixed_and_back": ("FORM_MULTI", form.MULTI_TREE, None, opened(
        ("joint", "arm_joint"), ("dump", "first"), ("choose", "comboBoxJointType", "fixed"), ("joint", "block_joint"), ("joint", "arm_joint"),
        ("dump", "fixed"), ("choose", "comboBoxJointType", "revolute"), ("joint", "block_joint"), ("joint", "arm_joint"), ("dump", "back"), ("next",)) + FULL),
    "fixed_on_page": ("FORM_MULTI", form.MULTI_TREE, None, opened(
        ("joint", "arm_joint"), ("dump", "first"), ("choose", "comboBoxJointType", "fixed"), ("joint", "block_joint"), ("next",)) + FULL),
    "axis_zero": ("LIM_HINGE", tree(), None, opened(
        ("joint", "arm_joint"), ("type", "textBoxAxisX", "0"), ("type", "textBoxAxisY", "0"), ("type", "textBoxAxisZ", "0"), ("next",), ("page", "after"))),
    # axis and coordinate system of a part inside a sub-assembly, two instances of it
    "nested_stl": ("FORM_NEST", NEST_TREE, None, opened(("joint", "wheel1_joint"), ("dump", "wheel1"), ("joint", "wheel2_joint"), ("dump", "wheel2"), ("next",))
                   + FULL + [("reopen",), ("saved", "file")]),
    "nested_obj": ("FORM_NEST", NEST_TREE, None, opened(("next",), ("radio", "radioButtonObj")) + FULL),
    "nested_3dxml": ("FORM_NEST", NEST_TREE, None, opened(("next",), ("radio", "radioButton3dxml")) + FULL),
    # the mesh origin edited on the link page: it moves the mesh in the frame of the link, whatever the format
    "origin_stl": ("FORM_MULTI", form.MULTI_TREE, None, opened(("next",), *EDIT_ORIGIN) + FULL),
    "origin_obj": ("FORM_MULTI", form.MULTI_TREE, None, opened(("next",), ("radio", "radioButtonObj"), *EDIT_ORIGIN) + FULL),
    "origin_3dxml": ("FORM_MULTI", form.MULTI_TREE, None, opened(("next",), ("radio", "radioButton3dxml"), *EDIT_ORIGIN) + FULL),
    # a value of the link on display is in the saved settings
    "settings_link": ("LIM_HINGE", tree(), None, opened(("next",), ("link", "arm"), ("type", "textBoxMass", "1.234")) + FULL + [("reopen",), ("saved", "file")]),
    # another configuration becomes active while the form is open: nothing is exported, its settings are not replaced
    "settings_switch": ("LIM_HINGE", tree(), None, opened(("next",), ("add_configuration", "Second")) + FULL[:1] + [("saved", "second")]),
    # the configuration is renamed after an export: its settings are offered again
    "settings_rename": ("LIM_HINGE", tree(), None, PLAIN + [("rename_configuration", "Renamed"), ("ensure",), ("saved", "renamed")]),
    # an export that fails (OBJ, a link with a texture) and one that works, in the same form
    "failed_export": ("FORM_TEXTURE", tree("FORM_TEX-1"), None, opened(
        ("next",), ("radio", "radioButtonObj"), FULL[0], ("form", "failed"), ("radio", "radioButtonStl")) + FULL + [("form", "done")]),
    # between the two pages, forwards and backwards, with links selected on the way: no value moves to another link
    "pages": ("FORM_MULTI", form.MULTI_TREE, None, opened(
        ("joint", "block_joint"), ("next",), ("link", "base_link"), ("link", "block"), ("previous",), ("joint", "post_joint"), ("next",), ("link", "slider"),
        ("link", "base_link"), ("previous",), ("joint", "arm_joint"), ("next",), ("link", "post"), ("link", "arm"), ("previous",), ("next",)) + FULL),
    # Previous, and a value typed before a joint is selected
    "previous_typed": ("LIM_HINGE", tree(), None, opened(
        ("joint", "arm_joint"), ("next",), ("previous",), ("type", "textBoxLimitEffort", "4.5"), ("next",)) + FULL),
    # Export Model File Only when the meshes do not exist: the question and its three answers
    "mesh_yes": ("LIM_HINGE", tree(), None, opened(("next",), ("answer", "Yes"), MODEL_ONLY, ("asked", "once"))),
    "mesh_no": ("LIM_HINGE", tree(), None, opened(("next",), ("answer", "No"), MODEL_ONLY, ("asked", "once"))),
    "mesh_cancel": ("LIM_HINGE", tree(), None, opened(("next",), ("answer", "Cancel"), MODEL_ONLY, ("asked", "once"))),
    "mesh_present": ("LIM_HINGE", tree(), None, PLAIN + opened(("next",), ("answer", "Cancel"), MODEL_ONLY, ("asked", "after"), ("overwritten", "after"))),
    # an export into the package of an earlier export: asked first; No writes nothing and keeps the form, Yes exports
    "overwrite_no": ("LIM_HINGE", tree(), None, PLAIN + opened(
        ("next",), ("snapshot", "before", PACKAGE), ("overwrite", "No"), FULL[0], ("form", "refused"), ("overwritten", "refused"),
        ("snapshot", "refused", PACKAGE), ("overwrite", "Yes"), FULL[0], ("overwritten", "accepted"), ("snapshot", "accepted", PACKAGE))),
    # 3DXML and the part form
    "multi_3dxml": ("FORM_MULTI", form.MULTI_TREE, None, opened(("next",), ("radio", "radioButton3dxml")) + FULL),
    "part_stl": ("FORM_BLOCK", tree(), None, [("part",), ("export_part", "stl", PACKAGE)]),
    "part_obj": ("FORM_BLOCK", tree(), None, [("part",), ("export_part", "obj", PACKAGE)]),
}
# scenarios in which the user is told something, once, and texts the message must hold
MESSAGES = {
    "origin_loose": ["arm_joint"],
    "origin_plane": ["arm_joint"],
    "type_hinge_prismatic": ["arm_joint", "arm", "base_link"],
    "type_slide_revolute": ["arm_joint", "arm", "base_link"],
    "type_slide_continuous": ["arm_joint", "arm", "base_link"],
    "type_loose_continuous": ["arm_joint", "arm", "base_link"],
    "blank_effort": ["arm_joint"],
    "blank_effort_message": ["arm_joint"],
    "blank_lower": ["arm_joint"],
    "axis_zero": ["arm_joint"],
    "cancel_joint": [],     # whether to save the changed settings; answered with Yes
    "cancel_link": [],
    "settings_rename": ["Renamed"],
}
NOT_EXPORTED = ("settings_switch",)
REFUSED = [n for n, s in SCENARIOS.items() if s[3] == NOT_BUILT]


def model_file(model):
    return model + (".SLDPRT" if model == "FORM_BLOCK" else ".SLDASM")


def dialogs(case):
    return [v for k, v in sorted(case.values.items()) if k.startswith("dialog.message") or k.startswith("dialog.stuck")]


def read_3dxml(path):
    """All vertices (m) of a 3DXML file in the frame of its root product, read without the exporter."""
    def name(element):
        return element.tag.split("}")[-1]

    with zipfile.ZipFile(path) as archive:
        root_file = re.search(r"<Root>([^<]+)</Root>", archive.read("Manifest.xml").decode("utf-8")).group(1)
        structure = next(e for e in ET.fromstring(archive.read(root_file)).iter() if name(e) == "ProductStructure")
        files, points = {}, {}
        for element in structure:
            if name(element) == "ReferenceRep":
                files[element.get("id")] = element.get("associatedFile").split(":")[-1]
        for key, file in files.items():
            points[key] = [tuple(float(x) for x in p.split()) for e in ET.fromstring(archive.read(file)).iter() if name(e) == "Positions"
                           for p in e.text.split(",")]
        children = {}
        for element in structure:
            if name(element) not in ("InstanceRep", "Instance3D"):
                continue
            values = {name(c): c.text for c in element}
            matrix = [float(x) for x in values["RelativeMatrix"].split()] if "RelativeMatrix" in values else None
            children.setdefault(values["IsAggregatedBy"], []).append((name(element), values["IsInstanceOf"], matrix))

        def walk(reference):
            found = []
            for kind, target, m in children.get(reference, []):
                inner = points[target] if kind == "InstanceRep" else walk(target)
                if m is not None:
                    inner = [tuple(m[i] * x + m[3 + i] * y + m[6 + i] * z + m[9 + i] for i in range(3)) for x, y, z in inner]
                found += inner
            return found
        return [tuple(c / 1000 for c in p) for p in walk(structure.get("root"))]


def attributes(robot, joint, element):
    found = robot.joints[joint].find(element)
    return None if found is None else dict(found.attrib)


def evaluate(check, cases, measured, guard):
    """Adds the checks of this file. guard(ident, title, function) reports an exception as FAIL."""
    def add(ident, title):
        def decorate(function):
            guard(ident, title, lambda: function(ident, title))
        return decorate

    # ---- every scenario came to its end, and the user was told exactly what is expected
    for name, (_model, _tree, _needs, steps) in SCENARIOS.items():
        @add("run." + name, "scenario %s runs to its end; messages: %s" % (name, "one " + ", ".join(MESSAGES[name]) if name in MESSAGES else "none"))
        def ran(ident, title, name=name, steps=steps):
            case, texts = cases[name], dialogs(cases[name])
            wanted = MESSAGES.get(name)
            problems = []
            if case.values.get("status") != "ok":
                problems.append("status %s %s" % (case.values.get("status"), case.values.get("error", "")[:200]))
            if len(texts) != (1 if wanted is not None else 0):
                problems.append("%d messages: %s" % (len(texts), " / ".join(texts)[:300]))
            problems += ["message without %r: %s" % (w, t[:200]) for t in texts for w in wanted or [] if w not in t]
            if name not in NOT_EXPORTED and any(s[0] == "export" for s in steps) and case.values.get("dialog.save") is None:
                problems.append("no Save As dialog")
            check.equal(ident, title, "ok", "; ".join(problems) or "ok", case.proof)

    # ---- where the limit mate is: the hinge of hinge_plain, with another link tree
    text = tuple(form.g15(math.radians(x)) for x in (-90, 30))
    for name, what in (("child2", "the second component of the child link"), ("sub", "a part inside the sub-assembly that is the child link")):
        @add(name + ".limits", "limit mate on %s: revolute, exact limit text in the URDF" % what)
        def placed(ident, title, name=name):
            robot = cases[name].robot()
            base = form.frame(cases[name].values["sw.Origin_global"])
            turned = form.expected_limits(measured[name + ".motion"], [60 - 90, 180 - 90], form.world_axis(robot, "arm_joint", base)[2])
            expected = "revolute %s %s" % tuple(form.g15(math.radians(x)) for x in turned)
            check.equal(ident, title, expected, "%s %s %s" % (robot.joints["arm_joint"].get("type"), robot.limit("arm_joint", "lower"), robot.limit("arm_joint", "upper")),
                        cases[name].urdf_proof())
        guard("axis.%s.arm_joint" % name, "joint axis", lambda n=name: form.axis_on_boss(check, cases[n], "arm_joint", measured[n + ".axis"], "revolute"))
        guard("sw.%s.arm" % name, "%s: link arm against SolidWorks" % name, lambda n=name: form.against_solidworks(check, cases[n], SCENARIOS[n][1][1]))

    # ---- the child link on the assembly origin
    @add("origin.full", "fully defined child exactly on the assembly origin: fixed joint at the assembly origin, no limits")
    def full(ident, title):
        case, robot = cases["origin_full"], cases["origin_full"].robot()
        place = robot.place("arm", form.frame(case.values["sw.Origin_global"]))
        off = form.worst(place[1], [0, 0, 0])
        check.equal(ident, title, "fixed, at the origin: True, limit None, arm in SolidWorks at 0 0 0", "%s, at the origin: %s, limit %s, arm in SolidWorks at %s" % (
            robot.joints["arm_joint"].get("type"), off < 1e-9, attributes(robot, "arm_joint", "limit"), " ".join("%g" % x for x in form.vec(measured["full.arm"])[:3])), case.urdf_proof())

    # ---- previews that may not open
    for name in REFUSED:
        chosen = SCENARIOS[name][1][1][5]

        @add(name + ".refused", "%s: the preview does not open and the joint keeps type, frame and axis (%s)" % (name, chosen))
        def refused(ident, title, name=name, chosen=chosen):
            values = cases[name].values
            check.equal(ident, title, "built False, %s, %s, %s" % (chosen, AUTO, AUTO), "built %s, %s, %s, %s" % tuple(
                [values.get("built.first")] + [values.get("state.first.arm." + k) for k in ("type", "frame", "axis")]), cases[name].proof)

    # ---- the chosen type
    for name, kind, expected in (
            ("type_hinge_revolute", "revolute", {"lower": text[0], "upper": text[1], "effort": "10", "velocity": "1"}),
            ("type_hinge_continuous", "continuous", {"effort": "10", "velocity": "1"}),
            ("type_hinge_fixed", "fixed", None),
            ("type_slide_prismatic", "prismatic", {"lower": form.g15(-10 / 1000), "upper": form.g15(20 / 1000), "effort": "10", "velocity": "1"}),
            ("type_cyl_detect", "continuous", {"effort": "10", "velocity": "1"}),
            ("type_cyl_continuous", "continuous", {"effort": "10", "velocity": "1"}),
            ("type_cyl_prismatic", "prismatic", {"lower": "-0.02", "upper": "0.03", "effort": "10", "velocity": "1"})):
        @add(name + ".urdf", "%s: the chosen type and its limits in the URDF" % name)
        def typed(ident, title, name=name, kind=kind, expected=expected):
            robot = cases[name].robot()
            check.equal(ident, title, (kind, expected), (robot.joints["arm_joint"].get("type"), attributes(robot, "arm_joint", "limit")), cases[name].urdf_proof())
        if kind != "fixed":
            key, axis_kind = ("cyl" if "_cyl_" in name else "slide" if kind == "prismatic" else "hinge"), kind
            guard("axis.%s.arm_joint" % name, "joint axis", lambda n=name, k=key, t=axis_kind: form.axis_on_boss(check, cases[n], "arm_joint", measured[k + ".axis"], t))

    # ---- the axis with a Chinese name
    @add("chinese.name", "axis with a Chinese name: the model, the page, the settings of the open document and of the file")
    def chinese(ident, title):
        v = cases["chinese_axis"].values
        check.equal(ident, title, [CHINESE_AXIS] * 4, [measured.get("cyl.axis.name"), v.get("dump.page.comboBoxAxis"), v.get("state.open.arm.axis"), v.get("state.file.arm.axis")],
                    cases["chinese_axis"].proof)

    @add("chinese.type", "axis with a Chinese name: the chosen type in the settings of the file, also for a child that can turn and slide")
    def chinese_type(ident, title):
        check.equal(ident, title, "continuous", cases["chinese_axis"].values.get("state.file.arm.type"), cases["chinese_axis"].proof)
    guard("axis.chinese_axis.arm_joint", "joint axis", lambda: form.axis_on_boss(check, cases["chinese_axis"], "arm_joint", measured["cyl.axis"], "continuous"))

    # ---- Cancel
    for name in ("cancel_joint", "cancel_link"):
        @add(name + ".saved", "%s: the settings saved by Cancel still say automatic" % name)
        def cancelled(ident, title, name=name):
            v = cases[name].values
            check.equal(ident, title, [DETECT, AUTO, AUTO], [v.get("state.after.arm." + k) for k in ("type", "frame", "axis")], cases[name].proof)

        @add(name + ".again", "%s: a new preview and export after Cancel give the joint of hinge_plain" % name)
        def again(ident, title, name=name):
            check.equal(ident, title, form.joint_texts(cases["hinge_plain"]), form.joint_texts(cases[name]), cases[name].urdf_proof())

    # ---- blank boxes
    @add("blank.page", "effort and velocity left out, limits from the limit mate: the page shows the limits and two blank boxes")
    def blank(ident, title):
        check.equal(ident, title, [form.g12(math.radians(-90)), form.g12(math.radians(30)), "", ""], [cases["blank_effort"].box("page", b) for b in LIMIT_BOXES], cases["blank_effort"].proof)
    for name, what in (("blank_effort", "effort and velocity blank"), ("blank_lower", "lower limit deleted"), ("axis_zero", "axis 0 0 0")):
        @add(name + ".stays", "%s: Next stays on the joint page" % what)
        def stays(ident, title, name=name):
            check.equal(ident, title, "joint", cases[name].values.get("page.after"), cases[name].proof)

    @add("blank.typed", "effort and velocity typed before Next: the values in the URDF")
    def typed_before(ident, title):
        check.equal(ident, title, {"lower": text[0], "upper": text[1], "effort": "3.5", "velocity": "0.25"}, attributes(cases["blank_effort_typed"].robot(), "arm_joint", "limit"),
                    cases["blank_effort_typed"].urdf_proof())

    @add("blank.corrected", "effort and velocity typed after the message, in a robot with one joint: Next goes on")
    def typed_after(ident, title):
        check.equal(ident, title, "link", cases["blank_effort_message"].values.get("page.after"), cases["blank_effort_message"].proof)

    # ---- fixed and back
    @add("fixed.page", "joint made fixed on its page, other joint visited: axis and limit boxes show the same")
    def fixed_page(ident, title):
        case = cases["fixed_and_back"]
        shown = {label: [case.box(label, b) for b in AXIS_BOXES + LIMIT_BOXES] for label in ("first", "fixed", "back")}
        if None in shown["first"] or "" in shown["first"]:
            raise RuntimeError("boxes of the first visit: %s" % shown["first"])
        check.equal(ident, title, [shown["first"]] * 2, [shown["fixed"], shown["back"]], case.proof)

    @add("fixed.back", "joint made fixed and revolute again: the joints of the URDF are those of multi_stl")
    def fixed_back(ident, title):
        check.equal(ident, title, form.joint_texts(cases["multi_stl"]), form.joint_texts(cases["fixed_and_back"]), cases["fixed_and_back"].urdf_proof())

    @add("fixed.urdf", "joint left fixed: type, no limits, and the axis it had")
    def fixed_urdf(ident, title):
        robot = cases["fixed_on_page"].robot()
        check.equal(ident, title, ("fixed", None, attributes(cases["multi_stl"].robot(), "arm_joint", "axis")),
                    (robot.joints["arm_joint"].get("type"), attributes(robot, "arm_joint", "limit"), attributes(robot, "arm_joint", "axis")), cases["fixed_on_page"].urdf_proof())

    # ---- reference geometry inside sub-assemblies
    @add("nested.names", "axis and coordinate system of a part inside a sub-assembly: page, and settings of the saved file")
    def nested_names(ident, title):
        v, expected, actual = cases["nested_stl"].values, [], []
        for line in NEST_TREE[1:]:
            expected += [line[3], line[4]] * 2
            actual += [v.get("dump.%s.%s" % (line[0], b)) for b in ("comboBoxOrigin", "comboBoxAxis")] + [v.get("state.file.%s.%s" % (line[0], k)) for k in ("frame", "axis")]
        check.equal(ident, title, expected, actual, cases["nested_stl"].proof)
    for name in ("nested_stl", "nested_obj", "nested_3dxml"):
        for number in (1, 2):
            guard("axis.%s.wheel%d_joint" % (name, number), "joint axis", lambda n=name, i=number: form.axis_on_boss(
                check, cases[n], "wheel%d_joint" % i, measured["nest%d.axis" % i], "continuous"))
        for line in NEST_TREE:
            guard("sw.%s.%s" % (name, line[0]), "%s: link %s against SolidWorks" % (name, line[0]), lambda n=name, l=line: form.against_solidworks(check, cases[n], l))

    # ---- the mesh origin edited on the link page
    for name in ("origin_stl", "origin_obj", "origin_3dxml"):
        for kind in ("visual", "collision"):
            @add("%s.%s" % (name, kind), "%s: %s mesh of the block, with the typed origin, against the bodies in SolidWorks moved by it in the link frame" % (name, kind))
            def moved(ident, title, name=name, kind=kind):
                case, robot = cases[name], cases[name].robot()
                placed = robot.place("block", form.frame(case.values["sw.Origin_global"]))
                yaw = EDITED[3][1]
                typed = ([[math.cos(yaw), -math.sin(yaw), 0], [math.sin(yaw), math.cos(yaw), 0], [0, 0, 1]], [v for _n, v in EDITED[:3]])
                back = (form.transpose(placed[0]), [-x for x in form.apply(form.transpose(placed[0]), placed[1])])
                uri, origin = robot.mesh("block", kind)
                path = obj.resolve_mesh(case.package, uri)
                points = [form.transformed(form.compose(placed, origin), p) for p in form.mesh_points(path)]
                vertices = form.vec(case.values["sw.block.vertices"])
                corners = [form.transformed(form.compose(form.compose(placed, typed), back), vertices[i:i + 3]) for i in range(0, len(vertices), 3)]
                far = max(min(math.dist(c, p) for p in points) for c in corners)
                check.add(ident, title, "%d corners within 1e-5 m" % len(corners), "farthest corner %.2g m" % far, "PASS" if far <= 1e-5 else "FAIL", case.urdf_proof())

    # ---- saved settings
    @add("settings_link.mass", "mass typed for the link on display: in the URDF and in the settings of the saved file")
    def settings_link(ident, title):
        case = cases["settings_link"]
        check.equal(ident, title, ["1.234", "1.234"], [case.robot().links["arm"].find("inertial/mass").get("value"), case.values.get("state.file.arm.mass")], case.proof)

    @add("settings_switch.kept", "another configuration activated while the form is open: told once, nothing exported, no settings of it")
    def settings_switch(ident, title):
        case = cases["settings_switch"]
        check.equal(ident, title, "named in a message: True, package: False, settings: False", "named in a message: %s, package: %s, settings: %s" % (
            "Second" in case.values.get("messages", ""), case.robot_file() is not None, case.values.get("state.second.found")), case.proof)

    @add("settings_rename.kept", "configuration renamed after an export: the settings are back, with the link tree")
    def settings_rename(ident, title):
        v = cases["settings_rename"].values
        check.equal(ident, title, ["True", "revolute"], [v.get("state.renamed.found"), v.get("state.renamed.arm.type")], cases["settings_rename"].proof)

    # ---- an export that fails
    @add("failed_export.kept", "OBJ export of a link with a texture: told why, the form stays open; STL in the same form closes it and writes the package")
    def failed_export(ident, title):
        case = cases["failed_export"]
        message = case.values.get("messages", "")
        check.equal(ident, title, "names the link and STL: True, form open then closed, meshes ['arm.STL', 'base_link.STL'], robot file: True",
                    "names the link and STL: %s, form %s then %s, meshes %s, robot file: %s" % (
                        "arm" in message and "STL" in message, case.values.get("form.failed"), case.values.get("form.done"),
                        [m for m in case.files("meshes") if m.endswith(".STL")], case.robot_file() is not None), case.proof)
    guard("sw.failed_export.arm", "failed_export: link arm against SolidWorks", lambda: form.against_solidworks(check, cases["failed_export"], SCENARIOS["failed_export"][1][1]))

    # ---- between the pages
    @add("pages.urdf", "several visits of both pages without an edit: the joints of the URDF are those of multi_stl")
    def pages(ident, title):
        check.equal(ident, title, form.joint_texts(cases["multi_stl"]), form.joint_texts(cases["pages"]), cases["pages"].urdf_proof())
    for line in form.MULTI_TREE:
        guard("sw.pages.%s" % line[0], "pages: link %s against SolidWorks" % line[0], lambda l=line: form.against_solidworks(check, cases["pages"], l))

    @add("previous.typed", "effort typed after Previous, before a joint is selected: the value in the URDF")
    def previous_typed(ident, title):
        check.equal(ident, title, "4.5", cases["previous_typed"].robot().limit("arm_joint", "effort"), cases["previous_typed"].urdf_proof())

    # ---- Export Model File Only without the meshes
    for name, robot_file, meshes in (("mesh_yes", True, ["arm.STL", "base_link.STL"]), ("mesh_no", True, []), ("mesh_cancel", False, [])):
        @add(name + ".result", "Export Model File Only, meshes missing, answer %s: asked once about both links; what is written" % name[5:])
        def mesh_answer(ident, title, name=name, robot_file=robot_file, meshes=meshes):
            case = cases[name]
            question = case.values.get("questions", "")
            check.equal(ident, title, "asked 1, names both links: True, robot file: %s, meshes %s" % (robot_file, meshes),
                        "asked %s, names both links: %s, robot file: %s, meshes %s" % (case.values.get("asked.once"), "arm" in question and "base_link" in question,
                                                                                      case.robot_file() is not None, case.files("meshes")), case.proof)

    @add("mesh_present.result", "Export Model File Only after a full export into the same package: nothing is asked about meshes")
    def mesh_present(ident, title):
        check.equal(ident, title, "0", cases["mesh_present"].values.get("asked.after"), cases["mesh_present"].proof)

    # ---- an export into an existing package
    @add("mesh_present.overwrite", "Export Model File Only into the package of an earlier export: asked once whether to overwrite it, naming its folder")
    def mesh_present_overwrite(ident, title):
        v = cases["mesh_present"].values
        check.equal(ident, title, "asked 1, names the folder: True",
                    "asked %s, names the folder: %s" % (v.get("overwrite.after"), "\\" + PACKAGE in v.get("overwrites", "")), cases["mesh_present"].proof)

    @add("overwrite.refused", "export into an existing package, answer No: nothing written, the form stays open; answer Yes: the package is written and the form closes")
    def overwrite_refused(ident, title):
        v = cases["overwrite_no"].values
        check.equal(ident, title, "asked 1 then 2, files kept: True, form open, written after Yes: True, form closed: True",
                    "asked %s then %s, files kept: %s, form %s, written after Yes: %s, form closed: %s" % (
                        v.get("overwrite.refused"), v.get("overwrite.accepted"), v.get("snapshot.before") == v.get("snapshot.refused") != "none",
                        v.get("form.refused"), v.get("snapshot.accepted") != v.get("snapshot.refused"), v.get("export.%s.closed" % PACKAGE)),
                    cases["overwrite_no"].proof)

    # ---- 3DXML
    @add("3dxml.files", "3DXML: one mesh per link and nothing else, each a 3DXML archive with geometry")
    def xml_files(ident, title):
        case = cases["multi_3dxml"]
        sound = []
        for file in case.files("meshes"):
            with zipfile.ZipFile(os.path.join(case.package, "meshes", file)) as archive:
                inside = "".join(archive.read(n).decode("utf-8", "replace") for n in archive.namelist())
            sound.append(file if "Positions" in inside else file + " (no geometry)")
        check.equal(ident, title, sorted(l[0] + ".3dxml" for l in form.MULTI_TREE), sorted(sound), "multi_3dxml/export")

    @add("3dxml.urdf", "3DXML: every visual and collision of the URDF names the mesh of its link")
    def xml_urdf(ident, title):
        robot = cases["multi_3dxml"].robot()
        check.equal(ident, title, ["package://%s/meshes/%s.3dxml" % (PACKAGE, l[0]) for l in form.MULTI_TREE for _ in range(2)],
                    [robot.mesh(l[0], k)[0] for l in form.MULTI_TREE for k in ("visual", "collision")], cases["multi_3dxml"].urdf_proof())

    for line in form.MULTI_TREE:
        guard("sw.multi_3dxml.%s" % line[0], "multi_3dxml: link %s against SolidWorks" % line[0], lambda l=line: form.against_solidworks(check, cases["multi_3dxml"], l))

    # ---- part form
    for name, extensions in (("part_stl", [".STL"]), ("part_obj", [".mtl", ".obj"])):
        @add(name + ".files", "%s: the meshes of the part form" % name)
        def part_files(ident, title, name=name, extensions=extensions):
            check.equal(ident, title, [PACKAGE + e for e in extensions], [f for f in cases[name].files("meshes") if not f.lower().endswith(".3dxml")], name + "/export")
        guard("sw.%s.%s" % (name, PACKAGE), "%s: the part against SolidWorks" % name, lambda n=name: form.against_solidworks(check, cases[n], (PACKAGE, "-")))


# ------------------------------------------------------------------------------------------- proofs

def changes(proofs):
    """One change per check that must make it fail (form_e2e_proofs.py)."""
    box, limit = proofs.box, proofs.limit

    def page(case, value):
        return lambda c: c.tsv(case, page__after=value)

    def kind(case, value):
        return lambda c: c.attribute(case, "joint[@name='arm_joint']", "type", lambda v: value)

    def moved(case, kind):
        return lambda c: c.attribute(case, "link[@name='block']/%s/origin" % kind, "xyz", proofs.first_plus(2e-5))

    def remove_mesh(case, file):
        return lambda c: os.remove(c.path(case, "export", PACKAGE, "meshes", file))

    result = {
        "child2.limits": limit("child2", "arm_joint", "lower"),
        "sub.limits": limit("sub", "arm_joint", "upper"),
        "origin.full": kind("origin_full", "revolute"),
        "chinese.name": lambda c: c.tsv("chinese_axis", state__file__arm__axis="???"),
        "chinese.type": lambda c: c.tsv("chinese_axis", state__file__arm__type="fixed"),
        "cancel_joint.saved": lambda c: c.tsv("cancel_joint", state__after__arm__type="revolute"),
        "cancel_joint.again": limit("cancel_joint", "arm_joint", "upper"),
        "cancel_link.saved": lambda c: c.tsv("cancel_link", state__after__arm__axis="Axis_arm_joint"),
        "cancel_link.again": limit("cancel_link", "arm_joint", "lower"),
        "blank.page": box("blank_effort", "page", "textBoxLimitEffort", "0"),
        "blank_effort.stays": page("blank_effort", "link"),
        "blank_lower.stays": page("blank_lower", "link"),
        "axis_zero.stays": page("axis_zero", "link"),
        "blank.typed": limit("blank_effort_typed", "arm_joint", "effort", lambda v: "0"),
        "blank.corrected": page("blank_effort_message", "joint"),
        "fixed.page": box("fixed_and_back", "back", "textBoxAxisZ", "0"),
        "fixed.back": limit("fixed_and_back", "arm_joint", "lower"),
        "fixed.urdf": lambda c: c.attribute("fixed_on_page", "joint[@name='arm_joint']/axis", "xyz", lambda v: "0 0 0"),
        "3dxml.files": remove_mesh("multi_3dxml", "block.3dxml"),
        "3dxml.urdf": lambda c: c.attribute("multi_3dxml", "link[@name='block']/visual/geometry/mesh", "filename", lambda v: v.replace("block", "arm")),
        "origin_stl.visual": moved("origin_stl", "visual"), "origin_stl.collision": moved("origin_stl", "collision"),
        "origin_obj.visual": moved("origin_obj", "visual"), "origin_obj.collision": moved("origin_obj", "collision"),
        "origin_3dxml.visual": moved("origin_3dxml", "visual"), "origin_3dxml.collision": moved("origin_3dxml", "collision"),
        "settings_link.mass": lambda c: c.tsv("settings_link", state__file__arm__mass="0.5"),
        "settings_switch.kept": lambda c: c.tsv("settings_switch", state__second__found="True"),
        "settings_rename.kept": lambda c: c.tsv("settings_rename", state__renamed__found="False"),
        "failed_export.kept": lambda c: c.tsv("failed_export", form__failed="closed"),
        "nested.names": lambda c: c.tsv("nested_stl", state__file__wheel2__axis="???"),
        "pages.urdf": limit("pages", "arm_joint", "upper"),
        "previous.typed": limit("previous_typed", "arm_joint", "effort", lambda v: "10"),
        "mesh_yes.result": remove_mesh("mesh_yes", "arm.STL"),
        "mesh_no.result": lambda c: c.tsv("mesh_no", asked__once="0"),
        "mesh_cancel.result": lambda c: c.tsv("mesh_cancel", asked__once="0"),
        "mesh_present.result": lambda c: c.tsv("mesh_present", asked__after="1"),
        "mesh_present.overwrite": lambda c: c.tsv("mesh_present", overwrite__after="0"),
        "overwrite.refused": lambda c: c.tsv("overwrite_no", snapshot__refused="9 files 00000000"),
        "part_stl.files": remove_mesh("part_stl", PACKAGE + ".STL"),
        "part_obj.files": remove_mesh("part_obj", PACKAGE + ".mtl"),
        "type_hinge_revolute.urdf": limit("type_hinge_revolute", "arm_joint", "lower"),
        "type_hinge_continuous.urdf": kind("type_hinge_continuous", "revolute"),
        "type_hinge_fixed.urdf": kind("type_hinge_fixed", "revolute"),
        "type_slide_prismatic.urdf": limit("type_slide_prismatic", "arm_joint", "upper"),
        "type_cyl_detect.urdf": kind("type_cyl_detect", "fixed"),
        "type_cyl_continuous.urdf": kind("type_cyl_continuous", "fixed"),
        "type_cyl_prismatic.urdf": limit("type_cyl_prismatic", "arm_joint", "lower"),
    }
    for name in REFUSED:
        result[name + ".refused"] = lambda c, n=name: c.tsv(n, built__first="True")
    for name in MESSAGES:
        # one more message than the user has to see
        result["run." + name] = lambda c, n=name: c.tsv(n, dialog__message__9="[SolidWorks] something went wrong")
    return result
