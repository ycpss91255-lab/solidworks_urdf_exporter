"""Builds the small models of the export form check (scripts/test_form_e2e.py).

Parts (all on the front plane, so part Z is the extrusion direction; SI units, 1000 kg/m3):
  LIM_BASE.SLDPRT   100 x 100 x 20 mm plate, boss of 10 mm diameter up to z = 40 mm on the part origin
  LIM_ARM.SLDPRT    120 x 40 x 12 mm arm (x -20..100), hole of 14 mm diameter on the part origin
  FORM_BLOCK.SLDPRT L shaped block without any symmetry plane, so that its inertia tensor has no zero in a
                    frame that is turned against the part
Assemblies, not mated yet (FormE2eProbe.cs "models" adds the mates):
  LIM_HINGE.SLDASM  base, arm
  LIM_ORIGIN.SLDASM base, arm; once mated, the arm lies exactly on the origin of the assembly and is not turned
  LIM_SLIDE.SLDASM  base, arm
  LIM_FREE.SLDASM   base, arm (a slide without a limit mate)
  FORM_MULTI.SLDASM base, arm (hinge), second base, second arm (slide), block that is turned in the assembly
  FORM_CHILD2.SLDASM base, arm (hinge), block that is locked to the arm
  FORM_SUB.SLDASM   base, FORM_ARM_SUB.SLDASM with the arm in it (hinge)
  FORM_FULL.SLDASM  base, arm; once mated, the arm is fully defined on the origin of the assembly
  FORM_LOOSE.SLDASM base, arm without a mate on the origin of the assembly
  FORM_PLANE.SLDASM base, arm; once mated, the arm on the origin of the assembly moves in a plane
  FORM_CYL.SLDASM   base, arm that can turn and slide, an axis with a Chinese name
  FORM_TEXTURE.SLDASM base, FORM_TEX.SLDPRT (the arm with an image texture), hinge
  FORM_NEST.SLDASM  two bases, two FORM_WHEEL_SUB.SLDASM with FORM_WHEEL.SLDPRT (the arm with an axis and a frame)

The script binds to ONE SolidWorks process that you name with --pid (through the Running Object Table entry
SolidWorks_PID_<pid>) and never starts or closes SolidWorks. Existing files with these names in the output folder
are replaced. Requires pywin32.

Usage:  python scripts/make_form_fixtures.py --pid <pid> --output <folder>
"""
import argparse
import os
import sys

import make_test_fixtures as f

MM = 0.001


def extrude(model, height, draw):
    """Sketch on the front plane (draw(sketch_manager)) extruded by height (m) along +Z, merged."""
    plane = f.first_reference_plane(model)
    model.ClearSelection2(True)
    plane.Select2(False, 0)
    sketch_manager = model.SketchManager
    sketch_manager.InsertSketch(True)
    sketch_manager.AddToDB = True    # no snapping to what is near
    draw(sketch_manager)
    sketch_manager.AddToDB = False
    sketch_manager.InsertSketch(True)
    feature = model.FeatureManager.FeatureExtrusion3(
        True, False, False, 0, 0, height, 0.0, False, False, False, False, 0.0, 0.0,
        False, False, False, False, True, True, True, 0, 0.0, False)
    if feature is None:
        raise RuntimeError("Extrusion failed")
    model.ClearSelection2(True)


def rectangle(x1, y1, x2, y2):
    return lambda s: s.CreateCornerRectangle(x1 * MM, y1 * MM, 0, x2 * MM, y2 * MM, 0)


def part(app, color, steps):
    model = f.new_document(app, f.SW_DEFAULT_TEMPLATE_PART)
    for height, draw in steps:
        extrude(model, height * MM, draw)
    import pythoncom
    from win32com.client import VARIANT
    model.MaterialPropertyValues = VARIANT(
        pythoncom.VT_ARRAY | pythoncom.VT_R8, (color[0], color[1], color[2], 1.0, 1.0, 0.0, 0.0, 0.0, 0.0))
    f.rename_active_configuration(model, "Default")
    model.ForceRebuild3(False)
    return model


def base(app):
    return part(app, f.RED, [(20, rectangle(-50, -50, 50, 50)),
                             (40, lambda s: s.CreateCircleByRadius(0, 0, 0, 5 * MM))])


def arm(app):
    def draw(sketch):
        rectangle(-20, -20, 100, 20)(sketch)
        sketch.CreateCircleByRadius(0, 0, 0, 7 * MM)
    return part(app, f.BLUE, [(12, draw)])


def wheel(app):
    """The arm with an axis and a coordinate system of its own, with names in Chinese."""
    model = arm(app)
    planes, feature = [], model.FirstFeature
    while feature is not None and len(planes) < 3:
        if feature.GetTypeName2 == "RefPlane":
            planes.append(feature)
        feature = feature.GetNextFeature
    model.ClearSelection2(True)
    planes[1].Select2(False, 0)
    planes[2].Select2(True, 0)
    if not model.InsertAxis2(True):
        raise RuntimeError("could not create the axis")
    feature, axis = model.FirstFeature, None
    while feature is not None:
        axis = feature if feature.GetTypeName2 == "RefAxis" else axis
        feature = feature.GetNextFeature
    axis.Name = "\u65cb\u8f49\u8ef8"
    model.ClearSelection2(True)
    frame = model.FeatureManager.CreateCoordinateSystemUsingNumericalValues(True, 0, 0, 0.005, True, 0.3, -0.2, 0.5)
    if frame is None:
        raise RuntimeError("could not create the coordinate system")
    frame.Name = "\u5ea7\u6a19_1"
    model.ClearSelection2(True)
    return model


