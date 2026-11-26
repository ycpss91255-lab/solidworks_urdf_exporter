using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.Utilities;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. SolidWorks writes the vertices of a 3DXML file in the frame of the assembly,
    /// and the origins of the mesh in the URDF put them into the frame of the link, wherever its coordinate
    /// system is (issue #212). The export of one link runs on a fake assembly.
    /// </summary>
    public class Test3dxmlPlacement
    {
        private static readonly double[] Identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        // Turned about z by 90 degrees, x goes to y, and about x by 90 degrees, y goes to z. SolidWorks lists
        // the images of the axes one after the other.
        private static readonly double[] AboutZ = { 0, 1, 0, -1, 0, 0, 0, 0, 1 };
        private static readonly double[] AboutX = { 1, 0, 0, 0, 0, 1, 0, -1, 0 };

        // Like MathTransform.Multiply: first this one, then the other one.
        private static MathTransform Transform(double[] rotation, double x, double y, double z)
        {
            Mock<MathTransform> transform = new Mock<MathTransform>();
            transform.SetupGet(t => t.ArrayData).Returns(rotation.Concat(new[] { x, y, z, 1, 0, 0, 0 }).ToArray());
            transform.Setup(t => t.Multiply(It.IsAny<MathTransform>())).Returns((object other) =>
            {
                Matrix<double> m = Pose((MathTransform)other) * Pose(transform.Object);
                return Transform(new[] { m[0, 0], m[1, 0], m[2, 0], m[0, 1], m[1, 1], m[2, 1], m[0, 2], m[1, 2], m[2, 2] },
                    m[0, 3], m[1, 3], m[2, 3]);
            });
            return transform.Object;
        }

        private static Matrix<double> Pose(MathTransform transform)
        {
            double[] d = (double[])transform.ArrayData;
            return DenseMatrix.OfArray(new[,]
            {
                { d[0], d[3], d[6], d[9] },
                { d[1], d[4], d[7], d[10] },
                { d[2], d[5], d[8], d[11] },
                { 0, 0, 0, 1 }
            });
        }

        private static readonly MathTransform FramePlace = Transform(AboutX, 0.5, 0, 0);
        private static readonly MathTransform MainPlace = Transform(AboutZ, 1, 2, 3);
        private static readonly MathTransform OtherPlace = Transform(AboutX, -4, 0, 2);
        private static readonly MathTransform NestedPlace = Transform(AboutZ, 0, 7, 0);

        // The frame of the link in the assembly, for the names the tests use.
        private static readonly Dictionary<string, Matrix<double>> Frames = new Dictionary<string, Matrix<double>>
        {
            { "Frame_top", Pose(FramePlace) },
            { "Frame <MAIN-1>", Pose(MainPlace) * Pose(FramePlace) },
            { "Frame <OTHER-1>", Pose(OtherPlace) * Pose(FramePlace) },
            { "Frame <SUB-1/MAIN-1>", Pose(NestedPlace) * Pose(FramePlace) }
        };

        private static ModelDoc2 Document(Mock<ModelDoc2> document, string frame)
        {
            Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
            extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>()))
                .Returns((string name) => name == frame ? FramePlace : null);
            int errors = 0, warnings = 0;
            extension.Setup(e => e.SaveAs(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<object>(),
                ref errors, ref warnings)).Returns((string path, int version, int options, object data, int e, int w) =>
                {
                    File.WriteAllText(path, "mesh in the frame of the assembly");
                    return true;
                });
            document.SetupGet(d => d.Extension).Returns(extension.Object);
            return document.Object;
        }

        private static Component2 Component(string name, MathTransform place)
        {
            ModelDoc2 document = Document(new Mock<ModelDoc2>(), "Frame");
            Mock<Component2> component = new Mock<Component2>();
            component.SetupGet(c => c.Name2).Returns(name);
            component.SetupGet(c => c.Transform2).Returns(place);
            component.Setup(c => c.GetModelDoc2()).Returns(document);
            return component.Object;
        }

        // Exports the mesh of a link whose main component is MAIN-1, and returns the link.
        private static Link Export(string method, string coordinateSystem, double[] originXyz = null, double[] originRpy = null)
        {
            Component2[] components =
            {
                Component("MAIN-1", MainPlace), Component("OTHER-1", OtherPlace), Component("SUB-1", MainPlace),
                Component("SUB-1/MAIN-1", NestedPlace)
            };
            Mock<ModelDoc2> top = new Mock<ModelDoc2>();
            top.As<AssemblyDoc>().Setup(a => a.GetComponents(false)).Returns(components.Cast<object>().ToArray());
            Document(top, "Frame_top");
            Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
            selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
            top.SetupGet(d => d.SelectionManager).Returns(selection.Object);
            ExportHelper helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
            helper.ActiveSWModel = top.Object;

            Link link = new Link { Name = "arm", SWMainComponent = components[0], SWComponents = new List<Component2>() };
            link.Joint.CoordinateSystemName = coordinateSystem;
            foreach (Origin origin in new[] { link.Visual.Origin, link.Collision.Origin })
            {
                origin.SetXYZ(originXyz ?? new double[3]);
                origin.SetRPY(originRpy ?? new double[3]);
            }
            string path = Path.Combine(Path.GetTempPath(), "sw2urdf-3dxml-" + Guid.NewGuid().ToString("N") + ".3dxml");
            try
            {
                object result = new PrivateObject(helper).Invoke(method, link, path);
                Assert.True(result == null || (bool)result);
                Assert.True(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
            return link;
        }

        private static Matrix<double> Pose(Origin origin)
        {
            return MathOps.GetTransformation(origin.GetXYZ(), origin.GetRPY());
        }

        private static void AssertPose(Matrix<double> expected, Matrix<double> actual, string what)
        {
            Assert.True((expected - actual).Enumerate().All(difference => Math.Abs(difference) < 1e-9),
                what + ": expected\n" + expected + "\ngot\n" + actual);
        }

        private static void AssertOrigins(Matrix<double> expected, Link link, string what)
        {
            AssertPose(expected, Pose(link.Visual.Origin), what + ", visual");
            AssertPose(expected, Pose(link.Collision.Origin), what + ", collision");
        }

        [Fact]
        public void FrameAtTheTopLevel()
        {
            AssertOrigins(Frames["Frame_top"].Inverse(), Export("Save3dxml", "Frame_top"), "top level");
        }

        [Fact]
        public void FrameInTheMovedMainComponent()
        {
            AssertOrigins(Frames["Frame <MAIN-1>"].Inverse(), Export("Save3dxml", "Frame <MAIN-1>"), "main component");
        }

        [Fact]
        public void FrameInAnotherComponent()
        {
            AssertOrigins(Frames["Frame <OTHER-1>"].Inverse(), Export("Save3dxml", "Frame <OTHER-1>"), "other component");
        }

        [Fact]
        public void FrameInANestedComponent()
        {
            AssertOrigins(Frames["Frame <SUB-1/MAIN-1>"].Inverse(), Export("Save3dxml", "Frame <SUB-1/MAIN-1>"), "nested component");
        }

        // The vertices are read back through the origin, as a viewer does: the origin of the frame of the
        // link is at 0 0 0 of the link, and a point in front of it keeps its distance along the same axis.
        [Fact]
        public void VerticesEndUpInTheFrameOfTheLink()
        {
            foreach (string name in Frames.Keys)
            {
                Matrix<double> origin = Pose(Export("Save3dxml", name).Visual.Origin);
                foreach (double[] inLink in new[] { new double[] { 0, 0, 0, 1 }, new[] { 0.1, -0.2, 0.3, 1 } })
                {
                    Vector<double> vertex = Frames[name] * new DenseVector(inLink);
                    Assert.True((origin * vertex - new DenseVector(inLink)).L2Norm() < 1e-9, name);
                }
            }
        }

        [Fact]
        public void OriginsAreTheOnesOfTheObjExport()
        {
            double[] xyz = { 0.01, 0.02, 0.03 };
            double[] rpy = { 0.1, 0.2, 0.3 };
            foreach (string name in Frames.Keys)
            {
                AssertOrigins(Pose(Export("SaveObjSource", name).Visual.Origin), Export("Save3dxml", name), name);
                AssertOrigins(Pose(Export("SaveObjSource", name, xyz, rpy).Visual.Origin),
                    Export("Save3dxml", name, xyz, rpy), name + " with an origin");
            }
        }

        // The origin of the link page moves the mesh in the frame of the link.
        [Fact]
        public void OriginOfTheLinkPageIsInTheFrameOfTheLink()
        {
            double[] xyz = { 0.01, 0.02, 0.03 };
            double[] rpy = { 0.1, 0.2, 0.3 };
            foreach (string name in Frames.Keys)
            {
                AssertOrigins(MathOps.GetTransformation(xyz, rpy) * Frames[name].Inverse(),
                    Export("Save3dxml", name, xyz, rpy), name);
            }
        }

        [Fact]
        public void FrameThatDoesNotExistIsAFailure()
        {
            foreach (string name in new[] { "Frame_gone", "Frame_gone <MAIN-1>" })
            {
                Assert.NotNull(Record.Exception(() => Export("Save3dxml", name)));
            }
        }

        [Fact]
        public void LinkWithoutACoordinateSystemStaysInTheFrameOfTheAssembly()
        {
            AssertOrigins(DenseMatrix.CreateIdentity(4), Export("Save3dxml", ""), "no coordinate system");
        }
    }
}