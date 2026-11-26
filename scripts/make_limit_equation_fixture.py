"""Builds the minimal LIMIT_EQUATIONS model used by scripts/test_limit_equations_live.py (issue 170).

Creates, in --output:
  LEQ_BASE.SLDPRT    100 x 100 x 20 mm plate, with a reference axis AXIS_Z (the part Z axis)
  LEQ_HINGE.SLDPRT   60 x 10 x 5 mm arm, with a reference axis AXIS_Z
  LEQ_SLIDE.SLDPRT   20 x 20 x 10 mm block
  LIMIT_EQUATIONS.SLDASM
      the base is fixed;
      hinge: front planes coincident + AXIS_Z coincident + angle limit mate HINGE_LIMIT (right planes);
      slide: top planes coincident + right planes coincident + distance limit mate SLIDE_LIMIT (front planes);
      global variables (document units: mm and degrees), different per configuration:
          "base_angle" = 90 in CFG_A, 120 in CFG_B
          "base_dist"  = 10 in CFG_A, 20 in CFG_B
      equations   "D1@HINGE_LIMIT" = "base_angle"   (the angle of the limit mate)
                  "D1@SLIDE_LIMIT" = "base_dist"    (the distance of the limit mate)
      typed limits: HINGE_LIMIT 60 .. 180 degrees, SLIDE_LIMIT 5 .. 30 mm.
      configurations CFG_A (active when saved) and CFG_B.

The script binds to ONE SolidWorks process that you name with --pid (through the Running Object Table entry
SolidWorks_PID_<pid>, never through a generic ProgID) and never starts or closes SolidWorks. Existing files with
these names in the output folder are replaced. Nothing else is opened or saved. Requires pywin32.

Why the maximum and minimum are typed: a limit mate has one dimension only (D1, its value). SOLIDWORKS rejects
equations for the maximum and minimum ("D2@HINGE_LIMIT", "$UPPERLIMIT_ANGLE@HINGE_LIMIT", ...), and changing them
through the feature data applies to all configurations. Only a design table can vary them per configuration.

Usage:  python scripts/make_limit_equation_fixture.py --pid <pid> --output <folder>
"""
import argparse
import os
import sys

import pythoncom
from win32com.client import VARIANT

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from make_test_fixtures import (  # noqa: E402
    SW_DEFAULT_TEMPLATE_ASSEMBLY, SW_DEFAULT_TEMPLATE_PART, bind, first_reference_plane, new_document, null_dispatch, prop,
    rename_active_configuration, save)

MM = 0.001
DEG = 3.141592653589793 / 180.0
BASE, HINGE, SLIDE = "LEQ_BASE", "LEQ_HINGE", "LEQ_SLIDE"
ASSEMBLY = "LIMIT_EQUATIONS"
CONFIG_A, CONFIG_B = "CFG_A", "CFG_B"
HINGE_MATE, SLIDE_MATE = "HINGE_LIMIT", "SLIDE_LIMIT"
AXIS = "AXIS_Z"

# Values of the global variables per configuration (degrees and millimetres).
VARIABLES = {
    CONFIG_A: {"base_angle": 90, "base_dist": 10},
    CONFIG_B: {"base_angle": 120, "base_dist": 20},
}
# Typed limits (degrees and millimetres).
HINGE_MIN, HINGE_MAX = 60, 180
SLIDE_MIN, SLIDE_MAX = 5, 30

# swMateType_e / swMateAlign_e
SW_MATE_COINCIDENT, SW_MATE_DISTANCE, SW_MATE_ANGLE = 0, 5, 6
SW_ALIGN_CLOSEST = 2
# swInConfigurationOpts_e
SW_ALL_CONFIGURATIONS, SW_SPECIFY_CONFIGURATIONS = 2, 3
# swUserPreferenceIntegerValue_e.swUnitSystem / swUnitSystem_e.swUnitSystem_MMGS
SW_UNIT_SYSTEM, SW_UNIT_SYSTEM_MMGS = 465, 2


def reference_planes(model):
    """Front, top and right plane, in tree order, whatever the UI language."""
    planes = []
    feature = first_reference_plane(model)
    while feature is not None and len(planes) < 3:
        if feature.GetTypeName2 == "RefPlane":
            planes.append(feature)
        feature = feature.GetNextFeature
    if len(planes) != 3:
        raise RuntimeError("Expected three reference planes")
    return planes


