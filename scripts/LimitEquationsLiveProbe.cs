// Live check for limit mates whose value is driven by equations and global variables (issue 170).
// It runs the exporter's own reading of a limit mate (ExportHelper.ReadLimitMateValues of a private SW2URDF.dll,
// called by reflection) against ONE SolidWorks process named by its Running Object Table entry
// (SolidWorks_PID_<pid>), on the LIMIT_EQUATIONS assembly that scripts/make_limit_equation_fixture.py builds, and
// compares the variations with the exact doubles computed from the typed numbers:
//     (minimum - value) * pi / 180 and (maximum - value) * pi / 180 for the angle mate (degrees typed),
//     (minimum - value) / 1000 and (maximum - value) / 1000 for the distance mate (millimetres typed).
// Steps: the global variables of CFG_A; a plain equation; a variable change read before and after a rebuild;
// CFG_B; CFG_A again. The document is closed without saving. It never changes the visibility of SolidWorks.
// Plain C# 5 so that the .NET Framework csc.exe can compile it.
//
// Usage: LimitEquationsLiveProbe.exe <pid> <LIMIT_EQUATIONS.SLDASM>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.URDFExport;

public static class LimitEquationsLiveProbe
{
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx context);

    const string HingeMate = "HINGE_LIMIT", SlideMate = "SLIDE_LIMIT";
    const double HingeMin = 60, HingeMax = 180, SlideMin = 5, SlideMax = 30;   // typed limits of the fixture

    static SldWorks app;
    static ModelDoc2 doc;
    static ExportHelper helper;
    static MethodInfo read;
    static int failures;

    static SldWorks Bind(int pid)
    {
        IRunningObjectTable table; IBindCtx context;
        GetRunningObjectTable(0, out table);
        CreateBindCtx(0, out context);
        IEnumMoniker monikers; table.EnumRunning(out monikers);
        IMoniker[] one = new IMoniker[1];
        string wanted = "SolidWorks_PID_" + pid;
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            string name; one[0].GetDisplayName(context, null, out name);
            if (name != wanted) continue;
            object instance; table.GetObject(one[0], out instance);
            SldWorks sw = (SldWorks)instance;
            if (sw.GetProcessID() != pid) throw new Exception(wanted + " reports another process id");
            return sw;
        }
        throw new Exception(wanted + " is not in the Running Object Table");
    }

    static string R(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    static string G15(double v) { return v.ToString("G15", CultureInfo.InvariantCulture); }

    // The equation manager has to be fetched again after the configuration changed.
    static EquationMgr Equations() { return (EquationMgr)doc.GetEquationMgr(); }

    static int IndexOf(string prefix)
    {
        EquationMgr manager = Equations();
        for (int i = 0; i < manager.GetCount(); i++)
            if (manager.get_Equation(i).Replace(" ", "").StartsWith(prefix.Replace(" ", ""))) return i;
        throw new Exception("No equation starting with " + prefix);
    }

    static void SetEquation(string prefix, string text, string configuration)
    {
        int index = IndexOf(prefix);
        int status = configuration == null
            ? Equations().SetEquationAndConfigurationOption(index, text, (int)swInConfigurationOpts_e.swAllConfiguration, null)
            : Equations().SetEquationAndConfigurationOption(index, text, (int)swInConfigurationOpts_e.swSpecifyConfiguration, new string[] { configuration });
        if (status < 0) throw new Exception("SolidWorks rejected " + text);
    }

    static void Rebuild()
    {
        Equations().EvaluateAll();
        doc.ForceRebuild3(false);
    }

    static void Activate(string configuration)
    {
        string active = ((Configuration)doc.GetActiveConfiguration()).Name;
        if (active != configuration && !doc.ShowConfiguration2(configuration)) throw new Exception("Could not activate " + configuration);
        doc.ForceRebuild3(false);
    }

    // The exporter's own reading of the mate: ReadLimitMateValues(Mate2, bool angle, out reference, out min, out max).
    static bool ReadLikeExporter(string mateName, bool angle, out double reference, out double minimum, out double maximum)
    {
        Feature feature = (Feature)((AssemblyDoc)doc).FeatureByName(mateName);
        if (feature == null) throw new Exception("No mate " + mateName);
        object[] arguments = { (Mate2)feature.GetSpecificFeature2(), angle, null, null, null };
        bool ok = (bool)read.Invoke(helper, arguments);
        reference = (double)arguments[2]; minimum = (double)arguments[3]; maximum = (double)arguments[4];
        return ok;
    }

    static void CheckMate(string step, string mateName, bool angle, double typedValue, double typedMin, double typedMax, bool informational)
    {
        double reference, minimum, maximum;
        bool ok = ReadLikeExporter(mateName, angle, out reference, out minimum, out maximum);
        double expectedMin = angle ? (typedMin - typedValue) * Math.PI / 180.0 : (typedMin - typedValue) / 1000.0;
        double expectedMax = angle ? (typedMax - typedValue) * Math.PI / 180.0 : (typedMax - typedValue) / 1000.0;
        bool exact = ok && minimum == expectedMin && maximum == expectedMax;
        double typedStored = angle ? typedValue * Math.PI / 180.0 : typedValue / 1000.0;
        bool current = ok && Math.Abs(reference - typedStored) < 1e-9;
        string verdict = informational ? (current ? "info: value current" : "info: value STALE") : (exact ? "PASS" : "FAIL");
        if (!informational && !exact) failures++;
        Console.WriteLine("{0} {1} {2}: value {3} {4} (stored {5}); variations {6} .. {7}, expected {8} .. {9}; URDF text {10} .. {11}",
            verdict, step, mateName, typedValue, angle ? "deg" : "mm", R(reference), R(minimum), R(maximum),
            R(expectedMin), R(expectedMax), G15(minimum), G15(maximum));
    }

    static void Check(string step, double angle, double distance, bool informational = false)
    {
        CheckMate(step, HingeMate, true, angle, HingeMin, HingeMax, informational);
        CheckMate(step, SlideMate, false, distance, SlideMin, SlideMax, informational);
    }

    public static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: LimitEquationsLiveProbe.exe <pid> <LIMIT_EQUATIONS.SLDASM>"); return 2; }
        app = Bind(int.Parse(args[0], CultureInfo.InvariantCulture));
        read = typeof(ExportHelper).GetMethod("ReadLimitMateValues", BindingFlags.NonPublic | BindingFlags.Instance);
        if (read == null) { Console.WriteLine("FAIL this SW2URDF.dll has no ExportHelper.ReadLimitMateValues (issue 168)"); return 1; }
        int errors = 0, warnings = 0;
        doc = app.OpenDoc6(args[1], (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
        if (doc == null) { Console.WriteLine("FAIL could not open " + args[1] + " (error " + errors + ")"); return 1; }
        string title = doc.GetTitle();
        try
        {
            int activateErrors = 0;
            app.ActivateDoc3(title, false, 0, ref activateErrors);
            helper = new ExportHelper(app);

            Activate("CFG_A");
            Check("CFG_A, global variables 90 deg / 10 mm", 90, 10);

            SetEquation("\"D1@HINGE_LIMIT\"", "\"D1@HINGE_LIMIT\" = \"base_angle\" * 0.75 + 10.2", null);
            SetEquation("\"D1@SLIDE_LIMIT\"", "\"D1@SLIDE_LIMIT\" = \"base_dist\" * 1.25", null);
            Rebuild();
            Check("CFG_A, plain equations base_angle * 0.75 + 10.2 and base_dist * 1.25", 77.7, 12.5);
            SetEquation("\"D1@HINGE_LIMIT\"", "\"D1@HINGE_LIMIT\" = \"base_angle\"", null);
            SetEquation("\"D1@SLIDE_LIMIT\"", "\"D1@SLIDE_LIMIT\" = \"base_dist\"", null);
            Rebuild();

            SetEquation("\"base_angle\"", "\"base_angle\" = 100", "CFG_A");
            SetEquation("\"base_dist\"", "\"base_dist\" = 15", "CFG_A");
            Console.WriteLine("info: after the variable change NeedsRebuild2 = " + doc.Extension.NeedsRebuild2);
            Check("CFG_A, variables changed to 100 deg / 15 mm, read before the rebuild", 100, 15, true);
            Rebuild();
            Check("CFG_A, variables changed to 100 deg / 15 mm, after the rebuild", 100, 15);

            Activate("CFG_B");
            Check("CFG_B, global variables 120 deg / 20 mm", 120, 20);
            Activate("CFG_A");
            Check("CFG_A again, 100 deg / 15 mm", 100, 15);
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL " + e);
            failures++;
        }
        finally
        {
            app.CloseDoc(title);
        }
        Console.WriteLine(failures == 0 ? "All checks passed" : failures + " check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
