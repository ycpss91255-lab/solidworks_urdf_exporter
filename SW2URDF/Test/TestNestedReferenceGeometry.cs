using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Moq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. Axes and coordinate systems of components at any depth are offered and can
    /// be used for a joint (issue #204). The code of the exporter runs on a fake assembly: TOP holds two
    /// instances of SUB, SUB holds PART and the sub-assembly INNER, INNER holds PART as well.
    /// </summary>
    public class TestNestedReferenceGeometry
    {
        // A document with reference geometry; a frame or axis has the same place and direction in all of them.
        private sealed class FakeDocument
        {
            public readonly Mock<ModelDoc2> Document = new Mock<ModelDoc2>();
            public readonly Mock<FeatureManager> Features = new Mock<FeatureManager>();
            private string selected;

            public FakeDocument(string path, swDocumentTypes_e type, params string[] featureNames)
            {
                if (type == swDocumentTypes_e.swDocASSEMBLY)
                {
                    Document.As<AssemblyDoc>();
                }
                Document.Setup(d => d.GetType()).Returns((int)type);
                Document.Setup(d => d.GetPathName()).Returns(path);
                Document.Setup(d => d.GetTitle()).Returns(path);
                Document.SetupGet(d => d.FeatureManager).Returns(Features.Object);
                // Every document also has a feature of another type, and "A..." is an axis, "F..." a frame.
                Features.Setup(f => f.GetFeatures(false)).Returns(() => featureNames.Concat(new[] { "Sketch1" })
                    .Select(name => (object)NewFeature(name)).ToArray());

                Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
                extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>()))
                    .Returns((string name) => featureNames.Contains(name) ? Transform(Identity, 1, 0, 0) : null);
                extension.Setup(e => e.SelectByID2(It.IsAny<string>(), "AXIS", 0, 0, 0, false, 0, It.IsAny<Callout>(), 0))
                    .Returns((string name, string kind, double x, double y, double z, bool append, int mark,
                        Callout callout, int option) =>
                    {
                        selected = name;
                        return featureNames.Contains(name);
                    });
                Document.SetupGet(d => d.Extension).Returns(extension.Object);
                Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
                selection.Setup(s => s.GetSelectedObject6(1, 0)).Returns(() => NewFeature(selected));
                Document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
            }

            private static Feature NewFeature(string name)
            {
                Mock<RefAxis> axis = new Mock<RefAxis>();
                // From the end to the start: along x.
                axis.Setup(a => a.GetRefAxisParams()).Returns(new double[] { 1, 0, 0, 0, 0, 0 });
                Mock<Feature> feature = new Mock<Feature>();
                feature.SetupGet(f => f.Name).Returns(name);
                feature.Setup(f => f.GetTypeName2()).Returns(
                    name[0] == 'A' ? "RefAxis" : name[0] == 'F' ? "CoordSys" : "ProfileFeature");
                feature.Setup(f => f.GetSpecificFeature2()).Returns(axis.Object);
                return feature.Object;
            }
        }

        private static readonly double[] Identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        // Turned about z by 90 degrees: x goes to y. SolidWorks lists the rows of the images of the axes.
        private static readonly double[] Turned = { 0, 1, 0, -1, 0, 0, 0, 0, 1 };

        private static MathTransform Transform(double[] rotation, double x, double y, double z)
        {
            double[] data = rotation.Concat(new[] { x, y, z, 1, 0, 0, 0 }).ToArray();
            Mock<MathTransform> transform = new Mock<MathTransform>();
            transform.SetupGet(t => t.ArrayData).Returns(data);
            // Multiply(other): first this one, then the other one.
            transform.Setup(t => t.Multiply(It.IsAny<MathTransform>())).Returns((object other) =>
            {
                double[] b = (double[])((MathTransform)other).ArrayData;
                double[] r = new double[9];
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        r[i * 3 + j] = data[i * 3] * b[j] + data[i * 3 + 1] * b[3 + j] + data[i * 3 + 2] * b[6 + j];
                    }
                }
                double[] p = new double[3];
                for (int j = 0; j < 3; j++)
                {
                    p[j] = data[9] * b[j] + data[10] * b[3 + j] + data[11] * b[6 + j] + b[9 + j];
                }
                return Transform(r, p[0], p[1], p[2]);
            });
            return transform.Object;
        }

        private sealed class FakeAssembly
        {
            public readonly FakeDocument Top = new FakeDocument("top", swDocumentTypes_e.swDocASSEMBLY, "Axis_top", "Frame_top");
            public readonly FakeDocument Sub = new FakeDocument("sub", swDocumentTypes_e.swDocASSEMBLY, "Frame_sub");
            public readonly FakeDocument Inner = new FakeDocument("inner", swDocumentTypes_e.swDocASSEMBLY);
            public readonly FakeDocument Part = new FakeDocument("part", swDocumentTypes_e.swDocPART, "Axis", "Frame");
            public readonly ExportHelper Helper;
            private readonly List<Component2> components = new List<Component2>();
            private readonly PrivateObject fields;

            public FakeAssembly()
            {
                Mock<AssemblyDoc> assembly = Top.Document.As<AssemblyDoc>();
                assembly.Setup(a => a.GetComponents(true)).Returns(() =>
                    components.Where(c => !c.Name2.Contains("/")).Select(c => (object)c).ToArray());
                assembly.Setup(a => a.GetComponents(false)).Returns(() => components.Select(c => (object)c).ToArray());
                Helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                Helper.ActiveSWModel = Top.Document.Object;
                fields = new PrivateObject(Helper);
                fields.SetField("ReferenceCoordinateSystemNames", new List<string>());
                fields.SetField("ReferenceAxesNames", new List<string>());
            }

            public void Add(string name, FakeDocument document, MathTransform place)
            {
                Mock<Component2> component = new Mock<Component2>();
                component.SetupGet(c => c.Name2).Returns(name);
                component.SetupGet(c => c.Transform2).Returns(place);
                component.Setup(c => c.GetModelDoc2()).Returns(document?.Document.Object);
                components.Add(component.Object);
            }

            public void AddAll()
            {
                foreach (int number in new[] { 1, 2 })
                {
                    string sub = "SUB-" + number;
                    MathTransform place = number == 1 ? Transform(Identity, 0, 0, 5) : Transform(Turned, 10, 0, 0);
                    Add(sub, Sub, place);
                    Add(sub + "/PART-1", Part, place);
                    Add(sub + "/INNER-1", Inner, place);
                    Add(sub + "/INNER-1/PART-1", Part, place);
                }
            }

            public object Invoke(string method, params object[] arguments)
            {
                return fields.Invoke(method, arguments);
            }
        }

        [Fact]
        public void AxesAtEveryDepthAreOffered()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.AddAll();
            assembly.Helper.UpdateReferenceGeometries();
            Assert.Equal(new[]
            {
                "Axis_top", "Axis <SUB-1/PART-1>", "Axis <SUB-1/INNER-1/PART-1>",
                "Axis <SUB-2/PART-1>", "Axis <SUB-2/INNER-1/PART-1>"
            }, assembly.Helper.GetRefAxes());
        }

        [Fact]
        public void CoordinateSystemsAtEveryDepthAreOffered()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.AddAll();
            assembly.Helper.UpdateReferenceGeometries();
            Assert.Equal(new[]
            {
                "Frame_top", "Frame_sub <SUB-1>", "Frame <SUB-1/PART-1>", "Frame <SUB-1/INNER-1/PART-1>",
                "Frame_sub <SUB-2>", "Frame <SUB-2/PART-1>", "Frame <SUB-2/INNER-1/PART-1>"
            }, assembly.Helper.GetRefCoordinateSystems());
        }

        [Fact]
        public void SavedNestedNamesAreKept()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.AddAll();
            assembly.Helper.UpdateReferenceGeometries();
            Link link = new Link { Name = "wheel" };
            link.Joint.Type = "continuous";
            link.Joint.CoordinateSystemName = "Frame <SUB-2/INNER-1/PART-1>";
            link.Joint.AxisName = "Axis <SUB-2/INNER-1/PART-1>";
            assembly.Invoke("CheckRefGeometryExists", link);
            Assert.Equal("Frame <SUB-2/INNER-1/PART-1>", link.Joint.CoordinateSystemName);
            Assert.Equal("Axis <SUB-2/INNER-1/PART-1>", link.Joint.AxisName);

            link.Joint.CoordinateSystemName = "Frame <SUB-3/PART-1>";
            link.Joint.AxisName = "Axis <PART-1>";
            assembly.Invoke("CheckRefGeometryExists", link);
            Assert.Equal("Automatically Generate", link.Joint.CoordinateSystemName);
            Assert.Equal("Automatically Generate", link.Joint.AxisName);
        }

        [Fact]
        public void PlaceIsTheOneOfTheInstance()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.AddAll();
            Assert.Equal(new double[] { 1, 0, 0 }, assembly.Helper.EstimateAxis("Axis <SUB-1/PART-1>"));
            Assert.Equal(new double[] { 0, 1, 0 }, assembly.Helper.EstimateAxis("Axis <SUB-2/PART-1>"));
            Assert.Equal(new double[] { 0, 1, 0 }, assembly.Helper.EstimateAxis("Axis <SUB-2/INNER-1/PART-1>"));
            double[] first = (double[])((MathTransform)assembly.Invoke("GetCoordinateSystemTransform", "Frame <SUB-1/PART-1>")).ArrayData;
            double[] second = (double[])((MathTransform)assembly.Invoke("GetCoordinateSystemTransform", "Frame <SUB-2/PART-1>")).ArrayData;
            // The frame is at (1, 0, 0) of the part.
            Assert.Equal(new double[] { 1, 0, 5 }, first.Skip(9).Take(3));
            Assert.Equal(new double[] { 10, 1, 0 }, second.Skip(9).Take(3));
        }

        [Fact]
        public void ComponentsThatWereResolvedSinceAreFoundWhenTheRobotIsBuilt()
        {
            // The lists are read when the exporter is made, before lightweight components are resolved.
            FakeAssembly assembly = new FakeAssembly();
            assembly.AddAll();
            PrivateObject fields = new PrivateObject(assembly.Helper);
            fields.SetField("progressBar", new Mock<UserProgressBar>().Object);
            Link baseLink = new Link { Name = "base_link", isFixedFrame = true };
            baseLink.Joint.CoordinateSystemName = "Frame <SUB-1/INNER-1/PART-1>";
            LinkNode node = new LinkNode { Link = baseLink, IsBaseNode = true };
            Assert.True(assembly.Helper.CreateRobotFromTreeView(node));
            Assert.Equal("Frame <SUB-1/INNER-1/PART-1>", node.Link.Joint.CoordinateSystemName);
        }

        [Fact]
        public void NamesWithChineseCharactersAndSpacesAreOffered()
        {
            string wheel = new string(new[] { (char)0x8F2A, (char)0x5B50 });
            FakeAssembly assembly = new FakeAssembly();
            FakeDocument document = new FakeDocument("wheel", swDocumentTypes_e.swDocPART, "Axis " + wheel);
            string component = wheel + " SUB-1/" + wheel + "-2";
            assembly.Add(wheel + " SUB-1", assembly.Sub, Transform(Turned, 0, 0, 0));
            assembly.Add(component, document, Transform(Turned, 0, 0, 0));
            assembly.Helper.UpdateReferenceGeometries();
            string name = "Axis " + wheel + " <" + component + ">";
            Assert.Contains(name, assembly.Helper.GetRefAxes());
            Assert.Equal(new double[] { 0, 1, 0 }, assembly.Helper.EstimateAxis(name));
        }

        [Fact]
        public void ComponentsWithoutADocumentAreLeftOut()
        {
            // Suppressed and lightweight components have none.
            FakeAssembly assembly = new FakeAssembly();
            assembly.Add("SUB-1", assembly.Sub, Transform(Identity, 0, 0, 0));
            assembly.Add("SUB-1/PART-1", null, Transform(Identity, 0, 0, 0));
            assembly.Add("SUB-1/PART-2", assembly.Part, Transform(Identity, 0, 0, 0));
            assembly.Helper.UpdateReferenceGeometries();
            Assert.Equal(new[] { "Axis_top", "Axis <SUB-1/PART-2>" }, assembly.Helper.GetRefAxes());
        }

        [Fact]
        public void EveryDocumentIsSearchedOnce()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.AddAll();
            assembly.Helper.UpdateReferenceGeometries();
            foreach (FakeDocument document in new[] { assembly.Top, assembly.Sub, assembly.Inner, assembly.Part })
            {
                document.Features.Verify(f => f.GetFeatures(It.IsAny<bool>()), Times.Once());
            }
            // The next search sees what changed.
            assembly.Helper.UpdateReferenceGeometries();
            assembly.Part.Features.Verify(f => f.GetFeatures(It.IsAny<bool>()), Times.Exactly(2));
        }

        [Fact]
        public void DocumentsWithoutAPathAreNotTakenForOneAnother()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.Add("ONE-1", new FakeDocument("", swDocumentTypes_e.swDocPART, "Axis_1"), Transform(Identity, 0, 0, 0));
            assembly.Add("TWO-1", new FakeDocument("", swDocumentTypes_e.swDocPART, "Axis_2"), Transform(Identity, 0, 0, 0));
            assembly.Helper.UpdateReferenceGeometries();
            Assert.Equal(new[] { "Axis_top", "Axis_1 <ONE-1>", "Axis_2 <TWO-1>" }, assembly.Helper.GetRefAxes());
        }

        [Fact]
        public void PartAndEmptyAssemblyOfferTheirOwn()
        {
            FakeAssembly assembly = new FakeAssembly();
            assembly.Top.Document.As<AssemblyDoc>().Setup(a => a.GetComponents(It.IsAny<bool>())).Returns((object)null);
            assembly.Helper.UpdateReferenceGeometries();
            Assert.Equal(new[] { "Axis_top" }, assembly.Helper.GetRefAxes());
            assembly.Helper.ActiveSWModel = assembly.Part.Document.Object;
            assembly.Helper.UpdateReferenceGeometries();
            Assert.Equal(new[] { "Axis" }, assembly.Helper.GetRefAxes());
            Assert.Equal(new[] { "Frame" }, assembly.Helper.GetRefCoordinateSystems());
        }
    }
}