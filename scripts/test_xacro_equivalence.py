#!/usr/bin/env python3
"""Tests for scripts/check_xacro_equivalence.py. Standard library only; no xacro and no SolidWorks needed.

    python scripts/test_xacro_equivalence.py
"""
import contextlib
import io
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_xacro_equivalence as cx  # noqa: E402

BASE = """<?xml version="1.0" encoding="utf-8"?>
<!-- generated -->
<robot name="r">
  <link name="base_link">
    <inertial>
      <origin xyz="0.001 0 0.5" rpy="0 0 0"/>
      <mass value="1.5"/>
      <inertia ixx="0.01" ixy="0" ixz="0" iyy="0.02" iyz="0" izz="0.03"/>
    </inertial>
    <visual>
      <origin xyz="0 0 0" rpy="0 0 0"/>
      <geometry><mesh filename="package://r/meshes/base_link.STL"/></geometry>
      <material name=""><color rgba="0.5 0.5 0.5 1"/></material>
    </visual>
    <collision>
      <origin xyz="0 0 0" rpy="0 0 0"/>
      <geometry><mesh filename="package://r/meshes/base_link.STL"/></geometry>
    </collision>
  </link>
  <link name="arm"/>
  <joint name="j1" type="revolute">
    <origin xyz="0 0 0.1" rpy="0 0 1.5707963267948966"/>
    <parent link="base_link"/>
    <child link="arm"/>
    <axis xyz="0 0 1"/>
    <limit lower="-1" upper="1" effort="10" velocity="2"/>
  </joint>
</robot>
"""


def diff(expanded, original=BASE, tolerance=1e-9):
    return cx.compare_strings(original, expanded, tolerance)[0]


def replaced(old, new):
    assert old in BASE, old
    return BASE.replace(old, new, 1)


class Equivalent(unittest.TestCase):
    def test_identical(self):
        self.assertEqual(diff(BASE), [])

    def test_facts(self):
        self.assertEqual(cx.compare_strings(BASE, BASE)[1], "2 links, 1 joints")

    def test_whitespace(self):
        text = BASE.replace("><", ">\n\n   <").replace(' xyz="0 0 1"', '\n   xyz="0   0 1"')
        self.assertEqual(diff(text), [])

    def test_attribute_order(self):
        self.assertEqual(diff(replaced('<joint name="j1" type="revolute">', '<joint type="revolute" name="j1">')), [])
        self.assertEqual(diff(replaced('<limit lower="-1" upper="1" effort="10" velocity="2"/>',
                                       '<limit velocity="2" effort="10" upper="1" lower="-1"/>')), [])

    def test_comments(self):
        self.assertEqual(diff(replaced("<mass", "<!-- note --><mass")), [])
        self.assertEqual(diff(BASE.replace("<!-- generated -->", "")), [])

    def test_number_format(self):
        self.assertEqual(diff(replaced('value="1.5"', 'value="1.50E0"')), [])
        self.assertEqual(diff(replaced('ixx="0.01"', 'ixx="1e-2"')), [])
        self.assertEqual(diff(replaced('xyz="0.001 0 0.5"', 'xyz="1E-3 0.0 5e-1"')), [])

    def test_within_tolerance(self):
        self.assertEqual(diff(replaced('value="1.5"', 'value="1.5000000000001"')), [])

    def test_link_order_and_xmlns(self):
        swapped = BASE.replace('<link name="arm"/>', "").replace("</joint>", '</joint>\n<link name="arm"/>')
        self.assertEqual(diff(swapped.replace("<robot ", '<robot xmlns:xacro="http://www.ros.org/wiki/xacro" ')), [])


