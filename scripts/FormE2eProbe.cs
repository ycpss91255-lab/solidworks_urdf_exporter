// Live host for scripts/test_form_e2e.py. It shows the real AssemblyExportForm of a private SW2URDF.dll with the
// real ExportHelper, for ONE SolidWorks process, carries out the steps of a steps file the way a user does (tree selection, combo boxes, key presses, buttons, the
// Save As dialog), and records what it saw in <out>\probe.tsv (key<TAB>value). It also reads the reference values
// the check needs from SolidWorks itself. It decides nothing: the expectations are in test_form_e2e.py.
//
// The process must run on a desktop of its own (--desktop <name>, created by the script): no window of the form,
// no message box and no Save As dialog can then appear on the user's desktop or take its focus. It refuses to run
// on any other desktop. Every dialog is answered by a watcher and its text is recorded.
//
// A process on another desktop does not see the Running Object Table entries of the user's desktop. The script binds
// to SolidWorks_PID_<pid> and hands the interface over in a file (marshalled by COM); the host checks the process id.
//
// Only the scratch copies it is given are opened; SolidWorks stays as visible or hidden as it is.
// Plain C# 5 so that the .NET Framework csc.exe can compile it, together with LimitLiveProbe.cs (mates, measured
// motion) and ObjRegressProbe.cs (opening scratch copies).
//
// Usage:
//   FormE2eProbe.exe models   --desktop <name> <pid> <app> <folder> <out>                    adds the mates and measures them
//   FormE2eProbe.exe run      --desktop <name> <pid> <app> <assembly> <tree> <steps> <out>   one scenario
//   FormE2eProbe.exe features --desktop <name> <out>                                         what the build can do
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;