def last_feature(model):
    feature, last = model.FirstFeature, None
    while feature is not None:
        last, feature = feature, feature.GetNextFeature
    return last


def make_block(app, x0, y0, x1, y1, height, with_axis):
    model = new_document(app, SW_DEFAULT_TEMPLATE_PART)
    model.Extension.SetUserPreferenceInteger(SW_UNIT_SYSTEM, 0, SW_UNIT_SYSTEM_MMGS)
    front, top, right = reference_planes(model)
    model.ClearSelection2(True)
    front.Select2(False, 0)
    sketch_manager = model.SketchManager
    sketch_manager.InsertSketch(True)
    sketch_manager.CreateCornerRectangle(x0, y0, 0, x1, y1, 0)
    sketch_manager.InsertSketch(True)
    extrusion = model.FeatureManager.FeatureExtrusion3(
        True, False, False, 0, 0, height, 0.0, False, False, False, False, 0.0, 0.0,
        False, False, False, False, True, True, True, 0, 0.0, False)
    if extrusion is None:
        raise RuntimeError("Extrusion failed")
    if with_axis:
        model.ClearSelection2(True)
        top.Select2(False, 0)
        right.Select2(True, 0)
        if not model.InsertAxis2(True):
            raise RuntimeError("Could not insert the reference axis")
        axis = last_feature(model)
        if axis.GetTypeName2 != "RefAxis":
            raise RuntimeError("The last feature is %s, not the new axis" % axis.GetTypeName2)
        axis.Name = AXIS
    model.ClearSelection2(True)
    rename_active_configuration(model, "Default")
    model.ForceRebuild3(False)
    return model


def select_in_component(assembly, component, feature_name, append):
    feature = component.FeatureByName(feature_name)
    if feature is None:
        raise RuntimeError("%s has no feature %s" % (component.Name2, feature_name))
    if not feature.Select2(append, 0):
        raise RuntimeError("Could not select %s of %s" % (feature_name, component.Name2))


def add_mate(assembly, pairs, mate_type, align=SW_ALIGN_CLOSEST, distance=0.0, distance_max=0.0, distance_min=0.0,
             angle=0.0, angle_max=0.0, angle_min=0.0, name=None):
    assembly.ClearSelection2(True)
    for index, (component, feature_name) in enumerate(pairs):
        select_in_component(assembly, component, feature_name, index > 0)
    error = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    mate = assembly.AddMate5(mate_type, align, False, distance, distance_max, distance_min, 0.0, 0.0,
                             angle, angle_max, angle_min, False, False, 0, error)
    if mate is None or error.value != 1:  # swAddMateError_NoError = 1
        raise RuntimeError("Mate %s failed (error %s)" % (name or mate_type, error.value))
    assembly.ClearSelection2(True)
    if name:
        mate.Name = name
    return mate


def add_equations(assembly):
    """Global variables (one value per configuration) and the equations that drive the limit mate values."""
    manager = assembly.GetEquationMgr
    for variable, value in VARIABLES[CONFIG_A].items():
        add_equation(manager, '"%s" = %s' % (variable, value))
    add_equation(manager, '"D1@%s" = "base_angle"' % HINGE_MATE)
    add_equation(manager, '"D1@%s" = "base_dist"' % SLIDE_MATE)
    set_variables(assembly, CONFIG_B, VARIABLES[CONFIG_B])


def add_equation(manager, text):
    if manager.Add3(prop(manager, "GetCount"), text, True, SW_ALL_CONFIGURATIONS, null_array()) < 0:
        raise RuntimeError("SOLIDWORKS rejected the equation " + text)


def null_array():
    return VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_BSTR, [])


def set_variables(assembly, configuration, values):
    """Sets global variables in one configuration only (it is activated), then evaluates and rebuilds.

    The equation manager must be fetched again after the configuration changed."""
    assembly.ShowConfiguration2(configuration)
    manager = assembly.GetEquationMgr
    names = VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_BSTR, [configuration])
    for variable, value in values.items():
        index = variable_index(manager, variable)
        status = manager.SetEquationAndConfigurationOption(
            index, '"%s" = %s' % (variable, value), SW_SPECIFY_CONFIGURATIONS, names)
        if status < 0:  # the index of the changed equation, or -1
            raise RuntimeError("Setting %s in %s failed (%s)" % (variable, configuration, status))
    prop(manager, "EvaluateAll")
    assembly.ForceRebuild3(False)


