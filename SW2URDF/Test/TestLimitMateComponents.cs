using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Moq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.Utilities;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. Which limit mate gives a joint its limits when the links have several
    /// components (issue #182). The joint estimate of the exporter runs on fake components and mates; the
    /// fake child slides along X.
    /// </summary>
    public class TestLimitMateComponents
    {
        private const double Tolerance = 1e-12;

        private class FakeMate
        {
            public Mock<Mate2> Mock = new Mock<Mate2>();
            public bool Suppressed;
            public bool SuppressedDuringEstimate;
            public int TimesSuppressed;

            public FakeMate(string name, double minimum, double maximum, params Mock<Component2>[] components)
            {
                Mock.SetupGet(m => m.Type).Returns((int)swMateType_e.swMateDISTANCE);
                Mock.SetupGet(m => m.MinimumVariation).Returns(minimum);
                Mock.SetupGet(m => m.MaximumVariation).Returns(maximum);
                Mock.SetupGet(m => m.Flipped).Returns(false);
                Mock.Setup(m => m.GetMateEntityCount()).Returns(components.Length);
                for (int i = 0; i < components.Length; i++)
                {
                    Mock<MateEntity2> entity = new Mock<MateEntity2>();
                    entity.SetupGet(e => e.ReferenceComponent).Returns(components[i] == null ? null : components[i].Object);
                    int index = i;
                    Mock.Setup(m => m.MateEntity(index)).Returns(entity.Object);
                }
                Mock<Feature> feature = Mock.As<Feature>();
                feature.SetupGet(f => f.Name).Returns(name);
                feature.Setup(f => f.SetSuppression2(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<object>()))
                    .Returns(true).Callback((int action, int option, object names) =>
                    {
                        Suppressed = action == (int)swFeatureSuppressionAction_e.swSuppressFeature;
                        TimesSuppressed += Suppressed ? 1 : 0;
                    });
            }
        }

        private class FakeAssembly
        {
            public readonly List<FakeMate> Mates = new List<FakeMate>();
            public readonly Link Parent = new Link { Name = "parent" };
            public readonly Link Child = new Link { Name = "child" };
            private readonly Mock<MathTransform> identity = new Mock<MathTransform>();
            private readonly Dictionary<Mock<Component2>, List<FakeMate>> matesOf =
                new Dictionary<Mock<Component2>, List<FakeMate>>();
            private readonly Mock<ModelDoc2> document = new Mock<ModelDoc2>();

            public FakeAssembly()
            {
                identity.SetupGet(t => t.ArrayData).Returns(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 });
                document.As<AssemblyDoc>();
                Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
                selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
                Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
                extension.Setup(e => e.GetCoordinateSystemTransformByName("frame")).Returns(identity.Object);
                document.SetupGet(d => d.Extension).Returns(extension.Object);
                Parent.Joint.CoordinateSystemName = "frame";
            }

            public Mock<Component2> Component(string name, Mock<Component2> parent = null)
            {
                Mock<Component2> component = new Mock<Component2>();
                component.SetupGet(c => c.Name2).Returns(name);
                component.SetupGet(c => c.Transform2).Returns(identity.Object);
                component.Setup(c => c.GetParent()).Returns(parent == null ? null : parent.Object);
                component.Setup(c => c.GetBox(false, false)).Returns(new double[] { -1, -1, -1, 1, 1, 1 });
                List<FakeMate> mates = new List<FakeMate>();
                matesOf[component] = mates;
                component.Setup(c => c.GetMates()).Returns(() => mates.Select(m => (object)m.Mock.Object).ToArray());
                return component;
            }

            public void Links(Mock<Component2>[] parent, Mock<Component2>[] child)
            {
                Parent.SWComponents.AddRange(parent.Select(c => c.Object));
                Parent.SWMainComponent = Parent.SWComponents.FirstOrDefault();
                Child.SWComponents.AddRange(child.Select(c => c.Object));
                Child.SWMainComponent = Child.SWComponents[0];

                Mock<MathVector> axis = new Mock<MathVector>();
                axis.SetupGet(v => v.ArrayData).Returns(new double[] { 1, 0, 0 });
                int none = 0, one = 1;
                MathPoint noPoint = null;
                MathVector noVector = null, xAxis = axis.Object;
                child[0].Setup(c => c.GetRemainingDOFs(out none, out noPoint, out none, out noVector,
                    out none, out noPoint, out none, out noVector, out one, out xAxis, out none, out noVector))
                    .Returns(0).Callback(() => Mates.ForEach(m => m.SuppressedDuringEstimate = m.Suppressed));
            }

            // A limit mate between components; like SolidWorks, each of them reports it.
            public FakeMate Mate(string name, double minimum, double maximum, params Mock<Component2>[] components)
            {
                FakeMate mate = new FakeMate(name, minimum, maximum, components);
                Mates.Add(mate);
                foreach (Mock<Component2> component in components.Where(c => c != null))
                {
                    matesOf[component].Add(mate);
                }
                return mate;
            }

            public Joint Estimate()
            {
                ExportHelper helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                helper.ActiveSWModel = document.Object;
                new PrivateObject(helper).SetField("ComputeJointLimits", true);
                helper.EstimateGlobalJointFromComponents(Parent, Child);
                Assert.Equal("prismatic", Child.Joint.Type);
                foreach (FakeMate mate in Mates)
                {
                    Assert.False(mate.Suppressed, "a mate stayed suppressed");
                }
                return Child.Joint;
            }
        }

        private static Mock<Component2>[] List(params Mock<Component2>[] components)
        {
            return components;
        }

        private static void CheckLimits(Joint joint, string mate, double lower, double upper)
        {
            Assert.NotNull(joint.LimitInfo);
            Assert.Equal(mate, joint.LimitInfo.MateName);
            Assert.InRange(joint.Limit.Lower, lower - Tolerance, lower + Tolerance);
            Assert.InRange(joint.Limit.Upper, upper - Tolerance, upper + Tolerance);
        }

        // Runs the estimate and returns what it wrote to the log.
        private static List<string> EstimateLog(FakeAssembly assembly)
        {
            log4net.Appender.MemoryAppender appender = new log4net.Appender.MemoryAppender();
            log4net.Repository.Hierarchy.Logger log =
                ((log4net.Repository.Hierarchy.Hierarchy)Logger.GetLogger().Logger.Repository).Root;
            log.AddAppender(appender);
            try
            {
                assembly.Estimate();
                return appender.GetEvents().Select(e => e.RenderedMessage).ToList();
            }
            finally
            {
                log.RemoveAppender(appender);
            }
        }

        [Fact]
        public void MateBetweenTheFirstComponentsGivesTheLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), base2 = a.Component("base-2");
            Mock<Component2> arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2");
            a.Links(List(base1, base2), List(arm1, arm2));
            FakeMate mate = a.Mate("Limit1", -0.01, 0.02, base1, arm1);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
            Assert.True(mate.SuppressedDuringEstimate);
        }

        [Fact]
        public void MateWithAnotherComponentOfTheParentGivesTheLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), base2 = a.Component("base-2"), arm1 = a.Component("arm-1");
            a.Links(List(base1, base2), List(arm1));
            a.Mate("Limit1", -0.01, 0.02, base2, arm1);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
        }

        [Fact]
        public void MateWithAnotherComponentOfTheChildGivesTheLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2");
            a.Links(List(base1), List(arm1, arm2));
            FakeMate mate = a.Mate("Limit1", -0.01, 0.02, base1, arm2);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
            Assert.True(mate.SuppressedDuringEstimate, "the limit mate hides the degree of freedom");
        }

        [Fact]
        public void MateBetweenOtherComponentsOfBothLinksGivesTheLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), base2 = a.Component("base-2"), base3 = a.Component("base-3");
            Mock<Component2> arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2"), arm3 = a.Component("arm-3");
            a.Links(List(base1, base2, base3), List(arm1, arm2, arm3));
            a.Mate("Limit1", -0.01, 0.02, arm3, base2);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
        }

        [Fact]
        public void MateWithAComponentInsideASubAssemblyOfTheLinkGivesTheLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), frame = a.Component("frame-1");
            Mock<Component2> inner = a.Component("inner-1", frame), rail = a.Component("inner-1/rail-1", inner);
            Mock<Component2> arm1 = a.Component("arm-1");
            a.Links(List(base1, frame), List(arm1));
            a.Mate("Limit1", -0.01, 0.02, rail, arm1);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
        }

        [Fact]
        public void LinksOfPartsOfTheSameSubAssemblyAreTwoLinks()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> sub = a.Component("sub-1");
            Mock<Component2> base1 = a.Component("base-1"), rail = a.Component("sub-1/rail-1", sub);
            Mock<Component2> arm1 = a.Component("sub-1/arm-1", sub);
            a.Links(List(base1, rail), List(arm1));
            a.Mate("Limit1", -0.01, 0.02, rail, arm1);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
        }

        [Fact]
        public void MatesInsideOneLinkGiveNoLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), base2 = a.Component("base-2");
            Mock<Component2> arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2"), arm3 = a.Component("arm-3");
            a.Links(List(base1, base2), List(arm1, arm2, arm3));
            a.Mate("InParent", -0.01, 0.02, base1, base2);
            a.Mate("InChild", -0.01, 0.02, arm1, arm2);
            FakeMate inner = a.Mate("InChildToo", -0.01, 0.02, arm2, arm3);
            Assert.Null(a.Estimate().LimitInfo);
            Assert.Equal(0, inner.TimesSuppressed);
        }

        [Fact]
        public void MatesWithAnotherLinkGiveNoLimits()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2");
            Mock<Component2> other = a.Component("other-1");
            a.Links(List(base1), List(arm1, arm2));
            a.Mate("OtherArm", -0.01, 0.02, other, arm1);
            FakeMate second = a.Mate("OtherArm2", -0.01, 0.02, other, arm2);
            a.Mate("OtherBase", -0.01, 0.02, other, base1);
            a.Mate("NoComponent", -0.01, 0.02, null, arm1);
            Assert.Null(a.Estimate().LimitInfo);
            Assert.Equal(0, second.TimesSuppressed);
        }

        [Fact]
        public void PartOfTheChildInsideASubAssemblyOfTheParentIsTheChild()
        {
            // The parent link has the whole sub-assembly, the child link one part of it: a mate of this
            // part with another link is not a mate between the two.
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> sub = a.Component("sub-1"), arm1 = a.Component("sub-1/arm-1", sub);
            Mock<Component2> rail = a.Component("sub-1/rail-1", sub), other = a.Component("other-1");
            a.Links(List(sub), List(arm1));
            a.Mate("OtherArm", -0.05, 0.05, other, arm1);
            Assert.Null(a.Estimate().LimitInfo);
            a.Mate("Limit1", -0.01, 0.02, rail, arm1);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
        }

        [Fact]
        public void MateOfTheFirstComponentsIsPreferred()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), base2 = a.Component("base-2");
            Mock<Component2> arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2");
            a.Links(List(base1, base2), List(arm1, arm2));
            a.Mate("Second", -0.03, 0.03, base2, arm1);
            a.Mate("First", -0.01, 0.02, base1, arm1);
            a.Mate("Third", -0.04, 0.04, base1, arm2);
            CheckLimits(a.Estimate(), "First", -0.01, 0.02);
        }

        [Fact]
        public void FirstOfSeveralMatesOfOtherComponentsIsUsedAndTheOthersAreLogged()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), base2 = a.Component("base-2");
            Mock<Component2> arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2");
            a.Links(List(base1, base2), List(arm1, arm2));
            a.Mate("Used", -0.03, 0.03, base2, arm1);
            a.Mate("Spare", -0.04, 0.04, base2, arm2);
            List<string> log = EstimateLog(a);
            CheckLimits(a.Child.Joint, "Used", -0.03, 0.03);
            Assert.Contains(log, line => line.Contains("Spare is not used") && line.Contains("Used"));
        }

        [Fact]
        public void LogSaysWhyAMateIsNotUsed()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1"), arm1 = a.Component("arm-1"), other = a.Component("other-1");
            a.Links(List(base1), List(arm1));
            a.Mate("OtherArm", -0.01, 0.02, other, arm1);
            FakeMate angle = a.Mate("Angle1", -0.5, 0.5, base1, arm1);
            angle.Mock.SetupGet(m => m.Type).Returns((int)swMateType_e.swMateANGLE);
            List<string> log = EstimateLog(a);
            Assert.Null(a.Child.Joint.LimitInfo);
            Assert.Contains(log, line => line.Contains("OtherArm is not used") && line.Contains("parent link"));
            Assert.Contains(log, line => line.Contains("Angle1 is not used") && line.Contains("prismatic"));
        }

        [Fact]
        public void MateSeenFromSeveralComponentsIsSuppressedOnce()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> base1 = a.Component("base-1");
            Mock<Component2> arm1 = a.Component("arm-1"), arm2 = a.Component("arm-2");
            a.Links(List(base1), List(arm1, arm2));
            FakeMate mate = a.Mate("Limit1", -0.01, 0.02, base1, arm1, arm2);
            CheckLimits(a.Estimate(), "Limit1", -0.01, 0.02);
            Assert.Equal(1, mate.TimesSuppressed);
        }

        [Fact]
        public void ParentLinkWithoutComponentsIsNotAnError()
        {
            FakeAssembly a = new FakeAssembly();
            Mock<Component2> arm1 = a.Component("arm-1"), other = a.Component("other-1");
            a.Links(List(), List(arm1));
            a.Mate("OtherArm", -0.01, 0.02, other, arm1);
            Assert.Null(a.Estimate().LimitInfo);
        }
    }
}
