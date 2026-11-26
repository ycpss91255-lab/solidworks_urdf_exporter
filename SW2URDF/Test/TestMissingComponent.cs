using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDFExport;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A coordinate system or axis of a component that is gone or suppressed is not
    /// taken from the top level assembly, where one of the same name can be.
    /// </summary>
    public class TestMissingComponent
    {
        private const string Frame = "Frame";
        private const string Axis = "Axis";

        private static MathTransform Transform(double x)
        {
            Mock<MathTransform> transform = new Mock<MathTransform>();
            transform.SetupGet(t => t.ArrayData).Returns(new[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, x, 0, 0, 1, 0, 0, 0 });
            // Only translations here.
            transform.Setup(t => t.Multiply(It.IsAny<MathTransform>())).Returns((object other) =>
                Transform(x + ((double[])((MathTransform)other).ArrayData)[9]));
            return transform.Object;
        }

        // A document with one frame, at x = 1, and one axis, along x.
        private static Mock<ModelDoc2> Document()
        {
            Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
            extension.Setup(e => e.GetCoordinateSystemTransformByName(Frame)).Returns(Transform(1));
            extension.Setup(e => e.SelectByID2(Axis, "AXIS", 0, 0, 0, false, 0, It.IsAny<Callout>(), 0)).Returns(true);
            Mock<RefAxis> axis = new Mock<RefAxis>();
            axis.Setup(a => a.GetRefAxisParams()).Returns(new double[] { 1, 0, 0, 0, 0, 0 });
            Mock<Feature> feature = new Mock<Feature>();
            feature.Setup(f => f.GetSpecificFeature2()).Returns(axis.Object);
            Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
            selection.Setup(s => s.GetSelectedObject6(1, 0)).Returns(feature.Object);
            Mock<ModelDoc2> document = new Mock<ModelDoc2>();
            document.SetupGet(d => d.Extension).Returns(extension.Object);
            document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
            return document;
        }

        private static Component2 Component(string name, ModelDoc2 document, double x)
        {
            Mock<Component2> component = new Mock<Component2>();
            component.SetupGet(c => c.Name2).Returns(name);
            component.SetupGet(c => c.Transform2).Returns(Transform(x));
            component.Setup(c => c.GetModelDoc2()).Returns(document);
            return component.Object;
        }

        // ARM-1 is there, SUPPRESSED-1 has no document, GONE-1 does not exist.
        private static ExportHelper Helper(bool withComponents = true)
        {
            object[] components =
            {
                Component("ARM-1", Document().Object, 5), Component("SUPPRESSED-1", null, 7)
            };
            Mock<ModelDoc2> top = Document();
            top.As<AssemblyDoc>().Setup(a => a.GetComponents(false)).Returns(withComponents ? components : null);
            ExportHelper helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
            helper.ActiveSWModel = top.Object;
            return helper;
        }

        private static double FrameX(ExportHelper helper, string name)
        {
            MathTransform transform = (MathTransform)new PrivateObject(helper).Invoke("GetCoordinateSystemTransform", name);
            return ((double[])transform.ArrayData)[9];
        }

        private static void AssertNamed(string component, Action action)
        {
            Exception e = Record.Exception(action);
            Assert.NotNull(e);
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            Assert.Contains(component, e.Message);
        }

        [Fact]
        public void CoordinateSystemOfAMissingComponentIsNotTheOneOfTheAssembly()
        {
            foreach (string component in new[] { "GONE-1", "SUPPRESSED-1", "SUB-1/ARM-1" })
            {
                AssertNamed(component, () => FrameX(Helper(), Frame + " <" + component + ">"));
            }
            AssertNamed("ARM-1", () => FrameX(Helper(false), Frame + " <ARM-1>"));
        }

        [Fact]
        public void AxisOfAMissingComponentIsNotTheOneOfTheAssembly()
        {
            foreach (string component in new[] { "GONE-1", "SUPPRESSED-1", "SUB-1/ARM-1" })
            {
                AssertNamed(component, () => Helper().EstimateAxis(Axis + " <" + component + ">"));
            }
            AssertNamed("ARM-1", () => Helper(false).EstimateAxis(Axis + " <ARM-1>"));
        }

        [Fact]
        public void ExistingOnesAreReadAsBefore()
        {
            Assert.Equal(1, FrameX(Helper(), Frame));
            Assert.Equal(6, FrameX(Helper(), Frame + " <ARM-1>"));
            Assert.Equal(new double[] { 1, 0, 0 }, Helper().EstimateAxis(Axis));
            Assert.Equal(new double[] { 1, 0, 0 }, Helper().EstimateAxis(Axis + " <ARM-1>"));
        }
    }
}