class Different(unittest.TestCase):
    def assertDiffAt(self, text, where, what=""):
        found = diff(text)
        self.assertTrue(any(where in w and what in d for w, d in found), "%s not in %s" % (where, found))

    def test_mass(self):
        self.assertDiffAt(replaced('value="1.5"', 'value="1.6"'), "link[base_link]/inertial/mass@value", "'1.5' != '1.6'")

    def test_inertia(self):
        self.assertDiffAt(replaced('izz="0.03"', 'izz="0.031"'), "inertia@izz")

    def test_inertial_origin(self):
        self.assertDiffAt(replaced('xyz="0.001 0 0.5"', 'xyz="0.001 0 0.6"'), "inertial/origin@xyz")

    def test_joint_origin_rpy(self):
        self.assertDiffAt(replaced("1.5707963267948966", "1.57"), "joint[j1]/origin@rpy")

    def test_mesh_path(self):
        text = BASE.replace("meshes/base_link.STL", "meshes/other.STL", 1)
        self.assertDiffAt(text, "visual/geometry/mesh@filename")

    def test_missing_joint(self):
        text = BASE[:BASE.index("<joint")] + "</robot>\n"
        found = diff(text)
        self.assertIn(("robot[r]/joint[j1]", "only in original"), found)
        self.assertTrue(any("joint count 1 != 0" in d for _, d in found))

    def test_extra_link(self):
        found = diff(replaced('<link name="arm"/>', '<link name="arm"/><link name="extra"/>'))
        self.assertTrue(any("link count 2 != 3" in d for _, d in found))
        self.assertIn(("robot[r]/link[extra]", "only in expanded"), found)

    def test_renamed_link(self):
        found = diff(replaced('<link name="arm"/>', '<link name="forearm"/>').replace('<child link="arm"/>', '<child link="forearm"/>'))
        self.assertIn(("robot[r]/link[arm]", "only in original"), found)
        self.assertIn(("robot[r]/link[forearm]", "only in expanded"), found)

    def test_joint_type_parent_child_axis_limit(self):
        self.assertDiffAt(replaced('type="revolute"', 'type="continuous"'), "joint[j1]@type")
        self.assertDiffAt(replaced('<parent link="base_link"/>', '<parent link="arm"/>'), "parent@link")
        self.assertDiffAt(replaced('<child link="arm"/>', '<child link="x"/>'), "child@link")
        self.assertDiffAt(replaced('<axis xyz="0 0 1"/>', '<axis xyz="0 1 0"/>'), "axis@xyz")
        self.assertDiffAt(replaced('upper="1"', 'upper="2"'), "limit@upper")

    def test_outside_tolerance(self):
        self.assertTrue(diff(replaced('value="1.5"', 'value="1.5001"')))
        self.assertEqual(diff(replaced('value="1.5"', 'value="1.5001"'), tolerance=1e-3), [])

    def test_wrong_root(self):
        with self.assertRaises(ValueError):
            cx.compare_strings(BASE, "<notrobot/>")


class CommandLine(unittest.TestCase):
    def run_main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = cx.main(list(argv))
        return code, out.getvalue()

    def test_expanded_pass_fail_and_exit_codes(self):
        with tempfile.TemporaryDirectory() as folder:
            a, b, c = (os.path.join(folder, n) for n in ("a.urdf", "b.urdf", "c.urdf"))
            for path, text in ((a, BASE), (b, BASE.replace("<link", "\n<link")), (c, replaced('value="1.5"', 'value="9"'))):
                with open(path, "w", encoding="utf-8") as stream:
                    stream.write(text)
            code, out = self.run_main("--expanded", a, b)
            self.assertEqual(code, 0)
            self.assertIn("PASS", out)
            code, out = self.run_main("--expanded", a, c)
            self.assertEqual(code, 1)
            self.assertIn("FAIL", out)
            self.assertIn("link[base_link]/inertial/mass@value", out)

    def test_unreadable_input_is_fail_not_crash(self):
        code, _ = self.run_main("--expanded", "does_not_exist.urdf", "does_not_exist2.urdf")
        self.assertEqual(code, 1)

    def test_skip_exit_code_without_xacro(self):
        original = cx.find_xacro
        cx.find_xacro = lambda: None
        try:
            code, out = self.run_main("--pair", __file__, __file__)
        finally:
            cx.find_xacro = original
        self.assertEqual(code, 3)
        self.assertIn("SKIP", out)
        self.assertEqual(out.count("PASS 0"), 1)

    def test_identity_on_committed_examples(self):
        code, out = self.run_main("--identity")
        self.assertEqual(code, 0, out)