def textured(app):
    """The arm with an appearance that has an image texture, which the OBJ export refuses."""
    import pythoncom
    from win32com.client import VARIANT
    model = arm(app)
    carpet = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "SOLIDWORKS Corp", "SOLIDWORKS",
                          "data", "graphics", "Materials", "fabric", "carpet", "carpet color1 2d.p2m")
    if not os.path.isfile(carpet):
        raise RuntimeError("texture appearance not found in this SolidWorks installation: " + carpet)
    material = model.Extension.CreateRenderMaterial(carpet)
    if not material.AddEntity(model) or not model.Extension.AddRenderMaterial(material, VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)):
        raise RuntimeError("could not apply the texture appearance")
    return model


def block(app):
    return part(app, (0.0, 1.0, 0.0), [(10, rectangle(0, 0, 60, 20)), (35, rectangle(0, 20, 15, 50)),
                                       (25, rectangle(45, -12, 60, 0))])


def assembly(app, folder, name, components):
    model = f.new_document(app, f.SW_DEFAULT_TEMPLATE_ASSEMBLY)
    for file, x, y, z in components:
        if model.AddComponent5(os.path.join(folder, file), 0, "", False, "", x * MM, y * MM, z * MM) is None:
            raise RuntimeError("Could not add %s to %s" % (file, name))
    f.rename_active_configuration(model, "Default")
    model.ForceRebuild3(False)
    f.save(model, os.path.join(folder, name))
    return model.GetTitle


def build(app, folder):
    os.makedirs(folder, exist_ok=True)
    titles = []   # closed in reverse order: assemblies before their parts
    try:
        for name, make in (("LIM_BASE.SLDPRT", base), ("LIM_ARM.SLDPRT", arm), ("FORM_BLOCK.SLDPRT", block),
                           ("FORM_WHEEL.SLDPRT", wheel), ("FORM_TEX.SLDPRT", textured)):
            model = make(app)
            f.save(model, os.path.join(folder, name))
            titles.append(model.GetTitle)
        pair = [("LIM_BASE.SLDPRT", 30, -20, 45), ("LIM_ARM.SLDPRT", 70, 10, 90)]
        # everything stays open until the end: a component can only be added from an open document
        titles.append(assembly(app, folder, "LIM_HINGE.SLDASM", pair))
        titles.append(assembly(app, folder, "LIM_ORIGIN.SLDASM", [("LIM_BASE.SLDPRT", 0, 0, 0), ("LIM_ARM.SLDPRT", 40, 10, 60)]))
        titles.append(assembly(app, folder, "LIM_SLIDE.SLDASM", pair))
        titles.append(assembly(app, folder, "LIM_FREE.SLDASM", pair))
        titles.append(assembly(app, folder, "FORM_MULTI.SLDASM", pair + [
            ("LIM_BASE.SLDPRT", 330, -20, 45), ("LIM_ARM.SLDPRT", 370, 10, 90), ("FORM_BLOCK.SLDPRT", 150, 130, 90)]))
        titles.append(assembly(app, folder, "FORM_TEXTURE.SLDASM", [("LIM_BASE.SLDPRT", 30, -20, 45), ("FORM_TEX.SLDPRT", 70, 10, 90)]))
        titles.append(assembly(app, folder, "FORM_CHILD2.SLDASM", pair + [("FORM_BLOCK.SLDPRT", 250, 60, 120)]))
        titles.append(assembly(app, folder, "FORM_ARM_SUB.SLDASM", [("LIM_ARM.SLDPRT", 0, 0, 0)]))
        titles.append(assembly(app, folder, "FORM_SUB.SLDASM", [("LIM_BASE.SLDPRT", 30, -20, 45), ("FORM_ARM_SUB.SLDASM", 70, 10, 90)]))
        titles.append(assembly(app, folder, "FORM_WHEEL_SUB.SLDASM", [("FORM_WHEEL.SLDPRT", 15, -8, 4)]))
        titles.append(assembly(app, folder, "FORM_NEST.SLDASM", [("LIM_BASE.SLDPRT", 30, -20, 45), ("LIM_BASE.SLDPRT", 330, -20, 45),
                                                                 ("FORM_WHEEL_SUB.SLDASM", 70, 10, 90), ("FORM_WHEEL_SUB.SLDASM", 370, 10, 90)]))
        for name in ("FORM_FULL.SLDASM", "FORM_LOOSE.SLDASM", "FORM_PLANE.SLDASM", "FORM_CYL.SLDASM"):
            titles.append(assembly(app, folder, name, pair))
    finally:
        for title in reversed(titles):
            app.CloseDoc(title)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pid", type=int, required=True, help="process id of the SolidWorks instance to use")
    parser.add_argument("--output", required=True, help="output folder")
    args = parser.parse_args()
    output = os.path.abspath(args.output)
    build(f.bind(args.pid), output)
    print("Wrote " + ", ".join(sorted(os.listdir(output))))


if __name__ == "__main__":
    sys.exit(main())
