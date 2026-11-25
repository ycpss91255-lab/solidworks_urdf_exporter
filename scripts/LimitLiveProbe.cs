// Live host that checks the joint axis and the <limit> values the exporter writes for a limit mate against
// the motion SolidWorks really allows. It runs the real export code of a private SW2URDF.dll against ONE
// SolidWorks process named by its Running Object Table entry (SolidWorks_PID_<pid>), on scratch copies of the
// three files LIM_BASE.SLDPRT, LIM_ARM.SLDPRT and LIM_HINGE.SLDASM (an unmated base plate with a boss and an arm
// with a hole, see TESTING.md). For each variant it
//   1. builds the mates in the scratch copy (hinge = concentric + coincident, or slide = concentric +
//      perpendicular) plus one limit mate (angle or distance),
//   2. measures the truth: drives the limit mate's dimension to its limits and measures how far the arm really
//      turns about (or slides along) the world Z axis, relative to the pose the model is exported in,
//   3. builds a two-link tree, creates the robot and exports it to a scratch folder, parses the written URDF,
//   4. converts the exported axis to the world frame and compares range and direction with the truth.
// It never saves anything except the scratch copies and never changes the visibility of SolidWorks.
// Plain C# 5 so that the .NET Framework csc.exe can compile it.
//
// scripts/FormE2eProbe.cs uses the internal members for the models of the export form check.
//
// Usage: LimitLiveProbe.exe <pid> <folder with the three unmated files> <out dir> [variant ...]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Xml;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.Utilities;

