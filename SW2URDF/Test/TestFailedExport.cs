using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. An export tells whether the package is complete, and the form stays open with
    /// the generated features when it is not, so that the export can be repeated (issue #255). The messages are
    /// collected, the forms are never shown.
    /// </summary>
    public class TestFailedExport
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Every test runs where no dialog can be created; one that wants a dialog fails.
        private sealed class NoDialogs : IDisposable
        {
            private readonly HookProc proc;
            private readonly IntPtr hook;
            private int count;

            public NoDialogs()
            {
                proc = Handle;
                hook = SetWindowsHookEx(5, proc, IntPtr.Zero, GetCurrentThreadId());
                Assert.NotEqual(IntPtr.Zero, hook);
            }

            private IntPtr Handle(int code, IntPtr window, IntPtr data)
            {
                StringBuilder name = new StringBuilder(64);
                if (code == 3 && GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770")
                {
                    count++;
                    return new IntPtr(1);
                }
                return CallNextHookEx(hook, code, window, data);
            }

            public void Dispose()
            {
                UnhookWindowsHookEx(hook);
                Assert.Equal(0, count);
            }

            private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hook);

            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();
        }

        private enum Mesh
        {
            Good,
            Textured,
            NotSaved,
            Throws
        }

        // An assembly of a base link and an arm, or the base link as a part. The mesh of the last link is the
        // one that can fail.
        private sealed class Export : IDisposable
        {
            private readonly NoDialogs noDialogs = new NoDialogs();
            public readonly FakeExport Fake;
            public Mesh Mesh = Mesh.Good;

            public Export(bool part)
            {
                Fake = part ? new FakeExport("base_link") : new FakeExport("base_link", "arm");
                Part = part;
                string failing = part ? "base_link" : "arm";
                Fake.OnSaveAs = path =>
                {
                    Mesh mesh = Path.GetFileNameWithoutExtension(path) == failing ? Mesh : Mesh.Good;
                    if (mesh == Mesh.Throws) throw new InvalidOperationException("SolidWorks failed");
                    if (mesh == Mesh.NotSaved) return false;
                    if (Path.GetExtension(path) == ".3dxml") Write3dxml(path, mesh == Mesh.Textured);
                    else FakeExport.WriteStl(path);
                    return true;
                };
                foreach (Link link in new[] { Fake.Robot.BaseLink }.Concat(Fake.Robot.BaseLink.Children))
                {
                    link.Visual.Origin.SetXYZ(new[] { 0.01, 0.02, 0.03 });
                    link.Inertial.Origin.SetXYZ(new[] { 1.0, 2.0, 3.0 });
                    link.Inertial.Inertia.SetUrdfMomentMatrix(new[] { 1.0, 0.1, 0.2, 0.1, 2.0, 0.3, 0.2, 0.3, 3.0 });
                }
            }

            public bool Part { get; }

            public ExportHelper Helper => Fake.Helper;

            // What the export returns. No value before it returned one.
            public object Run(MeshExportFormat format, bool exportMeshes = true)
            {
                string method = Part ? "ExportLink" : "ExportRobot";
                Type[] types = { typeof(bool), typeof(MeshExportFormat) };
                return typeof(ExportHelper).GetMethod(method, types).Invoke(Helper, new object[] { exportMeshes, format });
            }

            public string Urdf()
            {
                return File.Exists(Fake.UrdfFile) ? File.ReadAllText(Fake.UrdfFile) : null;
            }

            public bool Marked => File.Exists(Path.Combine(Fake.Directory, "package", MeshExportCheck.FailureMarkerFileName));

            // A coordinate system the preview created.
            public void Generate()
            {
                Helper.GeneratedFeatures.AddCreated(GeneratedKind.CoordinateSystem, "Origin_arm", new Mock<Feature>().Object);
            }

            public bool Generated => !Helper.GeneratedFeatures.IsEmpty;

            private static void Write3dxml(string path, bool textured)
            {
                File.Delete(path);
                using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Add(zip, "Manifest.xml", "<Manifest><Root>NameSetTree.3dxml</Root></Manifest>");
                    Add(zip, "NameSetTree.3dxml",
                        "<Model_3dxml xmlns=\"http://www.3ds.com/xsd/3DXML\"><ProductStructure root=\"1\">" +
                        "<Reference3D id=\"1\" name=\"root\"/><Reference3D id=\"2\" name=\"box\"/>" +
                        "<ReferenceRep id=\"3\" format=\"TESSELLATED\" associatedFile=\"urn:3DXML:Tess_3.3DRep\"/>" +
                        "<InstanceRep id=\"4\"><IsAggregatedBy>2</IsAggregatedBy><IsInstanceOf>3</IsInstanceOf></InstanceRep>" +
                        "<Instance3D id=\"10\" name=\"box-1\"><IsAggregatedBy>1</IsAggregatedBy><IsInstanceOf>2</IsInstanceOf>" +
                        "<RelativeMatrix>1 0 0 0 1 0 0 0 1 0 0 0</RelativeMatrix></Instance3D></ProductStructure></Model_3dxml>");
                    Add(zip, "Tess_3.3DRep",
                        "<XMLRepresentation xmlns=\"http://www.3ds.com/xsd/3DXML\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                        "<Root xsi:type=\"BagRepType\"><Rep xsi:type=\"PolygonalRepType\"><Faces><Face triangles=\"0 1 2\"/></Faces>" +
                        "<VertexBuffer><Positions>0 0 0,1 0 0,0 1 0</Positions><Normals>0 0 1,0 0 1,0 0 1</Normals>" +
                        (textured ? "<TextureCoordinates dimension=\"2\">0 0,0 0,0 0</TextureCoordinates>" : "") + "</VertexBuffer>" +
                        "<SurfaceAttributes><MaterialApplication><MaterialId id=\"urn:3DXML:CATMaterialRef.3dxml#0\"/>" +
                        "</MaterialApplication></SurfaceAttributes></Rep></Root></XMLRepresentation>");
                    Add(zip, "CATMaterialRef.3dxml",
                        "<Model_3dxml xmlns=\"http://www.3ds.com/xsd/3DXML\"><CATMaterialRef><CATMatReference id=\"0\" name=\"Material_0\"/>" +
                        "<MaterialDomain id=\"1\" name=\"Rendering\" format=\"TECHREP\" associatedFile=\"urn:3DXML:MaterialDef_0.3DRep\"/>" +
                        "<MaterialDomainInstance id=\"2\" name=\"Material_0.1\"><IsAggregatedBy>0</IsAggregatedBy>" +
                        "<IsInstanceOf>1</IsInstanceOf></MaterialDomainInstance></CATMaterialRef></Model_3dxml>");
                    Add(zip, "MaterialDef_0.3DRep",
                        "<Osm><Feature Alias=\"RenderingFeature\" Id=\"2\"><Attr Name=\"DiffuseColor\" Type=\"double\" Value=\"[0.25,0.5,0.75]\"/>" +
                        (textured ? "<Attr Name=\"TextureImage\" Type=\"string\" Value=\"urn:3DXML:Image.3dxml#1\"/>" : "") + "</Feature></Osm>");
                }
            }

            private static void Add(ZipArchive zip, string name, string content)
            {
                using (StreamWriter writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
                {
                    writer.Write(content);
                }
            }

            public void Dispose()
            {
                Fake.Dispose();
                noDialogs.Dispose();
            }
        }

        private static readonly bool[] PartOrNot = { false, true };

        // One of the forms without its constructor, which needs SolidWorks. It has a window, so that closing it
        // raises its events, but is never shown.
        private sealed class FormOf : IDisposable
        {
            private readonly Form form;
            private readonly Type type;

            public FormOf(Export export)
            {
                type = export.Part ? typeof(PartExportForm) : typeof(AssemblyExportForm);
                form = (Form)FormatterServices.GetUninitializedObject(type);
                typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
                Set("ui", new UiText("english"));
                type.GetMethod("InitializeComponent", Members).Invoke(form, null);
                Set("Exporter", export.Helper);
                if (!export.Part)
                {
                    Set("ActiveSWModel", export.Fake.Document.Object);
                }
                Assert.NotEqual(IntPtr.Zero, form.Handle);
                Assert.Equal(0, Application.OpenForms.Count);
            }

            private void Set(string name, object value)
            {
                type.GetField(name, Members).SetValue(form, value);
            }

            public void Export(MeshExportFormat format, bool first = true)
            {
                try
                {
                    type.GetMethod("ExportAndClose", Members).Invoke(form, new object[] { first, format });
                }
                catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException)
                {
                    // Until it is not complete instead.
                }
            }

            public bool IsOpen => !form.IsDisposed;

            public void Close()
            {
                form.Close();
            }

            public void Dispose()
            {
                form.Dispose();
            }
        }

        [Fact]
        public void CompletePackageIsTold()
        {
            foreach (bool part in PartOrNot)
            {
                foreach (MeshExportFormat format in new[] { MeshExportFormat.STL, MeshExportFormat.OBJ })
                {
                    using (Export export = new Export(part))
                    {
                        Assert.Equal((object)true, export.Run(format));
                        Assert.NotNull(export.Urdf());
                    }
                }
            }
        }

        [Fact]
        public void ModelFileOnlyIsCompleteWithoutMeshes()
        {
            using (Export export = new Export(false))
            {
                export.Mesh = Mesh.NotSaved;
                Assert.Equal((object)true, export.Run(MeshExportFormat.STL, false));
            }
        }

        [Fact]
        public void FailedMeshIsNotComplete()
        {
            foreach (bool part in PartOrNot)
            {
                foreach (Mesh mesh in new[] { Mesh.NotSaved, Mesh.Throws })
                {
                    foreach (MeshExportFormat format in new[] { MeshExportFormat.STL, MeshExportFormat.OBJ })
                    {
                        // The assembly export waits for an STL file that SolidWorks did not write.
                        if (mesh == Mesh.NotSaved && format == MeshExportFormat.STL && !part) continue;
                        using (Export export = new Export(part) { Mesh = mesh })
                        {
                            Assert.Equal((object)false, export.Run(format));
                            Assert.Null(export.Urdf());
                            Assert.True(export.Marked);
                        }
                    }
                }
            }
        }

        [Fact]
        public void CancelledConversionIsNotComplete()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part))
                {
                    CancellationTokenSource cancel = new CancellationTokenSource();
                    cancel.Cancel();
                    export.Helper.CancellationToken = cancel.Token;
                    Assert.Equal((object)false, export.Run(MeshExportFormat.OBJ));
                    Assert.Null(export.Urdf());
                }
            }
        }

        // The message about a texture says to export as STL.
        [Fact]
        public void StlAfterARefusedTextureIsTheSameAsAFirstExport()
        {
            foreach (bool part in PartOrNot)
            {
                string expected;
                using (Export export = new Export(part) { Mesh = Mesh.Textured })
                {
                    Assert.Equal((object)true, export.Run(MeshExportFormat.STL));
                    expected = export.Urdf();
                }
                using (Export export = new Export(part) { Mesh = Mesh.Textured })
                {
                    Assert.Equal((object)false, export.Run(MeshExportFormat.OBJ));
                    Assert.Null(export.Urdf());
                    Assert.Equal((object)true, export.Run(MeshExportFormat.STL));
                    Assert.Equal(expected, export.Urdf());
                    Assert.False(export.Marked);
                }
            }
        }

        [Fact]
        public void EveryExportIsJudgedOnItsOwn()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part))
                {
                    Assert.Equal((object)true, export.Run(MeshExportFormat.STL));
                    export.Mesh = Mesh.Throws;
                    Assert.Equal((object)false, export.Run(MeshExportFormat.STL));
                    export.Mesh = Mesh.Good;
                    Assert.Equal((object)true, export.Run(MeshExportFormat.STL));
                }
            }
        }

        [Fact]
        public void FormClosesAfterACompletedExportAndKeepsTheFeatures()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part))
                using (FormOf form = new FormOf(export))
                {
                    export.Generate();
                    form.Export(MeshExportFormat.STL);
                    Assert.False(form.IsOpen);
                    Assert.True(export.Generated);
                }
            }
        }

        [Fact]
        public void FormStaysOpenAfterAFailedExportAndDiscardsTheFeaturesWhenItIsClosed()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part) { Mesh = Mesh.Throws })
                using (FormOf form = new FormOf(export))
                {
                    export.Generate();
                    form.Export(MeshExportFormat.STL);
                    Assert.True(form.IsOpen);
                    Assert.True(export.Generated);
                    form.Close();
                    Assert.False(form.IsOpen);
                    Assert.False(export.Generated);
                }
            }
        }

        [Fact]
        public void FormStaysOpenAfterACancelledConversion()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part))
                using (FormOf form = new FormOf(export))
                {
                    CancellationTokenSource cancel = new CancellationTokenSource();
                    cancel.Cancel();
                    export.Helper.CancellationToken = cancel.Token;
                    export.Generate();
                    form.Export(MeshExportFormat.OBJ);
                    Assert.True(form.IsOpen);
                    Assert.True(export.Generated);
                }
            }
        }

        [Fact]
        public void ExportThatIsRepeatedInTheFormClosesIt()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part) { Mesh = Mesh.Textured })
                using (FormOf form = new FormOf(export))
                {
                    export.Generate();
                    form.Export(MeshExportFormat.OBJ);
                    Assert.True(form.IsOpen);
                    form.Export(MeshExportFormat.STL);
                    Assert.False(form.IsOpen);
                    Assert.True(export.Generated);
                    Assert.NotNull(export.Urdf());
                }
            }
        }

        [Fact]
        public void FeaturesAreDiscardedWhenTheFormIsClosedWithoutAnExport()
        {
            foreach (bool part in PartOrNot)
            {
                using (Export export = new Export(part))
                using (FormOf form = new FormOf(export))
                {
                    export.Generate();
                    form.Close();
                    Assert.False(export.Generated);
                }
            }
        }

        // The base frame of a part is made for the direction the form asks for, and an existing one is used
        // again: the one of the failed export must not stay when the other direction is chosen.
        [Fact]
        public void PartThatIsTurnedAfterAFailedExportGetsANewBaseFrame()
        {
            using (Export export = new Export(true) { Mesh = Mesh.NotSaved })
            using (FormOf form = new FormOf(export))
            {
                export.Generate();
                form.Export(MeshExportFormat.STL, true);
                form.Export(MeshExportFormat.STL, true);
                Assert.True(export.Generated);
                form.Export(MeshExportFormat.STL, false);
                Assert.False(export.Generated);
            }
        }
    }
}