public static class FormE2eProbe
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder text, int size, out int needed);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc proc, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr w, string l, uint flags, uint ms, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("ole32.dll")] static extern int CreateStreamOnHGlobal(IntPtr memory, bool release, out System.Runtime.InteropServices.ComTypes.IStream stream);
    [DllImport("ole32.dll")] static extern int CoUnmarshalInterface(System.Runtime.InteropServices.ComTypes.IStream stream, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    const uint WM_SETTEXT = 0x000C, WM_COMMAND = 0x0111, WM_CHAR = 0x0102;
    const double Deg = Math.PI / 180.0;

    static readonly List<string> rows = new List<string>();
    static readonly object rowsLock = new object();
    static string outDir;
    static SldWorks app;
    static IMathUtility math;
    static ModelDoc2 doc;
    static uint uiThread;
    static volatile string savePath;          // what the watcher types into the Save As dialog
    static volatile string doing = "starting";
    static int dialogs;

    static void Put(string key, object value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        lock (rowsLock) rows.Add(key + "\t" + text);
    }

    static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    static string Nums(IEnumerable<double> v) { return string.Join(" ", v.Select(Num).ToArray()); }

    static void Flush()
    {
        lock (rowsLock) if (outDir != null) File.WriteAllLines(Path.Combine(outDir, "probe.tsv"), rows.ToArray(), new UTF8Encoding(false));
    }

    // --- dialogs ------------------------------------------------------------------------------------------

    static string ClassName(IntPtr h) { var b = new StringBuilder(256); GetClassNameW(h, b, 256); return b.ToString(); }
    static string WindowText(IntPtr h) { var b = new StringBuilder(4096); GetWindowTextW(h, b, 4096); return b.ToString(); }

    static List<IntPtr> Children(IntPtr h)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(h, (c, l) => { list.Add(c); return true; }, IntPtr.Zero);
        return list;
    }

    // Answers every dialog of the UI thread: the Save As dialog gets the path of the running export step, a message
    // box gets its Yes or OK button. Nothing ever waits for a person.
    static void Watch()
    {
        var since = new Dictionary<IntPtr, DateTime>();
        var answered = new HashSet<IntPtr>();
        while (true)
        {
            Thread.Sleep(150);
            var found = new List<IntPtr>();
            EnumThreadWindows(uiThread, (h, l) => { if (IsWindowVisible(h) && ClassName(h) == "#32770") found.Add(h); return true; }, IntPtr.Zero);
            foreach (IntPtr h in found)
            {
                if (!since.ContainsKey(h)) { since[h] = DateTime.Now; continue; }   // let it finish building
                if ((DateTime.Now - since[h]).TotalMilliseconds < 600) continue;
                if (answered.Contains(h))
                {
                    // the answer did not close it: cancel, so that the run can report it and go on
                    if ((DateTime.Now - since[h]).TotalSeconds > 25)
                    {
                        Put("dialog.stuck", WindowText(h) + " during " + doing);
                        since[h] = DateTime.Now;
                        PostMessageW(h, WM_COMMAND, new IntPtr(2), IntPtr.Zero);
                    }
                    continue;
                }
                answered.Add(h);
                since[h] = DateTime.Now;
                Answer(h, Children(h));
            }
            foreach (IntPtr gone in since.Keys.Where(k => !IsWindow(k)).ToArray()) { since.Remove(gone); answered.Remove(gone); }
        }
    }

    static void Answer(IntPtr h, List<IntPtr> children)
    {
        if (children.Any(c => ClassName(c) == "DUIViewWndClassName" || ClassName(c) == "ComboBoxEx32"))
        {
            IntPtr edit = children.FirstOrDefault(c => ClassName(c) == "Edit" && GetDlgCtrlID(c) == 1001);
            if (edit == IntPtr.Zero) edit = children.FirstOrDefault(c => ClassName(c) == "Edit");
            Put("dialog.save", WindowText(h) + " -> " + (savePath == null ? "(no path expected)" : Path.GetFileName(savePath)));
            int answer = savePath != null && edit != IntPtr.Zero ? 1 : 2;
            if (answer == 1)
            {
                IntPtr result;
                SendMessageTimeoutW(edit, WM_SETTEXT, IntPtr.Zero, savePath, 2, 5000, out result);
                Thread.Sleep(300);
            }
            PostMessageW(h, WM_COMMAND, new IntPtr(answer), GetDlgItem(h, answer));
            return;
        }
        string text = string.Join(" | ", children.Where(c => ClassName(c) == "Static").Select(WindowText).Where(t => t.Length > 0).ToArray());
        Put("dialog.message." + (dialogs++), "[" + WindowText(h) + "] " + text + " (during " + doing + ")");
        int button = GetDlgItem(h, 6) != IntPtr.Zero ? 6 : GetDlgItem(h, 1) != IntPtr.Zero ? 1 : 2;
        PostMessageW(h, WM_COMMAND, new IntPtr(button), GetDlgItem(h, button));
    }

    // --- the form -----------------------------------------------------------------------------------------

    static AssemblyExportForm form;
    static ExportHelper helper;

    static T Field<T>(string name) where T : class
    {
        FieldInfo field = typeof(AssemblyExportForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (field == null) throw new Exception("The form has no " + name);
        return (T)field.GetValue(form);
    }

    static void Pump(int milliseconds)
    {
        DateTime end = DateTime.Now.AddMilliseconds(milliseconds);
        do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.Now < end);
    }

    // tree file, one link per line: name, parent (or -), components (,), frame, axis, joint type, joint name
    static string[][] TreeLines(string file)
    {
        return File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')).ToArray();
    }

    // What the PropertyManager page hands over: the link tree, the base frame and the four "compute" choices.
    static void OpenForm(string[][] tree)
    {
        var links = new Dictionary<string, Link>();
        Link root = null;
        foreach (string[] t in tree)
        {
            var link = new Link { Name = t[0] };
            foreach (string name in t[2].Split(',')) LimitLiveProbe.ComponentList(link).Add(Component(name));
            link.Joint.CoordinateSystemName = t[3];
            link.Joint.AxisName = t[4];
            if (t[1] == "-") root = link;
            else
            {
                link.Parent = links[t[1]];
                links[t[1]].Children.Add(link);
                link.Joint.Type = t[5];
                link.Joint.Name = t[6];
                link.Joint.Limit.Effort = 10; link.Joint.Limit.Velocity = 1;   // required; the page fills them
            }
            links[t[0]] = link;
        }
        var node = new LinkNode(root);
        node.UpdateLinkTree(null);
        helper = new ExportHelper(app);
        MethodInfo create = typeof(ExportHelper).GetMethod("CreateBaseRefOrigin", BindingFlags.NonPublic | BindingFlags.Instance);
        create.Invoke(helper, new object[] { true });
        helper.UpdateReferenceGeometries();
        helper.SetComputeInertial(true);
        helper.SetComputeVisualCollision(true);
        helper.SetComputeJointKinematics(true);
        helper.SetComputeJointLimits(true);
        if (!helper.CreateRobotFromTreeView(node)) throw new Exception("Robot build failed");
        form = new AssemblyExportForm(app, node, helper);
        form.Exporter = helper;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(0, 0);
        form.ShowInTaskbar = false;
        form.Show();
        Pump(400);
    }

    static Component2 Component(string name)
    {
        Component2 c = ((AssemblyDoc)doc).GetComponentByName(name);
        if (c == null) throw new Exception("component not found: " + name);
        return c;
    }

    static IEnumerable<TreeNode> All(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes) { yield return n; foreach (TreeNode c in All(n.Nodes)) yield return c; }
    }

    static void SelectNode(string tree, Func<LinkNode, bool> wanted, string what)
    {
        TreeView view = Field<TreeView>(tree);
        TreeNode node = All(view.Nodes).FirstOrDefault(n => wanted((LinkNode)n));
        if (node == null) throw new Exception("not in the tree: " + what);
        view.SelectedNode = node;
        Pump(100);
    }

    static void Click(string button)
    {
        Button b = Field<Button>(button);
        if (!b.Visible || !b.Enabled) throw new Exception(button + " is not available");
        b.PerformClick();
        Pump(200);
    }

    // Key presses in the box, as a user replaces its content: everything selected, Backspace, then the characters
    static void TypeInto(string box, string text)
    {
        TextBox t = Field<TextBox>(box);
        t.Focus(); t.SelectAll();
        SendMessageW(t.Handle, WM_CHAR, new IntPtr(8), IntPtr.Zero);
        foreach (char c in text) SendMessageW(t.Handle, WM_CHAR, new IntPtr((int)c), IntPtr.Zero);
        Pump(50);
    }

    static void Dump(string label)
    {
        foreach (Control c in AllControls(form))
        {
            if (string.IsNullOrEmpty(c.Name)) continue;
            string key = "dump." + label + "." + c.Name;
            if (c is TextBox || c is ComboBox || c is DomainUpDown || c is Label) Put(key, c.Text);
            else if (c is CheckBox) Put(key, ((CheckBox)c).Checked);
            else if (c is RadioButton) Put(key, ((RadioButton)c).Checked);
        }
        foreach (string unit in new[] { "comboBoxAngleUnitJoint", "comboBoxAngleUnitLink" })
        {
            FieldInfo f = typeof(AssemblyExportForm).GetField(unit, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) Put("dump." + label + "." + unit + ".index", ((ComboBox)f.GetValue(form)).SelectedIndex);
        }
    }

    static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control c in parent.Controls) { yield return c; foreach (Control d in AllControls(c)) yield return d; }
    }

    static void Export(string button, string dir, string package)
    {
        Directory.CreateDirectory(dir);
        helper.SavePath = dir; helper.PackageName = package;    // the dialog opens with these, as after an earlier export
        savePath = Path.Combine(dir, package);
        AssemblyExportForm current = form;
        Click(button);
        Pump(300);
        savePath = null;
        Put("export." + package + ".closed", current.IsDisposed || !current.Visible);
        Put("export." + package + ".path", Path.GetFileName(helper.SavePath) + "/" + helper.PackageName);
        Put("export." + package + ".folder", Directory.Exists(Path.Combine(dir, package)));
    }

    static void RunStep(string[] s, string[][] tree)
    {
        switch (s[0])
        {
            case "open": OpenForm(tree); break;
            case "unit":
                {
                    // the box of the page on display
                    bool linkPage = Field<Panel>("panelLinkProperties").Visible;
                    ComboBox box = Field<ComboBox>(linkPage ? "comboBoxAngleUnitLink" : "comboBoxAngleUnitJoint");
                    box.SelectedIndex = s[1] == "deg" ? 1 : 0;
                    Pump(100);
                    break;
                }
            case "joint": SelectNode("treeViewJointTree", n => n.Link.Joint.Name == s[1], s[1]); break;
            case "link": SelectNode("treeViewLinkProperties", n => n.Link.Name == s[1], s[1]); break;
            case "type": TypeInto(s[1], s[2]); break;
            case "dump": Dump(s[1]); break;
            case "next": Click("buttonJointNext"); break;
            case "previous": Click("buttonLinksPrevious"); break;
            case "check": Field<CheckBox>(s[1]).Checked = s[2] == "on"; Pump(50); break;
            case "radio": Field<RadioButton>(s[1]).Checked = true; Pump(50); break;
            case "export": Export(s[1] == "model" ? "buttonLinksExportUrdfOnly" : "buttonLinksFinish", Path.Combine(outDir, "export"), s[2]); break;
            case "close": form.Close(); Pump(200); break;
            case "read": ReadModel(tree, s[1]); break;
            default: throw new Exception("unknown step " + s[0]);
        }
    }

    // --- reference values from SolidWorks -----------------------------------------------------------------

    static double[] MapPoint(MathTransform t, double[] p)
    {
        return (double[])((MathPoint)((MathPoint)math.CreatePoint(p)).MultiplyTransform(t)).ArrayData;
    }

    static double[] MapVector(MathTransform t, double[] v)
    {
        return (double[])((MathVector)((MathVector)math.CreateVector(v)).MultiplyTransform(t)).ArrayData;
    }

    // Origin and images of the three unit vectors in the world, computed by SolidWorks (no matrix convention assumed)
    static double[] FrameOf(MathTransform t)
    {
        return MapPoint(t, new double[] { 0, 0, 0 }).Take(3)
            .Concat(MapVector(t, new double[] { 1, 0, 0 }).Take(3))
            .Concat(MapVector(t, new double[] { 0, 1, 0 }).Take(3))
            .Concat(MapVector(t, new double[] { 0, 0, 1 }).Take(3)).ToArray();
    }

    // Mass, centre of mass, inertia and vertices of the components of every link, all in the world frame, and the
    // frames of the links. The inertia is put together from the principal moments and axes, which have no sign convention.
    static void ReadModel(string[][] tree, string label)
    {
        foreach (string[] t in tree)
        {
            string key = label + "." + t[0];
            Component2[] components = t[2].Split(',').Select(n => Component(n)).ToArray();
            IMassProperty2 mp = (IMassProperty2)doc.Extension.CreateMassProperty2();
            mp.UseSystemUnits = true;
            mp.IncludeHiddenBodiesOrComponents = true;
            mp.SelectedItems = components;
            if (!mp.Recalculate()) throw new Exception("Mass properties failed for " + t[0]);
            Put(key + ".mass", Num(mp.Mass));
            Put(key + ".volume", Num(mp.Volume));
            Put(key + ".com", Nums(((double[])mp.CenterOfMass).Take(3)));
            double[] moments = ((double[])mp.PrincipalMomentsOfInertia).Take(3).ToArray();
            var tensor = new double[9];
            for (int k = 0; k < 3; k++)
            {
                double[] a = ((double[])mp.get_PrincipalAxesOfInertia(k)).Take(3).ToArray();
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) tensor[3 * i + j] += moments[k] * a[i] * a[j];
            }
            Put(key + ".inertia", Nums(tensor));
            Put(key + ".component", Nums(FrameOf(components[0].Transform2)));
            var vertices = new List<double>();
            double[] box = { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            foreach (Component2 c in components)
            {
                object info;
                MathTransform back = (MathTransform)c.Transform2.Inverse();
                foreach (object body in (object[])c.GetBodies3((int)swBodyType_e.swSolidBody, out info))
                {
                    for (int d = 0; d < 6; d++)
                    {
                        // the farthest point of the body (round faces included) along +-x, +-y, +-z of the world
                        var world = new double[3]; world[d % 3] = d < 3 ? -1 : 1;
                        double[] inPart = MapVector(back, world);
                        double x, y, z;
                        if (!((Body2)body).GetExtremePoint(inPart[0], inPart[1], inPart[2], out x, out y, out z)) throw new Exception("no extreme point");
                        double value = MapPoint(c.Transform2, new[] { x, y, z })[d % 3];
                        box[d] = d < 3 ? Math.Min(box[d], value) : Math.Max(box[d], value);
                    }
                    foreach (object v in (object[])(((Body2)body).GetVertices() ?? new object[0]))
                        vertices.AddRange(MapPoint(c.Transform2, (double[])((Vertex)v).GetPoint()).Take(3));
                }
            }
            Put(key + ".vertices", Nums(vertices));
            Put(key + ".box", Nums(box));
            string frame = t[3] == "Automatically Generate" ? GeneratedFrame(t[0]) : t[3];
            if (frame == null) continue;
            MathTransform transform = doc.Extension.GetCoordinateSystemTransformByName(frame);
            if (transform != null) Put(key + ".frame", Nums(FrameOf(transform)));
        }
        MathTransform global = doc.Extension.GetCoordinateSystemTransformByName("Origin_global");
        if (global != null) Put(label + ".Origin_global", Nums(FrameOf(global)));
    }

    // Name of the coordinate system the exporter made for a link (its place is then read from SolidWorks)
    static string GeneratedFrame(string link)
    {
        if (helper == null || helper.URDFRobot == null) return null;
        var links = new List<Link>(); ObjRegressProbe.CollectLinks(helper.URDFRobot.BaseLink, links);
        Link found = links.FirstOrDefault(l => l.Name == link);
        return found == null || found.Joint == null ? null : found.Joint.CoordinateSystemName;
    }

    // --- models -------------------------------------------------------------------------------------------

    static Feature LastMate;

    static void MateAndMeasure(string key, string baseName, string armName, LimitLiveProbe.Variant v)
    {
        LimitLiveProbe.BaseName = baseName; LimitLiveProbe.ArmName = armName;
        Feature feature = LimitLiveProbe.MatePair(v);
        LastMate = feature;
        string mate = feature.Name;
        Dimension dimension = (Dimension)doc.Parameter("D1@" + mate);
        Put(key + ".value", Num(dimension.SystemValue * (v.Slide ? 1000 : 1 / Deg)));
        LimitLiveProbe.Truth truth = LimitLiveProbe.MeasureTruth(v, mate, v.Reference);
        Put(key + ".motion", Num(truth.Lo) + " " + Num(truth.Hi));
        Put(key + ".raw", truth.Raw);
        // the boss is on the origin of the base part and along its Z axis
        MathTransform t = Component(baseName).Transform2;
        Put(key + ".axis", Nums(MapPoint(t, new double[] { 0, 0, 0 }).Take(3).Concat(MapVector(t, new double[] { 0, 0, 1 }).Take(3))));
        Put(key + ".arm", Nums(FrameOf(Component(armName).Transform2)));
    }

    static void Fix(string component, double[] transform)
    {
        string full = component + "@" + Path.GetFileNameWithoutExtension(doc.GetPathName());
        doc.ClearSelection2(true);
        if (!doc.Extension.SelectByID2(full, "COMPONENT", 0, 0, 0, false, 0, null, 0)) throw new Exception("component not found: " + component);
        if (transform != null)
        {
            ((AssemblyDoc)doc).UnfixComponent();
            Component(component).Transform2 = (MathTransform)math.CreateTransform(transform);
            doc.ForceRebuild3(false);
            doc.ClearSelection2(true);
            doc.Extension.SelectByID2(full, "COMPONENT", 0, 0, 0, false, 0, null, 0);
        }
        ((AssemblyDoc)doc).FixComponent();
        doc.ClearSelection2(true);
    }

    static double[] Rotation(double roll, double pitch, double yaw)
    {
        double cr = Math.Cos(roll), sr = Math.Sin(roll), cp = Math.Cos(pitch), sp = Math.Sin(pitch), cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        return new[] { cy * cp, cy * sp * sr - sy * cr, cy * sp * cr + sy * sr, sy * cp, sy * sp * sr + cy * cr, sy * sp * cr - cy * sr, -sp, cp * sr, cp * cr };
    }

    static void OpenModel(string file)
    {
        doc = ObjRegressProbe.Open(file, false);
        LimitLiveProbe.doc = doc;
    }

    static void Models(string folder)
    {
        var hinge = new LimitLiveProbe.Variant { Name = "hinge", Reference = 90, Lower = 60, Upper = 180 };
        var slide = new LimitLiveProbe.Variant { Name = "slide", Slide = true, Reference = 10, Lower = 0, Upper = 30 };
        foreach (string name in new[] { "LIM_HINGE", "LIM_ORIGIN", "LIM_SLIDE", "LIM_FREE", "FORM_MULTI" })
        {
            doing = "model " + name;
            OpenModel(Path.Combine(folder, name + ".SLDASM"));
            string title = doc.GetTitle();
            try
            {
                if (name == "LIM_HINGE") MateAndMeasure("hinge", "LIM_BASE-1", "LIM_ARM-1", hinge);
                else if (name == "LIM_ORIGIN") MateAndMeasure("origin", "LIM_BASE-1", "LIM_ARM-1", hinge);
                else if (name == "LIM_SLIDE") MateAndMeasure("slide", "LIM_BASE-1", "LIM_ARM-1", slide);
                else if (name == "LIM_FREE")
                {
                    // the slide without its limit mate
                    MateAndMeasure("free", "LIM_BASE-1", "LIM_ARM-1", slide);
                    doc.ClearSelection2(true);
                    if (!LastMate.Select2(false, 0) || !doc.Extension.DeleteSelection2(0)) throw new Exception("could not delete the limit mate");
                }
                else
                {
                    MateAndMeasure("multi.hinge", "LIM_BASE-1", "LIM_ARM-1", hinge);
                    MateAndMeasure("multi.slide", "LIM_BASE-2", "LIM_ARM-2", slide);
                    // The block: turned in the assembly, with a link frame that is turned in another way
                    double[] r = Rotation(25 * Deg, -35 * Deg, 50 * Deg);
                    Fix("FORM_BLOCK-1", new[] { r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8], 0.15, 0.13, 0.09, 1, 0, 0, 0 });
                    doc.ClearSelection2(true);
                    Feature frame = doc.FeatureManager.CreateCoordinateSystemUsingNumericalValues(true, 0.13, 0.16, 0.07, true, 40 * Deg, 15 * Deg, -70 * Deg);
                    if (frame == null) throw new Exception("could not create the coordinate system of the block");
                    frame.Name = "CS_BLOCK";
                    doc.ClearSelection2(true);
                    Feature post = doc.FeatureManager.CreateCoordinateSystemUsingNumericalValues(true, 0.31, -0.03, 0.06, true, 0, 30 * Deg, 90 * Deg);
                    if (post == null) throw new Exception("could not create the coordinate system of the post");
                    post.Name = "CS_POST";
                    Put("multi.block.frame", Nums(FrameOf(doc.Extension.GetCoordinateSystemTransformByName("CS_BLOCK"))));
                    Put("multi.block.component", Nums(FrameOf(Component("FORM_BLOCK-1").Transform2)));
                }
                doc.ForceRebuild3(false);
                int error = doc.SaveAs3(doc.GetPathName(), 0, 1);
                if (error != 0) throw new Exception("saving " + name + " failed: " + error);
            }
            finally { app.CloseDoc(title); }
        }
    }

    static void Features()
    {
        Assembly dll = typeof(ExportHelper).Assembly;
        Put("angle_unit", dll.GetType("SW2URDF.UI.AngleUnitSetting") != null &&
            typeof(AssemblyExportForm).GetField("comboBoxAngleUnitJoint", BindingFlags.NonPublic | BindingFlags.Instance) != null);
        Type xacro = dll.GetType("SW2URDF.URDFExport.XacroOutput");
        FieldInfo extension = xacro == null ? null : xacro.GetField("XacroExtension");
        Put("xacro", xacro != null && typeof(AssemblyExportForm).GetField("checkBoxXacro", BindingFlags.NonPublic | BindingFlags.Instance) != null);
        Put("xacro_extension", extension == null ? "" : extension.GetRawConstantValue());
        Put("digits12", dll.GetType("SW2URDF.UI.ValueFormat") != null);
        var version = (AssemblyInformationalVersionAttribute)System.Attribute.GetCustomAttribute(dll, typeof(AssemblyInformationalVersionAttribute));
        Put("version", version == null ? "" : version.InformationalVersion);
    }

    static void Scenario(string file, string treeFile, string stepsFile)
    {
        string[][] tree = TreeLines(treeFile);
        OpenModel(file);
        string title = doc.GetTitle();
        try
        {
            int n = 0;
            foreach (string[] step in TreeLines(stepsFile))
            {
                doing = "step " + (++n) + " " + string.Join(" ", step);
                RunStep(step, tree);
                Flush();
            }
        }
        finally
        {
            doing = "closing";
            if (form != null && !form.IsDisposed) form.Close();
            app.CloseDoc(title);
        }
    }

    // The SolidWorks interface that the script wrote to a file, after the script looked it up by its process id
    static SldWorks Attach(string file, int pid)
    {
        byte[] data = File.ReadAllBytes(file);
        System.Runtime.InteropServices.ComTypes.IStream stream;
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out stream));
        stream.Write(data, data.Length, IntPtr.Zero);
        stream.Seek(0, 0, IntPtr.Zero);
        Guid unknown = new Guid("00000000-0000-0000-C000-000000000046");
        object value;
        Marshal.ThrowExceptionForHR(CoUnmarshalInterface(stream, ref unknown, out value));
        var found = (SldWorks)value;
        if (found.GetProcessID() != pid) throw new Exception("process id mismatch");
        return found;
    }

    static string DesktopName()
    {
        var b = new StringBuilder(256); int needed;
        GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, b, 512, out needed);
        return b.ToString();
    }

    static int Work(string[] args)
    {
        try
        {
            if (args[1] != "--desktop" || DesktopName() != args[2] || args[2] == "Default")
                throw new Exception("not on the private desktop (" + DesktopName() + ")");
            uiThread = GetCurrentThreadId();
            new Thread(Watch) { IsBackground = true }.Start();
            // As in SolidWorks: an exception in an event handler reaches the form's own handler, which shows a message
            // box. The watcher records it and the run goes on, like a user who reads it and continues.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Put("status", "error"); Put("error", doing + ": unhandled " + e.ExceptionObject);
                Flush();
                System.Environment.Exit(1);
            };
            Application.EnableVisualStyles();
            URDFPackage.MessageBox = new LimitLiveProbe.RecordingMessages();
            if (args[0] == "features") { Features(); Put("status", "ok"); return 0; }
            app = Attach(args[4], int.Parse(args[3]));
            math = (IMathUtility)app.GetMathUtility();
            LimitLiveProbe.app = app; LimitLiveProbe.math = math; ObjRegressProbe.app = app;
            Put("language", app.GetCurrentLanguage());
            if (args[0] == "models") Models(Path.GetFullPath(args[5]));
            else if (args[0] == "run") Scenario(Path.GetFullPath(args[5]), Path.GetFullPath(args[6]), Path.GetFullPath(args[7]));
            else throw new Exception("unknown mode " + args[0]);
            Put("messages", string.Join(" | ", LimitLiveProbe.messages.ToArray()));
            Put("status", "ok");
            return 0;
        }
        catch (Exception e)
        {
            Put("status", "error"); Put("error", doing + ": " + (e is TargetInvocationException ? e.InnerException : e));
            return 1;
        }
    }

    // The work runs on its own STA thread: a call that never returns ends the run instead of leaving it hanging
    public static int Main(string[] args)
    {
        outDir = Path.GetFullPath(args[args.Length - 1]);
        Directory.CreateDirectory(outDir);
        int code = 1, seconds = 240;
        var worker = new Thread(() => { code = Work(args); });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
        if (!worker.Join(seconds * 1000))
        {
            Put("status", "timeout"); Put("error", "no result after " + seconds + " s, " + doing);
            code = 3;
        }
        Flush();
        System.Environment.Exit(code);
        return code;
    }
}