public static class LimitLiveProbe
{
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx context);

    internal sealed class Variant
    {
        public string Name, Description;
        public bool Slide;           // distance limit mate on a slide, else angle limit mate on a hinge
        public double Reference;     // reference angle (deg) or distance (mm) the limit mate is created with
        public double Lower, Upper;  // absolute limits in the same unit
        public swMateAlign_e Align = swMateAlign_e.swMateAlignCLOSEST;
        public swMateAlign_e ConcAlign = swMateAlign_e.swMateAlignALIGNED; // alignment of the concentric mate
        public bool BaseFlip;        // the base is turned by 180 degrees about X before it is fixed
        public bool Equation;        // an equation sets the value of the limit mate dimension (the exporter must not move it)
        public bool Flip;            // the Flip argument of AddMate4
        public bool SwapOrder;       // select the arm entity before the base entity
        public double PoseValue = double.NaN; // dimension value to export in (deg or mm); NaN = the reference
    }

    static readonly Variant[] Variants = new[]
    {
        new Variant { Name = "a_asym_60_180", Description = "hinge, ref 90, limits 60..180", Reference = 90, Lower = 60, Upper = 180 },
        new Variant { Name = "b_sym_0_180", Description = "hinge, ref 90, limits 0..180", Reference = 90, Lower = 0, Upper = 180 },
        new Variant { Name = "c1_flip_arg", Description = "hinge, ref 90, limits 60..180, Flip=true", Reference = 90, Lower = 60, Upper = 180, Flip = true },
        new Variant { Name = "c2_anti_aligned", Description = "hinge, ref 90, limits 60..180, anti-aligned", Reference = 90, Lower = 60, Upper = 180, Align = swMateAlign_e.swMateAlignANTI_ALIGNED },
        new Variant { Name = "c3_swapped_order", Description = "hinge, ref 90, limits 60..180, entities picked in the other order", Reference = 90, Lower = 60, Upper = 180, SwapOrder = true },
        new Variant { Name = "d_slide_distance", Description = "slide, distance ref 10 mm, limits 0..30 mm", Slide = true, Reference = 10, Lower = 0, Upper = 30 },
        new Variant { Name = "d2_slide_flip_arg", Description = "slide, distance ref 10 mm, limits 0..30 mm, Flip=true", Slide = true, Reference = 10, Lower = 0, Upper = 30, Flip = true },
        new Variant { Name = "d4_slide_anti", Description = "slide, distance ref 10 mm, limits 0..30 mm, anti-aligned", Slide = true, Reference = 10, Lower = 0, Upper = 30, Align = swMateAlign_e.swMateAlignANTI_ALIGNED },
        new Variant { Name = "d5_slide_swapped", Description = "slide, distance ref 10 mm, limits 0..30 mm, entities picked in the other order", Slide = true, Reference = 10, Lower = 0, Upper = 30, SwapOrder = true },
        new Variant { Name = "e_posed_120", Description = "hinge, ref 90, limits 60..180, exported at 120 deg", Reference = 90, Lower = 60, Upper = 180, PoseValue = 120 },
        new Variant { Name = "f_slide_posed", Description = "slide, distance ref 10 mm, limits 0..30 mm, exported at 20 mm", Slide = true, Reference = 10, Lower = 0, Upper = 30, PoseValue = 20 },
        new Variant { Name = "g1_hinge_conc_anti", Description = "hinge, 60..180, concentric mate anti-aligned", Reference = 90, Lower = 60, Upper = 180, ConcAlign = swMateAlign_e.swMateAlignANTI_ALIGNED },
        new Variant { Name = "g2_hinge_base_turned", Description = "hinge, 60..180, base turned by 180 degrees about X", Reference = 90, Lower = 60, Upper = 180, BaseFlip = true },
        new Variant { Name = "h1_slide_conc_anti", Description = "slide, distance 0..30 mm, concentric mate anti-aligned", Slide = true, Reference = 10, Lower = 0, Upper = 30, ConcAlign = swMateAlign_e.swMateAlignANTI_ALIGNED },
        new Variant { Name = "h2_slide_base_turned", Description = "slide, distance 0..30 mm, base turned by 180 degrees about X", Slide = true, Reference = 10, Lower = 0, Upper = 30, BaseFlip = true },
        new Variant { Name = "k1_hinge_equation", Description = "hinge, 60..180, the dimension is set by an equation", Reference = 90, Lower = 60, Upper = 180, Equation = true },
        new Variant { Name = "k2_slide_equation", Description = "slide, distance 0..30 mm, the dimension is set by an equation", Slide = true, Reference = 10, Lower = 0, Upper = 30, Equation = true },
    };

    static readonly List<string> rows = new List<string>();
    internal static readonly List<string> messages = new List<string>();
    internal static SldWorks app;
    internal static IMathUtility math;
    internal static ModelDoc2 doc;
    static string outDir;
    const double Deg = Math.PI / 180.0;

    static string Num(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    internal static void Say(string text) { Console.WriteLine(text); rows.Add(text); }

    static SldWorks Bind(int pid)
    {
        IRunningObjectTable rot; IBindCtx context;
        GetRunningObjectTable(0, out rot); CreateBindCtx(0, out context);
        IEnumMoniker iterator; rot.EnumRunning(out iterator);
        var next = new IMoniker[1];
        while (iterator.Next(1, next, IntPtr.Zero) == 0)
        {
            string name; next[0].GetDisplayName(context, null, out name);
            if (name != "SolidWorks_PID_" + pid) continue;
            object value; rot.GetObject(next[0], out value);
            var found = (SldWorks)value;
            if (found.GetProcessID() != pid) throw new Exception("process id mismatch");
            return found;
        }
        throw new Exception("SolidWorks_PID_" + pid + " is not in the Running Object Table");
    }

    internal static readonly List<string> questions = new List<string>();
    internal static readonly List<string> confirmations = new List<string>();
    internal static System.Windows.MessageBoxResult Answer = System.Windows.MessageBoxResult.No;

    internal sealed class RecordingMessages : IMessageBox
    {
        public System.Windows.MessageBoxResult Show(string message) { messages.Add(message); return System.Windows.MessageBoxResult.OK; }
        public System.Windows.MessageBoxResult Show(string message, string caption, System.Windows.MessageBoxButton buttons)
        {
            Show(message);
            if (buttons == System.Windows.MessageBoxButton.OK) return System.Windows.MessageBoxResult.OK;
            if (buttons == System.Windows.MessageBoxButton.YesNo)
            {
                // like the dialogs of the form: answered with Yes
                confirmations.Add("[" + caption + "] " + message);
                return System.Windows.MessageBoxResult.Yes;
            }
            questions.Add(message);
            return Answer;
        }
    }

    internal static System.Collections.IList ComponentList(Link link)
    {
        return (System.Collections.IList)typeof(Link).GetField("SWComponents").GetValue(link);
    }

    // --- mates --------------------------------------------------------------------------------------------

    // A component of the assembly, or of one of its sub-assemblies (then the name is a path, SUB-1/PART-1)
    internal static Component2 Find(string component)
    {
        Component2 c = ((AssemblyDoc)doc).GetComponentByName(component);
        if (c == null)
            c = ((object[])((AssemblyDoc)doc).GetComponents(false)).Cast<Component2>().FirstOrDefault(x => x.Name2 == component);
        if (c == null) throw new Exception("component not found: " + component);
        return c;
    }

    static void SelectFace(string component, bool append, Func<Face2, double[], Surface, bool> test, string what)
    {
        Component2 c = Find(component);
        object info;
        object[] bodies = (object[])c.GetBodies3((int)swBodyType_e.swSolidBody, out info);
        foreach (object bo in bodies)
        {
            Face2 f = (Face2)((Body2)bo).GetFirstFace();
            while (f != null)
            {
                Surface s = (Surface)f.GetSurface();
                double[] box = (double[])f.GetBox();
                if (test(f, box, s))
                {
                    SelectData sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData();
                    sd.Mark = 1;
                    if (!((Entity)f).Select4(append, sd)) throw new Exception("Select4 failed for " + what);
                    return;
                }
                f = (Face2)f.GetNextFace();
            }
        }
        throw new Exception("face not found: " + what);
    }

    static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-6; }

    internal static Mate2 AddMate(swMateType_e type, swMateAlign_e align, bool flip, double distance, double distUp, double distLow,
        double angle, double angUp, double angLow, string what)
    {
        int error;
        object raw = ((AssemblyDoc)doc).AddMate4((int)type, (int)align, flip, distance, distUp, distLow, 0, 0, angle, angUp, angLow, false, false, out error);
        Say("  mate " + what + ": " + (raw != null ? "ok" : "FAILED") + " (error " + error + ")");
        doc.ClearSelection2(true);
        if (raw == null) throw new Exception("mate failed: " + what);
        return raw as Mate2;
    }

    internal static string BaseName = "LIM_BASE-1", ArmName = "LIM_ARM-1";

    static void SelectPair(bool swap, Func<Face2, double[], Surface, bool> baseTest, string baseWhat, Func<Face2, double[], Surface, bool> armTest, string armWhat)
    {
        if (!swap) { SelectFace(BaseName, false, baseTest, baseWhat); SelectFace(ArmName, true, armTest, armWhat); }
        else { SelectFace(ArmName, false, armTest, armWhat); SelectFace(BaseName, true, baseTest, baseWhat); }
    }

    internal static Feature BuildMates(Variant v)
    {
        foreach (object o in (object[])((AssemblyDoc)doc).GetComponents(true))
        {
            string n = ((Component2)o).Name2;
            Say("  component " + n);
            if (n.IndexOf("BASE", StringComparison.OrdinalIgnoreCase) >= 0) BaseName = n;
            if (n.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0) ArmName = n;
        }
        return MatePair(v);
    }

    internal static void SelectCylinder(string component)
    {
        doc.ClearSelection2(true);
        SelectFace(component, false, (f, b, s) => s.IsCylinder(), "cylinder of " + component);
    }

    // The arm on the boss of the base, free to turn and to slide
    internal static void Concentric(string baseName, string armName)
    {
        SelectCylinder(baseName);
        SelectFace(armName, true, (f, b, s) => s.IsCylinder(), "arm hole cylinder");
        AddMate(swMateType_e.swMateCONCENTRIC, swMateAlign_e.swMateAlignALIGNED, false, 0, 0, 0, 0, 0, 0, "concentric boss/hole");
        doc.ForceRebuild3(false);
    }

    // The arm on the top of the base, free to slide in two directions and to turn
    internal static void Coincident(string baseName, string armName)
    {
        doc.ClearSelection2(true);
        SelectFace(baseName, false, (f, b, s) => s.IsPlane() && Near(b[2], 0.02) && Near(b[5], 0.02) && b[3] - b[0] > 0.09, "base top plane");
        SelectFace(armName, true, (f, b, s) => s.IsPlane() && Near(b[2], 0) && Near(b[5], 0), "arm bottom plane");
        AddMate(swMateType_e.swMateCOINCIDENT, swMateAlign_e.swMateAlignCLOSEST, false, 0, 0, 0, 0, 0, 0, "coincident faces");
        doc.ForceRebuild3(false);
    }

    // The mates of one base (BaseName) and one arm (ArmName), in a model that may hold more components
    internal static Feature MatePair(Variant v)
    {
        doc.ClearSelection2(true);
        if (!doc.Extension.SelectByID2(BaseName + "@" + System.IO.Path.GetFileNameWithoutExtension(doc.GetPathName()), "COMPONENT", 0, 0, 0, false, 0, null, 0))
            throw new Exception("base component not found");
        if (v.BaseFlip)
        {
            // turn the base by 180 degrees about X before it is fixed, so that the boss points the other way
            Component2 baseComponent = ((AssemblyDoc)doc).GetComponentByName(BaseName);
            double[] t = (double[])baseComponent.Transform2.ArrayData;
            baseComponent.Transform2 = (MathTransform)math.CreateTransform(new double[] { 1, 0, 0, 0, -1, 0, 0, 0, -1, t[9], t[10], t[11], 1, 0, 0, 0 });
            doc.ForceRebuild3(false);
            doc.ClearSelection2(true);
            doc.Extension.SelectByID2(BaseName + "@" + System.IO.Path.GetFileNameWithoutExtension(doc.GetPathName()), "COMPONENT", 0, 0, 0, false, 0, null, 0);
        }
        ((AssemblyDoc)doc).FixComponent();
        doc.ClearSelection2(true);

        SelectFace(BaseName, false, (f, b, s) => s.IsCylinder(), "base boss cylinder");
        SelectFace(ArmName, true, (f, b, s) => s.IsCylinder(), "arm hole cylinder");
        AddMate(swMateType_e.swMateCONCENTRIC, v.ConcAlign, false, 0, 0, 0, 0, 0, 0, "concentric boss/hole");

        Func<Face2, double[], Surface, bool> baseTop = (f, b, s) => s.IsPlane() && Near(b[2], 0.02) && Near(b[5], 0.02) && b[3] - b[0] > 0.09;
        Func<Face2, double[], Surface, bool> armTop = (f, b, s) => s.IsPlane() && Near(b[2], 0) && Near(b[5], 0); // the arm face at z = 0 of the part: it ends up on top of the base
        Func<Face2, double[], Surface, bool> baseSide = (f, b, s) => s.IsPlane() && Near(b[0], 0.05) && Near(b[3], 0.05);
        Func<Face2, double[], Surface, bool> armSide = (f, b, s) => s.IsPlane() && Near(b[1], -0.02) && Near(b[4], -0.02);

        Mate2 limit;
        if (!v.Slide)
        {
            SelectPair(false, baseTop, "base top plane", armTop, "arm bottom plane");
            AddMate(swMateType_e.swMateCOINCIDENT, swMateAlign_e.swMateAlignCLOSEST, false, 0, 0, 0, 0, 0, 0, "coincident faces");
            doc.ForceRebuild3(false);
            SelectPair(v.SwapOrder, baseSide, "base side plane x=50 mm", armSide, "arm side plane y=-20 mm");
            limit = AddMate(swMateType_e.swMateANGLE, v.Align, v.Flip, 0, 0, 0, v.Reference * Deg, v.Upper * Deg, v.Lower * Deg, "angle limit " + v.Lower + ".." + v.Upper);
        }
        else
        {
            SelectPair(false, baseSide, "base side plane x=50 mm", armSide, "arm side plane y=-20 mm");
            AddMate(swMateType_e.swMatePERPENDICULAR, swMateAlign_e.swMateAlignCLOSEST, false, 0, 0, 0, 0, 0, 0, "perpendicular side planes");
            doc.ForceRebuild3(false);
            SelectPair(v.SwapOrder, baseTop, "base top plane", armTop, "arm bottom plane");
            limit = AddMate(swMateType_e.swMateDISTANCE, v.Align, v.Flip, v.Reference / 1000.0, v.Upper / 1000.0, v.Lower / 1000.0, 0, 0, 0, "distance limit " + v.Lower + ".." + v.Upper);
        }
        doc.ForceRebuild3(false);
        return (Feature)limit;
    }

    internal static Mate2 FindLimitMate(out string featureName)
    {
        featureName = null;
        Feature f = (Feature)doc.FirstFeature();
        while (f != null)
        {
            if (f.GetTypeName2() == "MateGroup")
            {
                Feature sub = (Feature)f.GetFirstSubFeature();
                while (sub != null)
                {
                    Mate2 m = sub.GetSpecificFeature2() as Mate2;
                    if (m != null && m.MinimumVariation != m.MaximumVariation) { featureName = sub.Name; return m; }
                    sub = (Feature)sub.GetNextSubFeature();
                }
            }
            f = (Feature)f.GetNextFeature();
        }
        return null;
    }

    // --- truth: how the arm really moves ------------------------------------------------------------------

    // The world position of an arm-local point, computed by SolidWorks itself (no matrix convention assumed).
    internal static double[] World(Component2 comp, double x, double y, double z)
    {
        MathPoint p = (MathPoint)math.CreatePoint(new double[] { x, y, z });
        return (double[])((MathPoint)p.MultiplyTransform(comp.Transform2)).ArrayData;
    }

    // rotation of the arm about world +Z (right-handed) and its world Z position
    static void ArmState(Component2 arm, out double angleDeg, out double zMm)
    {
        double[] o = World(arm, 0, 0, 0), x = World(arm, 1, 0, 0);
        angleDeg = Math.Atan2(x[1] - o[1], x[0] - o[0]) / Deg;
        zMm = o[2] * 1000.0;
    }

    static double Wrap(double d) { while (d > 180) d -= 360; while (d <= -180) d += 360; return d; }

    internal sealed class Truth { public double Lo, Hi; public string Raw; }

    // Drives the dimension of the limit mate to its limits (and back to the pose) and returns the motion relative
    // to the pose, expressed about / along world +Z.
    internal static Truth MeasureTruth(Variant v, string mateName, double poseValue)
    {
        Dimension dim = doc.Parameter("D1@" + mateName) as Dimension;
        if (dim == null) throw new Exception("dimension D1@" + mateName + " not found");
        Component2 arm = Find(ArmName);
        double unit = v.Slide ? 1000.0 : 1.0 / Deg;   // SystemValue (m or rad) -> mm or deg
        double startValue = dim.SystemValue * unit;
        double a0, z0; ArmState(arm, out a0, out z0);
        var t = new Truth();
        double[] targets = new double[] { v.Lower, v.Upper };
        double[] result = new double[2];
        for (int i = 0; i < 2; i++)
        {
            dim.SystemValue = targets[i] / unit;
            doc.ForceRebuild3(false);
            double a, z; ArmState(arm, out a, out z);
            result[i] = v.Slide ? z - z0 : Wrap(a - a0);
            t.Raw += string.Format(" dim={0}->{1}", Num(targets[i]), Num(result[i]));
        }
        dim.SystemValue = poseValue / unit;   // back to the pose that is exported
        doc.ForceRebuild3(false);
        double ab, zb; ArmState(arm, out ab, out zb);
        t.Raw += string.Format(" start dim={0} pose back at dim={1}, state change {2}", Num(startValue), Num(dim.SystemValue * unit), Num(v.Slide ? zb - z0 : Wrap(ab - a0)));
        t.Lo = Math.Min(result[0], result[1]); t.Hi = Math.Max(result[0], result[1]);
        return t;
    }

    // --- exporter -----------------------------------------------------------------------------------------

    static ExportHelper BuildHelper(out Link child)
    {
        var baseLink = new Link { Name = "base_link" };
        child = new Link { Name = "arm" }; PendingChild = child;
        Component2 b = ((AssemblyDoc)doc).GetComponentByName(BaseName);
        Component2 a = ((AssemblyDoc)doc).GetComponentByName(ArmName);
        ComponentList(baseLink).Add(b);
        ComponentList(child).Add(a);
        baseLink.Children.Add(child);
        child.Parent = baseLink;
        baseLink.Joint.CoordinateSystemName = "Origin_global";
        baseLink.Joint.AxisName = "";
        child.Joint.CoordinateSystemName = "Automatically Generate";
        child.Joint.AxisName = "Automatically Generate";
        child.Joint.Type = "Automatically Detect";
        child.Joint.Name = "arm_joint";
        child.Joint.Limit.Effort = 10; child.Joint.Limit.Velocity = 1; // required by the URDF writer, as the add-in page fills them
        var tree = new LinkNode(baseLink);
        tree.UpdateLinkTree(null);
        var helper = new ExportHelper(app);
        MethodInfo create = typeof(ExportHelper).GetMethod("CreateBaseRefOrigin", BindingFlags.NonPublic | BindingFlags.Instance);
        create.Invoke(helper, new object[] { true });
        helper.UpdateReferenceGeometries();
        helper.SetComputeJointLimits(true);
        helper.SetComputeJointKinematics(true);
        helper.SetComputeInertial(true);
        helper.SetComputeVisualCollision(true);
        if (!helper.CreateRobotFromTreeView(tree)) throw new Exception("Robot build failed");
        child = helper.URDFRobot.BaseLink.Children[0];
        return helper;
    }

    static double[] Vec(string text)
    {
        return text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }

    static double[] Rpy(double[] rpy, double[] v)
    {
        double r = rpy[0], p = rpy[1], y = rpy[2];
        double[,] m = new double[3, 3];
        double cr = Math.Cos(r), sr = Math.Sin(r), cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y);
        m[0, 0] = cy * cp; m[0, 1] = cy * sp * sr - sy * cr; m[0, 2] = cy * sp * cr + sy * sr;
        m[1, 0] = sy * cp; m[1, 1] = sy * sp * sr + cy * cr; m[1, 2] = sy * sp * cr - cy * sr;
        m[2, 0] = -sp; m[2, 1] = cp * sr; m[2, 2] = cp * cr;
        var o = new double[3];
        for (int i = 0; i < 3; i++) o[i] = m[i, 0] * v[0] + m[i, 1] * v[1] + m[i, 2] * v[2];
        return o;
    }

    // vector of the base frame (a coordinate system of the model) mapped to the world frame by SolidWorks
    static double[] FrameToWorld(string frame, double[] v)
    {
        MathTransform t = doc.Extension.GetCoordinateSystemTransformByName(frame);
        MathVector mv = (MathVector)math.CreateVector(v);
        return (double[])((MathVector)mv.MultiplyTransform(t)).ArrayData;
    }

    static string Fmt(double[] v) { return "(" + string.Join(", ", v.Select(Num).ToArray()) + ")"; }

    // the world position of the origin of a component and the world images of its local unit vectors, as one list
    static double[] Pose(Component2 comp)
    {
        var list = new List<double>(World(comp, 0, 0, 0));
        foreach (double[] unit in new[] { new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 } })
            list.AddRange((double[])((MathVector)((MathVector)math.CreateVector(unit)).MultiplyTransform(comp.Transform2)).ArrayData);
        return list.ToArray();
    }

    static bool PoseMatches(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length; i++) if (!(Math.Abs(a[i] - b[i]) < 1e-7)) return false;
        return true;
    }

    static int failures = 0;
    static Link PendingChild;

    static void RunVariant(Variant v, string srcDir, string workDir)
    {
        Say("=== " + v.Name + ": " + v.Description);
        string dir = Path.Combine(workDir, v.Name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        // The copies get their own names so that they can be open beside the originals of the same files
        foreach (string f in new[] { "LIM_BASE.SLDPRT", "LIM_ARM.SLDPRT", "LIM_HINGE.SLDASM" }) File.Copy(Path.Combine(srcDir, f), Path.Combine(dir, f.Replace("LIM_", "PB_")));
        string file = Path.Combine(dir, "PB_HINGE.SLDASM");
        var dependencies = (string[])app.GetDocumentDependencies2(file, false, false, false);
        if (dependencies != null) for (int i = 1; i < dependencies.Length; i += 2)
        {
            string local = Path.Combine(dir, Path.GetFileName(dependencies[i]).Replace("LIM_", "PB_"));
            if (!String.Equals(dependencies[i], local, StringComparison.OrdinalIgnoreCase) && !app.ReplaceReferencedDocument(file, dependencies[i], local))
                throw new Exception("Could not localize reference " + dependencies[i]);
        }
        int error = 0, warning = 0;
        doc = app.OpenDoc6(file, 2, 1, "", ref error, ref warning);
        if (doc == null) throw new Exception("open failed " + error);
        string title = doc.GetTitle();
        int activate = 0; app.ActivateDoc3(title, false, 0, ref activate);
        try
        {
            Feature limitFeature = BuildMates(v);
            string mateName; Mate2 mate = FindLimitMate(out mateName);
            if (mate == null) throw new Exception("limit mate not found");
            Say(string.Format("  API: type={0} min={1} max={2} flipped={3} alignment={4}", mate.Type,
                Num(mate.MinimumVariation * (v.Slide ? 1000 : 1 / Deg)), Num(mate.MaximumVariation * (v.Slide ? 1000 : 1 / Deg)), mate.Flipped, mate.Alignment));
            double pose = double.IsNaN(v.PoseValue) ? v.Reference : v.PoseValue;
            Dimension dim = (Dimension)doc.Parameter("D1@" + mateName);
            double unit = v.Slide ? 1000.0 : 1.0 / Deg;
            Say("  limit mate dimension reads " + Num(dim.SystemValue * unit) + (v.Slide ? " mm" : " deg") + " after creation");
            if (!double.IsNaN(v.PoseValue)) { dim.SystemValue = pose / unit; doc.ForceRebuild3(false); }
            Truth truth = MeasureTruth(v, mateName, pose);
            Say("  truth (relative to the exported pose, about/along world +Z):" + truth.Raw);
            if (System.Environment.GetEnvironmentVariable("LIMIT_KEEP_FIXED") != "1")
            {
                // The base was fixed only to make the measurement repeatable; the add-in's model has it however the user left it
                doc.ClearSelection2(true);
                doc.Extension.SelectByID2(BaseName + "@" + System.IO.Path.GetFileNameWithoutExtension(doc.GetPathName()), "COMPONENT", 0, 0, 0, false, 0, null, 0);
                ((AssemblyDoc)doc).UnfixComponent();
                doc.ClearSelection2(true);
                Say("  base component unfixed before the export");
            }
            Component2 armNow = ((AssemblyDoc)doc).GetComponentByName(ArmName);
            if (v.Equation)
            {
                // from here on an equation decides the value of the limit mate dimension (the truth was measured before)
                string equation = "\"D1@" + mateName + "\" = " + Num(v.Reference) + (v.Slide ? "mm" : "deg");
                EquationMgr equations = doc.GetEquationMgr();
                int index = equations.Add2(-1, equation, true);
                doc.ForceRebuild3(false);
                Say("  equation added (index " + index + "): " + equation);
            }
            doc.SaveAs3(file, 0, 1);

            // The probe on its own, on a saved (clean) model: the exporter's own call, reached through reflection
            double valueBefore = dim.SystemValue, minBefore = mate.MinimumVariation, maxBefore = mate.MaximumVariation;
            double[] poseBefore = Pose(armNow);
            bool flagBefore = doc.GetSaveFlag();
            var probeHelper = new ExportHelper(app);
            MethodInfo probe = typeof(ExportHelper).GetMethod("ProbeVariationDirection", BindingFlags.NonPublic | BindingFlags.Instance);
            // as in the exporter, the parent is fixed while the direction is measured
            string asmTitle = System.IO.Path.GetFileNameWithoutExtension(doc.GetPathName());
            doc.ClearSelection2(true); doc.Extension.SelectByID2(BaseName + "@" + asmTitle, "COMPONENT", 0, 0, 0, false, 0, null, 0); ((AssemblyDoc)doc).FixComponent(); doc.ClearSelection2(true);
            int direction = (int)probe.Invoke(probeHelper, new object[] { mate, v.Slide, new double[] { 0, 0, 1 }, armNow });
            doc.ClearSelection2(true); doc.Extension.SelectByID2(BaseName + "@" + asmTitle, "COMPONENT", 0, 0, 0, false, 0, null, 0); ((AssemblyDoc)doc).UnfixComponent(); doc.ClearSelection2(true);
            double valueAfter = dim.SystemValue;
            bool flagAfter = doc.GetSaveFlag();
            bool restored = Math.Abs(valueAfter - valueBefore) < 1e-9 && PoseMatches(poseBefore, Pose(armNow)) &&
                Math.Abs(mate.MinimumVariation - minBefore) < 1e-9 && Math.Abs(mate.MaximumVariation - maxBefore) < 1e-9;
            Say(string.Format("  PROBE direction={0} dimension {1} -> {2} pose restored={3} min/max restored={4} save flag {5} -> {6}", direction,
                Num(valueBefore * unit), Num(valueAfter * unit), PoseMatches(poseBefore, Pose(armNow)),
                Math.Abs(mate.MinimumVariation - minBefore) < 1e-9 && Math.Abs(mate.MaximumVariation - maxBefore) < 1e-9, flagBefore, flagAfter));
            bool probeOk = restored && (v.Equation ? direction == 0 : direction != 0);
            Say("RESULT-PROBE " + v.Name + "\t" + (v.Equation ? "skipped expected" : "measured expected") + "\tdirection " + direction + "\trestored " + restored + "\tsave flag unchanged " + (flagBefore == flagAfter) + "\t" + (probeOk ? "yes" : "NO"));
            if (!probeOk) failures++;
            doc.SaveAs3(file, 0, 1);
            double valueExport = dim.SystemValue;
            double[] poseExport = Pose(armNow);

            string package = "pkg_" + v.Name;
            Link child;
            ExportHelper helper = BuildHelper(out child);
            Say(string.Format("  helper: joint type={0} axis=({1}, {2}, {3}) lower={4} upper={5}", child.Joint.Type,
                Num(child.Joint.Axis.X), Num(child.Joint.Axis.Y), Num(child.Joint.Axis.Z),
                Num(child.Joint.Limit.Lower * (v.Slide ? 1000 : 1 / Deg)), Num(child.Joint.Limit.Upper * (v.Slide ? 1000 : 1 / Deg))));
            string root = Path.Combine(dir, "export");
            helper.SavePath = root; helper.PackageName = package; helper.CancellationToken = CancellationToken.None;
            helper.ExportRobot(true, MeshExportFormat.STL);
            // after the whole export: the dimension, the child and the limits of the mate are where they were before it
            double[] poseAfterExport = Pose(armNow);
            bool afterOk = Math.Abs(dim.SystemValue - valueExport) < 1e-9 && PoseMatches(poseExport, poseAfterExport) &&
                Math.Abs(mate.MinimumVariation - minBefore) < 1e-9 && Math.Abs(mate.MaximumVariation - maxBefore) < 1e-9;
            Say(string.Format("RESULT-EXPORT {0}\tdimension {1} -> {2}\tpose restored {3}\tmate limits unchanged {4}\tsave flag after {5}\t{6}\tmodel already modified when the probe started: {7}", v.Name,
                Num(valueExport * unit), Num(dim.SystemValue * unit), PoseMatches(poseExport, poseAfterExport),
                Math.Abs(mate.MinimumVariation - minBefore) < 1e-9 && Math.Abs(mate.MaximumVariation - maxBefore) < 1e-9, doc.GetSaveFlag(), afterOk ? "yes" : "NO", ExportHelper.LastProbeStartedOnModifiedModel));
            if (!afterOk) failures++;
            string urdf = Directory.GetFiles(Path.Combine(root, package), "*.urdf", SearchOption.AllDirectories).FirstOrDefault();
            if (urdf == null) throw new Exception("no URDF written");
            var xml = new XmlDocument(); xml.Load(urdf);
            XmlNode joint = xml.SelectSingleNode("//joint");
            string type = joint.Attributes["type"].Value;
            double[] origin = Vec(joint.SelectSingleNode("origin").Attributes["xyz"].Value);
            double[] rpy = Vec(joint.SelectSingleNode("origin").Attributes["rpy"].Value);
            XmlNode axisNode = joint.SelectSingleNode("axis");
            double[] axis = axisNode == null ? new double[] { 1, 0, 0 } : Vec(axisNode.Attributes["xyz"].Value);
            XmlNode limitNode = joint.SelectSingleNode("limit");
            double lower = limitNode == null || limitNode.Attributes["lower"] == null ? double.NaN : double.Parse(limitNode.Attributes["lower"].Value, CultureInfo.InvariantCulture);
            double upper = limitNode == null || limitNode.Attributes["upper"] == null ? double.NaN : double.Parse(limitNode.Attributes["upper"].Value, CultureInfo.InvariantCulture);
            Say(string.Format("  URDF: type={0} origin={1} rpy={2} axis={3} lower={4} upper={5}", type, Fmt(origin), Fmt(rpy), Fmt(axis), Num(lower), Num(upper)));

            // exported axis in the world frame: base link frame (Origin_global) o joint frame (rpy)
            double[] axisWorld = FrameToWorld("Origin_global", Rpy(rpy, axis));
            double sign = axisWorld[2];  // +1 when the exported axis is world +Z, -1 when it is -Z
            Say("  exported axis in the world frame = " + Fmt(axisWorld));
            bool axisOk = Math.Abs(Math.Abs(sign) - 1) < 1e-3;
            double scale = v.Slide ? 1000.0 : 1.0 / Deg; // URDF value (m or rad) -> mm or deg
            double expectLo = sign > 0 ? truth.Lo : -truth.Hi;
            double expectHi = sign > 0 ? truth.Hi : -truth.Lo;
            double gotLo = lower * scale, gotHi = upper * scale;
            double tol = v.Slide ? 0.05 : 0.05;
            bool match = axisOk && (type == (v.Slide ? "prismatic" : "revolute")) && Math.Abs(gotLo - expectLo) < tol && Math.Abs(gotHi - expectHi) < tol;
            Say(string.Format("RESULT {0}\ttruth about world Z {1}..{2}\texported axis world Z sign {3}\ttruth in exported axis {4}..{5}\texporter {6}..{7}\ttype {8}\tmatch {9}",
                v.Name, Num(truth.Lo), Num(truth.Hi), Num(sign), Num(expectLo), Num(expectHi), Num(gotLo), Num(gotHi), type, match ? "yes" : "NO"));
            if (!match) failures++;
        }
        finally { app.CloseDoc(title); }
    }

    // The work runs on its own STA thread so that a call that never returns (for example a message box of the
    // exporter that waits for a click) ends the run with a message instead of leaving the host hanging.
    public static int Main(string[] args)
    {
        int code = 0;
        var worker = new Thread(() => { code = Real(args); });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        int seconds = 420;
        if (!worker.Join(seconds * 1000))
        {
            Console.WriteLine("TIMEOUT after " + seconds + " s: a call into SolidWorks or the exporter did not return");
            if (PendingChild != null)
                Console.WriteLine("joint when the call stopped: type=" + PendingChild.Joint.Type + " axis=(" + PendingChild.Joint.Axis.X + ", " +
                    PendingChild.Joint.Axis.Y + ", " + PendingChild.Joint.Axis.Z + ") lower=" + PendingChild.Joint.Limit.Lower + " upper=" + PendingChild.Joint.Limit.Upper);
            File.WriteAllLines(Path.Combine(outDir ?? ".", "limit_probe.txt"), rows.Concat(new[] { "TIMEOUT" }).ToArray(), new UTF8Encoding(false));
            System.Environment.Exit(3);
        }
        return code;
    }

    static int Real(string[] args)
    {
        int code = 0;
        try
        {
            int pid = int.Parse(args[0]);
            string srcDir = Path.GetFullPath(args[1]);
            outDir = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(outDir);
            app = Bind(pid); math = (IMathUtility)app.GetMathUtility();
            URDFPackage.MessageBox = new RecordingMessages();
            // Scratch copies left open by an earlier run (only documents whose file is named PB_*) are closed
            foreach (ModelDoc2 open in (object[])(app.GetDocuments() ?? new object[0]))
                if (Path.GetFileName(open.GetPathName()).StartsWith("PB_", StringComparison.OrdinalIgnoreCase)) app.CloseDoc(open.GetTitle());
            var wanted = new HashSet<string>(args.Skip(3));
            foreach (Variant v in Variants)
            {
                if (wanted.Count > 0 && !wanted.Contains(v.Name)) continue;
                try { RunVariant(v, srcDir, outDir); }
                catch (Exception e) { Say("ERROR in " + v.Name + ": " + e); failures++; }
            }
            Say("messages: " + string.Join(" | ", messages.ToArray()));
            Say("failures: " + failures);
            if (failures > 0) code = 1;
        }
        catch (Exception e) { Console.Error.WriteLine(e); code = 2; }
        finally { if (outDir != null) File.WriteAllLines(Path.Combine(outDir, "limit_probe.txt"), rows.ToArray(), new UTF8Encoding(false)); }
        return code;
    }
}


