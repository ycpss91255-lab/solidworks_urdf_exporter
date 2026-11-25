using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Windows;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.Utilities;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The message after an OBJ export that refused a texture names the links and
    /// the reason, and every other failure keeps the general message (issue #206). The export runs on a fake
    /// document; its messages are collected, no window is shown.
    /// </summary>
    public class TestTextureMessage
    {
        private const string General = "The mesh of these links could not be exported: {0}. The URDF was not written " +
            "and the package is incomplete (see {1}). Check the log and the output folder, then export again.";

        private const string Texture = "The OBJ export does not support textures, and these links have one: {0}. " +
            "Export as STL or 3DXML, or remove the texture.";

        private enum Mesh
        {
            Good,
            Textured,
            Broken
        }

        private sealed class Export : IDisposable
        {
            private readonly string directory = Path.Combine(Path.GetTempPath(), "sw2urdf-texture-" + Guid.NewGuid().ToString("N"));
            private readonly IMessageBox messageBox = URDFPackage.MessageBox;
            private readonly List<string> messages = new List<string>();
            private readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
            private readonly ExportHelper helper;
            private readonly UiText ui;

            public Export(string language, Mesh baseMesh, params Mesh[] childMeshes)
            {
                Directory.CreateDirectory(directory);
                ui = new UiText(language);
                Mock<IMessageBox> box = new Mock<IMessageBox>();
                box.Setup(b => b.Show(It.IsAny<string>())).Returns(MessageBoxResult.OK)
                    .Callback((string message) => messages.Add(message));
                URDFPackage.MessageBox = box.Object;
                // The conversion shows its progress in a dialog only while a form is open.
                Assert.Equal(0, System.Windows.Forms.Application.OpenForms.Count);

                Link baseLink = NewLink("base_link", null);
                meshes[baseLink.Name] = baseMesh;
                foreach (Mesh mesh in childMeshes)
                {
                    Link child = NewLink("link_" + meshes.Count, baseLink);
                    child.Joint.Name = "joint_" + meshes.Count;
                    child.Joint.Type = "fixed";
                    child.Joint.Parent.Name = baseLink.Name;
                    child.Joint.Child.Name = child.Name;
                    baseLink.Children.Add(child);
                    meshes[child.Name] = mesh;
                }
                Robot robot = new Robot { Name = "robot" };
                robot.SetBaseLink(baseLink);

                Mock<MathTransform> frame = new Mock<MathTransform>();
                frame.SetupGet(t => t.ArrayData).Returns(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 });
                Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
                extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>())).Returns(frame.Object);
                extension.Setup(e => e.SelectByID2("Origin_global", "COORDSYS", 0, 0, 0, false, 0,
                    It.IsAny<Callout>(), 0)).Returns(true);
                int errors = 0, warnings = 0;
                extension.Setup(e => e.SaveAs(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<object>(),
                    ref errors, ref warnings)).Returns((string path, int version, int options, object data, int e, int w) =>
                    {
                        Write3dxml(path, meshes[Path.GetFileNameWithoutExtension(path)]);
                        return true;
                    });
                Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
                selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                Mock<ModelDoc2> document = new Mock<ModelDoc2>();
                document.SetupGet(d => d.Extension).Returns(extension.Object);
                document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
                document.As<AssemblyDoc>().Setup(a => a.GetComponents(false)).Returns(new object[0]);
                Mock<SldWorks> app = new Mock<SldWorks>();
                UserProgressBar bar = new Mock<UserProgressBar>().Object;
                app.Setup(a => a.GetUserProgressBar(out bar)).Returns(true);
                app.Setup(a => a.GetCurrentLanguage()).Returns(language);

                helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                helper.ActiveSWModel = document.Object;
                helper.URDFRobot = robot;
                helper.PackageName = "package";
                helper.SavePath = directory;
                PrivateObject fields = new PrivateObject(helper);
                fields.SetFieldOrProperty("iSwApp", app.Object);
                fields.SetField("usedMeshFileNames", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                fields.SetField("usedTextureFileNames", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                fields.SetField("textureFileNames", new Dictionary<string, string>());
                fields.SetField("failedMeshLinks", new List<string>());
            }

            private static Link NewLink(string name, Link parent)
            {
                Link link = parent == null ? new Link() : new Link(parent);
                link.Name = name;
                link.SWComponents = new List<Component2>();
                link.Joint.CoordinateSystemName = "Origin_global";
                return link;
            }

            // One triangle, the way SolidWorks writes it.
            private static void Write3dxml(string path, Mesh mesh)
            {
                if (mesh == Mesh.Broken)
                {
                    File.WriteAllText(path, "not a 3DXML file");
                    return;
                }
                bool textured = mesh == Mesh.Textured;
                using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Add(zip, "Manifest.xml", "<Manifest><Root>NameSetTree.3dxml</Root></Manifest>");
                    Add(zip, "NameSetTree.3dxml",
                        "<Model_3dxml xmlns=\"http://www.3ds.com/xsd/3DXML\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                        "<ProductStructure root=\"1\"><Reference3D id=\"1\" name=\"root\"/><Reference3D id=\"2\" name=\"box\"/>" +
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

            // The message after the one about creating the package.
            public string Part()
            {
                helper.ExportLink(true, MeshExportFormat.OBJ);
                return messages.Skip(1).SingleOrDefault();
            }

            public void Change(string link, Mesh mesh)
            {
                meshes[link] = mesh;
            }

            public string Assembly()
            {
                messages.Clear();
                helper.ExportRobot(true, MeshExportFormat.OBJ);
                return messages.Skip(1).SingleOrDefault();
            }

            public string GeneralText(string links)
            {
                return string.Format(ui.Get(General), links, MeshExportCheck.FailureMarkerFileName);
            }

            public string TextureText(string links)
            {
                return string.Format(ui.Get(Texture), links);
            }

            public string LogText()
            {
                return string.Format(ui.Get("Email your maintainer with the log file found at {0}"), Logger.GetFileName());
            }

            public string[] Files(string pattern)
            {
                return Directory.GetFiles(directory, pattern, SearchOption.AllDirectories).Select(Path.GetFileName).ToArray();
            }

            public string Marker()
            {
                return File.ReadAllText(Path.Combine(directory, "package", MeshExportCheck.FailureMarkerFileName));
            }

            public void Dispose()
            {
                URDFPackage.MessageBox = messageBox;
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        private static readonly string[] Languages = { "english", "chinese" };

        [Fact]
        public void PartWithATextureNamesTheReason()
        {
            foreach (string language in Languages)
            {
                using (Export export = new Export(language, Mesh.Textured))
                {
                    Assert.Equal(export.GeneralText("base_link") + "\n" + export.TextureText("base_link"), export.Part());
                    Assert.Empty(export.Files("*.urdf"));
                    Assert.Empty(export.Files("*.obj"));
                    Assert.Contains("base_link", export.Marker());
                }
            }
        }

        [Fact]
        public void AssemblyNamesTheLinksWithATexture()
        {
            foreach (string language in Languages)
            {
                using (Export export = new Export(language, Mesh.Good, Mesh.Textured, Mesh.Good, Mesh.Textured))
                {
                    Assert.Equal(export.GeneralText("link_1, link_3") + "\n" + export.TextureText("link_1, link_3"),
                        export.Assembly());
                    Assert.Empty(export.Files("*.urdf"));
                    Assert.Equal(new[] { "base_link.obj", "link_2.obj" }, export.Files("*.obj").OrderBy(name => name));
                    Assert.Contains("link_1, link_3", export.Marker());
                }
            }
        }

        [Fact]
        public void OtherFailuresKeepTheGeneralMessage()
        {
            foreach (string language in Languages)
            {
                using (Export export = new Export(language, Mesh.Broken))
                {
                    Assert.Equal(export.GeneralText("base_link") + "\n" + export.LogText(), export.Part());
                }
                using (Export export = new Export(language, Mesh.Good, Mesh.Broken))
                {
                    Assert.Equal(export.GeneralText("link_1") + "\n" + export.LogText(), export.Assembly());
                }
            }
        }

        [Fact]
        public void BothReasonsAreNamedWhenBothHappen()
        {
            foreach (string language in Languages)
            {
                using (Export export = new Export(language, Mesh.Textured, Mesh.Broken))
                {
                    Assert.Equal(export.GeneralText("link_1, base_link") + "\n" + export.TextureText("base_link") + "\n" +
                        export.LogText(), export.Assembly());
                }
            }
        }

        [Fact]
        public void SecondExportForgetsTheTexturesOfTheFirst()
        {
            using (Export export = new Export("english", Mesh.Good, Mesh.Textured))
            {
                Assert.Contains("support textures", export.Assembly());
                export.Change("link_1", Mesh.Broken);
                Assert.DoesNotContain("support textures", export.Assembly());
            }
        }

        [Fact]
        public void ReasonIsTranslated()
        {
            Assert.Equal(Texture, new UiText("english").Get(Texture));
            string chinese = new UiText("chinese").Get(Texture);
            Assert.NotEqual(Texture, chinese);
            Assert.Contains("{0}", chinese);
            Assert.Contains("STL", chinese);
            Assert.Contains("3DXML", chinese);
        }
    }
}