def variable_index(manager, variable):
    for index in range(prop(manager, "GetCount")):
        if manager.GlobalVariable(index) and manager.Equation(index).replace(" ", "").startswith('"%s"=' % variable):
            return index
    raise RuntimeError("No global variable " + variable)


def add_component(app, assembly, path, x, y, z):
    """AddComponent5 now and then returns nothing right after the assembly was created; activate it and retry."""
    for _ in range(3):
        component = assembly.AddComponent5(path, 0, "", False, "", x, y, z)
        if component is not None:
            return component
        app.ActivateDoc3(assembly.GetTitle, False, 0, VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0))
    raise RuntimeError("Could not add the component " + path)


def build(app, output):
    output = os.path.abspath(output)
    os.makedirs(output, exist_ok=True)
    paths = {name: os.path.join(output, name + ".SLDPRT") for name in (BASE, HINGE, SLIDE)}
    assembly_path = os.path.join(output, ASSEMBLY + ".SLDASM")
    titles, planes = [], {}
    try:
        for name, (corner, height, axis) in {
                BASE: ((-50, -50, 50, 50), 20, True),
                HINGE: ((0, -5, 60, 5), 5, True),
                SLIDE: ((-10, -10, 10, 10), 10, False)}.items():
            model = make_block(app, *[c * MM for c in corner], height=height * MM, with_axis=axis)
            save(model, paths[name])
            titles.append(model.GetTitle)
            # the plane names depend on the UI language
            planes[name] = [plane.Name for plane in reference_planes(model)]

        assembly = new_document(app, SW_DEFAULT_TEMPLATE_ASSEMBLY)
        titles.append(assembly.GetTitle)
        assembly.Extension.SetUserPreferenceInteger(SW_UNIT_SYSTEM, 0, SW_UNIT_SYSTEM_MMGS)
        base = add_component(app, assembly, paths[BASE], 0.0, 0.0, 0.0)
        hinge = add_component(app, assembly, paths[HINGE], 0.0, 0.0, 0.05)
        slide = add_component(app, assembly, paths[SLIDE], 0.2, 0.0, 0.0)
        assembly.ClearSelection2(True)
        base.Select4(False, null_dispatch(), False)
        assembly.FixComponent()
        rename_active_configuration(assembly, CONFIG_A)

        front, top, right = 0, 1, 2
        add_mate(assembly, [(base, planes[BASE][front]), (hinge, planes[HINGE][front])], SW_MATE_COINCIDENT)
        add_mate(assembly, [(base, AXIS), (hinge, AXIS)], SW_MATE_COINCIDENT)
        a = VARIABLES[CONFIG_A]["base_angle"]
        add_mate(assembly, [(base, planes[BASE][right]), (hinge, planes[HINGE][right])], SW_MATE_ANGLE,
                 angle=a * DEG, angle_max=HINGE_MAX * DEG, angle_min=HINGE_MIN * DEG, name=HINGE_MATE)
        add_mate(assembly, [(base, planes[BASE][top]), (slide, planes[SLIDE][top])], SW_MATE_COINCIDENT)
        add_mate(assembly, [(base, planes[BASE][right]), (slide, planes[SLIDE][right])], SW_MATE_COINCIDENT)
        d = VARIABLES[CONFIG_A]["base_dist"]
        add_mate(assembly, [(base, planes[BASE][front]), (slide, planes[SLIDE][front])], SW_MATE_DISTANCE,
                 distance=d * MM, distance_max=SLIDE_MAX * MM, distance_min=SLIDE_MIN * MM, name=SLIDE_MATE)
        assembly.ForceRebuild3(False)

        if assembly.AddConfiguration3(CONFIG_B, "", "", 0) is None:
            raise RuntimeError("Could not add configuration " + CONFIG_B)
        add_equations(assembly)
        assembly.ShowConfiguration2(CONFIG_A)
        assembly.ForceRebuild3(False)
        save(assembly, assembly_path)
        titles[-1] = assembly.GetTitle
    finally:
        for title in reversed(titles):
            app.CloseDoc(title)
    return assembly_path


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pid", type=int, required=True, help="process id of the SolidWorks instance to use")
    parser.add_argument("--output", required=True, help="output folder")
    args = parser.parse_args()
    output = os.path.abspath(args.output)
    os.makedirs(output, exist_ok=True)
    print("Wrote " + build(bind(args.pid), output))


if __name__ == "__main__":
    sys.exit(main())
