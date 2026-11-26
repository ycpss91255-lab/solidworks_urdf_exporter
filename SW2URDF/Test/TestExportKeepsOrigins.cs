using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The URDF holds the pose of the link frame for meshes that SolidWorks writes in
    /// the frame of the model. That is part of the file, not of the link: exporting again must give the same.
    /// </summary>
    public class TestExportKeepsOrigins
    {
        private static FakeExport NewExport()
        {
            FakeExport export = new FakeExport("base_link", "arm");
            export.OnSaveAs = path =>
            {
                if (Path.GetExtension(path) == ".3dxml") Write3dxml(path);
                else FakeExport.WriteStl(path);
                return true;
            };
            Link arm = export.Robot.BaseLink.Children[0];
            arm.Visual.Origin.SetXYZ(new[] { 0.01, 0.02, 0.03 });
            arm.Visual.Origin.SetRPY(new[] { 0.1, 0.2, 0.3 });
            arm.Collision.Origin.SetXYZ(new[] { 0.04, 0.05, 0.06 });
            foreach (Link link in new[] { export.Robot.BaseLink, arm })
            {
                link.Inertial.Origin.SetXYZ(new[] { 1.0, 2.0, 3.0 });
                link.Inertial.Inertia.SetUrdfMomentMatrix(new[] { 1.0, 0.1, 0.2, 0.1, 2.0, 0.3, 0.2, 0.3, 3.0 });
            }
            return export;
        }

        private static string State(FakeExport export)
        {
            return string.Join("; ", new[] { export.Robot.BaseLink }.Concat(export.Robot.BaseLink.Children).Select(link =>
                string.Join(" ", link.Visual.Origin.GetXYZ().Concat(link.Visual.Origin.GetRPY())
                    .Concat(link.Collision.Origin.GetXYZ()).Concat(link.Collision.Origin.GetRPY())
                    .Concat(link.Inertial.Origin.GetXYZ()).Concat(link.Inertial.Origin.GetRPY())
                    .Concat(link.Inertial.Inertia.GetMoment()))));
        }

        private static void Write3dxml(string path)
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
                    "<VertexBuffer><Positions>0 0 0,1 0 0,0 1 0</Positions><Normals>0 0 1,0 0 1,0 0 1</Normals></VertexBuffer>" +
                    "</Rep></Root></XMLRepresentation>");
            }
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string ExportRobot(FakeExport export, MeshExportFormat format)
        {
            export.Helper.ExportRobot(true, format);
            Assert.Single(export.Messages);
            export.Messages.Clear();
            return File.ReadAllText(export.UrdfFile);
        }

        private static string ExportLink(FakeExport export, MeshExportFormat format)
        {
            export.Helper.ExportLink(true, format);
            Assert.Single(export.Messages);
            export.Messages.Clear();
            return File.ReadAllText(export.UrdfFile);
        }

        private static readonly MeshExportFormat[] Formats =
            { MeshExportFormat.STL, MeshExportFormat.OBJ, MeshExportFormat.THREEDXML };

        [Fact]
        public void LinksAreUnchangedAfterAnExport()
        {
            foreach (MeshExportFormat format in Formats)
            {
                using (FakeExport export = NewExport())
                {
                    string before = State(export);
                    ExportRobot(export, format);
                    Assert.Equal(before, State(export));
                }
            }
        }

        [Fact]
        public void SecondExportIsTheSame()
        {
            foreach (MeshExportFormat format in Formats)
            {
                using (FakeExport export = NewExport())
                {
                    Assert.Equal(ExportRobot(export, format), ExportRobot(export, format));
                }
            }
        }

        [Fact]
        public void StlAfterAnotherFormatIsTheSame()
        {
            string expected;
            using (FakeExport export = NewExport())
            {
                expected = ExportRobot(export, MeshExportFormat.STL);
            }
            foreach (MeshExportFormat first in new[] { MeshExportFormat.OBJ, MeshExportFormat.THREEDXML })
            {
                using (FakeExport export = NewExport())
                {
                    ExportRobot(export, first);
                    Assert.Equal(expected, ExportRobot(export, MeshExportFormat.STL));
                }
            }
        }

        // The export form stays open when the URDF cannot be written, and the user tries again.
        [Fact]
        public void ExportAfterAUrdfThatCouldNotBeWrittenIsTheSame()
        {
            foreach (MeshExportFormat format in Formats)
            {
                string expected;
                using (FakeExport export = NewExport())
                {
                    expected = ExportRobot(export, format);
                }
                using (FakeExport export = NewExport())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(export.UrdfFile));
                    using (new FileStream(export.UrdfFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        Assert.NotNull(Record.Exception(() => export.Helper.ExportRobot(true, format)));
                    }
                    export.Messages.Clear();
                    Assert.Equal(expected, ExportRobot(export, format));
                }
            }
        }

        [Fact]
        public void LinksAreUnchangedAfterAFailedMesh()
        {
            foreach (MeshExportFormat format in Formats)
            {
                using (FakeExport export = NewExport())
                {
                    string before = State(export);
                    export.OnSaveAs = path => false;
                    export.Helper.ExportRobot(true, format);
                    Assert.Equal(before, State(export));
                }
            }
        }

        // The frame of the fake assembly is at 0.1 0.2 0.3, which the URDF takes back.
        [Fact]
        public void UrdfHoldsThePoseOfTheFrame()
        {
            foreach (MeshExportFormat format in new[] { MeshExportFormat.OBJ, MeshExportFormat.THREEDXML })
            {
                using (FakeExport export = NewExport())
                {
                    XElement link = XDocument.Parse(ExportRobot(export, format)).Root.Elements("link").First();
                    foreach (string element in new[] { "visual", "collision" })
                    {
                        Assert.Equal("-0.1 -0.2 -0.3", (string)link.Element(element).Element("origin").Attribute("xyz"));
                    }
                }
            }
        }

        [Fact]
        public void PartIsUnchangedAfterAnExport()
        {
            foreach (MeshExportFormat format in new[] { MeshExportFormat.STL, MeshExportFormat.OBJ })
            {
                using (FakeExport export = NewExport())
                {
                    string before = State(export);
                    ExportLink(export, format);
                    Assert.Equal(before, State(export));
                }
            }
        }

        [Fact]
        public void SecondPartExportIsTheSame()
        {
            foreach (MeshExportFormat format in new[] { MeshExportFormat.STL, MeshExportFormat.OBJ })
            {
                using (FakeExport export = NewExport())
                {
                    Assert.Equal(ExportLink(export, format), ExportLink(export, format));
                }
            }
        }

        [Fact]
        public void PartUrdfHoldsThePoseOfTheFrame()
        {
            using (FakeExport export = NewExport())
            {
                XElement link = XDocument.Parse(ExportLink(export, MeshExportFormat.STL)).Root.Elements("link").First();
                Assert.Equal("0.9 1.8 2.7", (string)link.Element("inertial").Element("origin").Attribute("xyz"));
                Assert.Equal("-0.1 -0.2 -0.3", (string)link.Element("visual").Element("origin").Attribute("xyz"));
            }
        }
    }
}
