using System.Collections.Generic;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;
using PrivateType = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateType;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A document that is not loaded, or that reports no appearance, leaves the colour
    /// of the link as it is instead of ending the export.
    /// </summary>
    public class TestMissingAppearance
    {
        private static readonly PrivateType Helper = new PrivateType(typeof(ExportHelper));

        // [ R, G, B, Ambient, Diffuse, Specular, Shininess, Transparency, Emission ]
        private static readonly double[] Appearance = { 0.1, 0.2, 0.3, 1, 1, 0.5, 0.4, 0.75, 0 };

        // What the API can return instead of the nine values.
        private static readonly object[] NoAppearance = { null, new double[0], new double[] { 1, 1, 1 }, "" };

        private static Mock<ModelDoc2> Model(object appearance)
        {
            Mock<ModelDoc2> model = new Mock<ModelDoc2>();
            model.SetupGet(m => m.MaterialPropertyValues).Returns(appearance);
            model.Setup(m => m.GetTitle()).Returns("part");
            return model;
        }

        private static Link Assembly(object document)
        {
            Mock<Component2> component = new Mock<Component2>();
            component.Setup(c => c.GetModelDoc2()).Returns(document);
            Link link = new Link { Name = "arm", SWComponents = new List<Component2> { component.Object } };
            link.Visual.Material.Color.SetColor(new[] { 0.5, 0.6, 0.7, 0.8 });
            link.Visual.Origin.SetXYZ(new double[] { 1, 2, 3 });
            Helper.InvokeStatic("ComputeVisualCollisionProperties", link);
            return link;
        }

        private static Link Part(object appearance)
        {
            Mock<MassProperty> mass = new Mock<MassProperty>();
            mass.SetupGet(m => m.Mass).Returns(2);
            mass.SetupGet(m => m.CenterOfMass).Returns(new double[3]);
            mass.Setup(m => m.GetMomentOfInertia(It.IsAny<int>())).Returns(new double[9]);
            Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
            extension.Setup(e => e.CreateMassProperty()).Returns(mass.Object);
            Mock<ModelDoc2> model = Model(appearance);
            model.SetupGet(m => m.Extension).Returns(extension.Object);
            return (Link)Helper.InvokeStatic("CreateLinkFromPartModel", model.Object);
        }

        private static void AssertColor(Link link, double red, double green, double blue, double alpha)
        {
            Color color = link.Visual.Material.Color;
            Assert.Equal(new[] { red, green, blue, alpha }, new[] { color.Red, color.Green, color.Blue, color.Alpha });
        }

        [Fact]
        public void AppearanceOfTheFirstComponentIsUsed()
        {
            AssertColor(Assembly(Model(Appearance).Object), 0.1, 0.2, 0.3, 0.25);
        }

        [Fact]
        public void ComponentWithoutADocumentKeepsTheColor()
        {
            Link link = Assembly(null);
            AssertColor(link, 0.5, 0.6, 0.7, 0.8);
            Assert.Equal(new double[3], link.Visual.Origin.GetXYZ());
        }

        [Fact]
        public void DocumentWithoutAnAppearanceKeepsTheColor()
        {
            foreach (object none in NoAppearance)
            {
                AssertColor(Assembly(Model(none).Object), 0.5, 0.6, 0.7, 0.8);
            }
        }

        [Fact]
        public void AppearanceOfThePartIsUsed()
        {
            AssertColor(Part(Appearance), 0.1, 0.2, 0.3, 0.25);
        }

        [Fact]
        public void PartWithoutAnAppearanceHasTheDefaultColor()
        {
            foreach (object none in NoAppearance)
            {
                Link link = Part(none);
                AssertColor(link, 1, 1, 1, 1);
                Assert.Equal("material_part", link.Visual.Material.Name);
                Assert.Equal(2.0, link.Inertial.Mass.Value);
            }
        }
    }
}