MACRO = """<?xml version="1.0" encoding="utf-8"?>
<robot xmlns:xacro="http://www.ros.org/wiki/xacro">
  <xacro:macro name="r" params="prefix:='' parent:='' xyz:='0 0 0' rpy:='0 0 0'">
%s
    <xacro:if value="${parent != ''}">
      <joint name="${prefix}base_link_parent_joint" type="fixed">
        <origin xyz="${xyz}" rpy="${rpy}"/>
        <parent link="${parent}"/>
        <child link="${prefix}base_link"/>
      </joint>
    </xacro:if>
  </xacro:macro>
</robot>
"""
ROBOT = """<?xml version="1.0" encoding="utf-8"?>
<robot name="r" xmlns:xacro="http://www.ros.org/wiki/xacro">
  <xacro:include filename="r_macro.xacro"/>
  <xacro:r/>
</robot>
"""


def body(text=BASE):
    inner = text.split("<robot name=\"r\">", 1)[1].rsplit("</robot>", 1)[0]
    for name in ("base_link", "arm", "j1"):
        inner = inner.replace('name="%s"' % name, 'name="${prefix}%s"' % name)
        inner = inner.replace('link="%s"' % name, 'link="${prefix}%s"' % name)
    return inner


class MacroDefaults(unittest.TestCase):
    def expand(self, macro, robot=ROBOT):
        with tempfile.TemporaryDirectory() as folder:
            for name, text in (("r_macro.xacro", macro), ("r.xacro", robot)):
                with open(os.path.join(folder, name), "w", encoding="utf-8") as stream:
                    stream.write(text)
            path = os.path.join(folder, "r.xacro")
            self.assertEqual(cx.macro_file_of(path), os.path.join(folder, "r_macro.xacro"))
            return cx.default_macro_expansion(path)

    def test_defaults_give_the_plain_robot(self):
        differences, _ = cx.compare_robots(cx.ET.fromstring(BASE), self.expand(MACRO % body()))
        self.assertEqual(differences, [])

    def test_a_changed_value_is_found(self):
        differences, _ = cx.compare_robots(cx.ET.fromstring(BASE),
                                           self.expand(MACRO % body(replaced('<mass value="1.5"/>', '<mass value="1.6"/>'))))
        self.assertTrue(differences)

    def test_dollars_are_literal(self):
        macro = MACRO % body().replace("base_link.STL", "a$$b.STL")
        meshes = [m.get("filename") for m in self.expand(macro).iter("mesh")]
        self.assertEqual(meshes, ["package://r/meshes/a$b.STL"] * 2)

    def test_other_layouts_are_refused(self):
        for macro, robot in ((MACRO % body(), ROBOT.replace("<xacro:r/>", '<xacro:r prefix="x_"/>')),
                             (MACRO % body(), ROBOT.replace("<xacro:r/>", "<xacro:other/>")),
                             (MACRO.replace("prefix:=''", "prefix") % body(), ROBOT),
                             (MACRO % body().replace("1.5", "${m}"), ROBOT)):
            with self.assertRaises(ValueError):
                self.expand(macro, robot)

    def test_a_plain_xacro_has_no_macro_file(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "r.xacro")
            with open(path, "w", encoding="utf-8") as stream:
                stream.write(BASE)
            self.assertIsNone(cx.macro_file_of(path))


if __name__ == "__main__":
    unittest.main(verbosity=2)
