using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A joint exactly on the assembly origin, not turned, is a joint like any
    /// other, and an estimate that found no joint there is still reported (issue #183). The joint code of
    /// the exporter runs on a fake component.
    /// </summary>
    public class TestJointAtOrigin
    {
        private const string Failed = "Inferring the joint geometry failed";

        private static readonly double[] Origin = { 0, 0, 0 };
        // On the axis where it is closest to the parent frame, which is where the exporter puts the joint.
        private static readonly double[] Elsewhere = { 0.1, 0.2, 0 };

        // What SolidWorks reports about the free degrees of freedom of the child.
        private class Freedom
        {
            public int Result;
            public int R1;
            public int R2;
            public int L1;
            public int L2;
        }

        private static readonly Freedom Hinge = new Freedom { R1 = 1 };
        private static readonly Freedom Slide = new Freedom { L1 = 1 };
        private static readonly Freedom FullyDefined = new Freedom();

        // No single axis: more than one degree of freedom, or none could be told.
        private static readonly Freedom[] NotInferred =
        {
            new Freedom { Result = 1 },
            new Freedom { Result = 1, R1 = 1 },
            new Freedom { Result = 2, R1 = 1, R2 = 1, L1 = 1, L2 = 1 },
            new Freedom { R1 = 2 },
            new Freedom { L1 = 2 },
            new Freedom { R2 = 1 },
            new Freedom { L2 = 1 }
        };

        private class FakeAssembly
        {
            public readonly Link Parent = new Link { Name = "base_link" };
            public readonly Link Child = new Link { Name = "arm" };
            public readonly PrivateObject Helper;
            private readonly Dictionary<string, MathTransform> frames = new Dictionary<string, MathTransform>();
            private readonly HashSet<string> generated = new HashSet<string>();
            private string selected;

            public FakeAssembly(Freedom freedom, double[] place)
            {
                Mock<ModelDoc2> document = new Mock<ModelDoc2>();
                document.As<AssemblyDoc>();
                Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
                selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                selection.Setup(s => s.GetSelectedObject6(1, -1)).Returns(() => Feature(selected));
                document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
                Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
                extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>()))
                    .Returns((string name) => frames.ContainsKey(name) ? frames[name] : null);
                extension.Setup(e => e.SelectByID2(It.IsAny<string>(), "COORDSYS", 0, 0, 0, false, 0,
                    It.IsAny<Callout>(), 0)).Returns((string name, string type, double x, double y, double z,
                        bool append, int mark, Callout callout, int option) =>
                    {
                        selected = name;
                        return frames.ContainsKey(name);
                    });
                document.SetupGet(d => d.Extension).Returns(extension.Object);

                Mock<Component2> component = new Mock<Component2>();
                component.SetupGet(c => c.Name2).Returns("arm-1");
                component.SetupGet(c => c.Transform2).Returns(Transform(place));
                component.Setup(c => c.GetBox(false, false)).Returns(new double[] { -1, -1, -1, 1, 1, 1 });
                Mock<MathPoint> point = new Mock<MathPoint>();
                point.SetupGet(p => p.ArrayData).Returns(place);
                Mock<MathVector> axis = new Mock<MathVector>();
                axis.SetupGet(v => v.ArrayData).Returns(new double[] { 0, 0, 1 });
                int r1 = freedom.R1, r2 = freedom.R2, l1 = freedom.L1, l2 = freedom.L2, direction = 0;
                MathPoint rPoint = point.Object;
                MathVector vector = axis.Object;
                component.Setup(c => c.GetRemainingDOFs(out r1, out rPoint, out direction, out vector,
                    out r2, out rPoint, out direction, out vector, out l1, out vector, out l2, out vector))
                    .Returns(freedom.Result);

                AddFrame("Origin_global", Origin, false);
                AddFrame("Origin_arm_joint", place, true);
                Parent.Joint.CoordinateSystemName = "Origin_global";
                Child.SWComponents.Add(component.Object);
                Child.SWMainComponent = component.Object;
                Child.Joint.Name = "arm_joint";
                Child.Joint.Type = "Automatically Detect";
                Child.Joint.CoordinateSystemName = "Origin_arm_joint";
                Child.Joint.AxisName = "Axis_arm_joint";

                ExportHelper helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                helper.ActiveSWModel = document.Object;
                Helper = new PrivateObject(helper);
                Helper.SetField("ReferenceCoordinateSystemNames", new List<string>(frames.Keys));
                Helper.SetField("ReferenceAxesNames", new List<string> { "Axis_arm_joint" });
            }

            public void AddFrame(string name, double[] place, bool isGenerated)
            {
                frames[name] = Transform(place);
                if (isGenerated)
                {
                    generated.Add(name);
                }
            }

            private static MathTransform Transform(double[] place)
            {
                Mock<MathTransform> transform = new Mock<MathTransform>();
                transform.SetupGet(t => t.ArrayData).Returns(new double[]
                    { 1, 0, 0, 0, 1, 0, 0, 0, 1, place[0], place[1], place[2], 1, 0, 0, 0 });
                return transform.Object;
            }

            private Feature Feature(string name)
            {
                Mock<Feature> sketch = new Mock<Feature>();
                sketch.SetupGet(f => f.Name).Returns("URDF Reference");
                Mock<Feature> feature = new Mock<Feature>();
                feature.SetupGet(f => f.Name).Returns(name);
                feature.Setup(f => f.GetParents()).Returns(
                    generated.Contains(name) ? new object[] { sketch.Object } : new object[0]);
                return feature.Object;
            }

            public bool CreateJoint()
            {
                return (bool)Helper.Invoke("CreateJoint", Parent, Child);
            }

            public string Error
            {
                get { return (string)Helper.GetField("ExportErrorWhy") ?? ""; }
            }
        }

        private static void CheckCreated(Freedom freedom, double[] place, string type)
        {
            FakeAssembly a = new FakeAssembly(freedom, place);
            Assert.True(a.CreateJoint(), a.Error);
            Assert.Equal("", a.Error);
            Assert.Equal(type, a.Child.Joint.Type);
            Assert.Equal(place, a.Child.Joint.Origin.GetXYZ());
        }

        [Fact]
        public void HingeOnTheOriginIsCreated()
        {
            CheckCreated(Hinge, Origin, "continuous");
        }

        [Fact]
        public void SlideOnTheOriginIsCreated()
        {
            CheckCreated(Slide, Origin, "prismatic");
        }

        [Fact]
        public void FullyDefinedChildOnTheOriginIsAFixedJoint()
        {
            CheckCreated(FullyDefined, Origin, "fixed");
        }

        [Fact]
        public void JointThatCannotBeInferredOnTheOriginIsReported()
        {
            foreach (Freedom freedom in NotInferred)
            {
                FakeAssembly a = new FakeAssembly(freedom, Origin);
                Assert.False(a.CreateJoint());
                Assert.Contains(Failed, a.Error);
                Assert.Contains("arm_joint", a.Error);
            }
        }

        [Fact]
        public void JointsAwayFromTheOriginAreCreatedAsBefore()
        {
            CheckCreated(Hinge, Elsewhere, "continuous");
            CheckCreated(Slide, Elsewhere, "prismatic");
            CheckCreated(FullyDefined, Elsewhere, "fixed");
            foreach (Freedom freedom in NotInferred)
            {
                CheckCreated(freedom, Elsewhere, "fixed");
            }
        }

        // The frame an earlier export generated is somewhere else; a generated frame on the origin exists.
        private static string RefreshedFrame(Freedom freedom)
        {
            FakeAssembly a = new FakeAssembly(freedom, Origin);
            a.AddFrame("Origin_arm_joint", Elsewhere, true);
            a.AddFrame("Origin_arm_joint2", Origin, true);
            a.Child.Joint.Type = "fixed";
            a.Helper.Invoke("RefreshGeneratedReferences", a.Parent, a.Child);
            return a.Child.Joint.CoordinateSystemName;
        }

        [Fact]
        public void SavedFrameIsReplacedWhenTheJointMovedOntoTheOrigin()
        {
            Assert.Equal("Origin_arm_joint2", RefreshedFrame(Hinge));
            Assert.Equal("Origin_arm_joint2", RefreshedFrame(Slide));
            Assert.Equal("Origin_arm_joint2", RefreshedFrame(FullyDefined));
        }

        [Fact]
        public void SavedFrameIsKeptWhenTheJointCannotBeInferred()
        {
            foreach (Freedom freedom in NotInferred)
            {
                Assert.Equal("Origin_arm_joint", RefreshedFrame(freedom));
            }
        }
    }
}
