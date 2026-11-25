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
    /// Does not need SolidWorks. A joint type the user chose is kept when the frame or the axis of the joint
    /// is generated; only "Automatically Detect" takes the type of the estimate (issue #190). The joint code
    /// of the exporter runs on a fake component.
    /// </summary>
    public class TestChosenJointType
    {
        private const string Auto = "Automatically Generate";
        private const string Detect = "Automatically Detect";
        private const string Frame = "Origin_arm_joint";
        private const string AxisName = "Axis_arm_joint";

        // On the axis where it is closest to the parent frame, which is where the exporter puts the joint.
        private static readonly double[] Place = { 0.1, 0.2, 0 };

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
        private static readonly Freedom Cylinder = new Freedom { R1 = 1, L1 = 1 };

        // Fully defined, and more than one degree of freedom.
        private static readonly Freedom[] NoDirection =
            { new Freedom(), new Freedom { Result = 2, R1 = 1, R2 = 1, L1 = 1, L2 = 1 }, new Freedom { R1 = 2 } };

        private class FakeAssembly
        {
            public readonly Link Parent = new Link { Name = "base_link" };
            public readonly Link Child = new Link { Name = "arm" };
            public readonly ExportHelper Exporter;
            public readonly PrivateObject Helper;
            private readonly Dictionary<string, MathTransform> frames = new Dictionary<string, MathTransform>();
            private readonly List<object> mates = new List<object>();
            private readonly Mock<Component2> baseComponent;
            private readonly Mock<Component2> component;
            private string selected;

            public FakeAssembly(Freedom freedom, string type)
            {
                Mock<ModelDoc2> document = new Mock<ModelDoc2>();
                document.As<AssemblyDoc>();
                Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
                selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                selection.Setup(s => s.GetSelectedObject6(1, It.IsAny<int>())).Returns(() => SelectedFeature());
                document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
                Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
                extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>()))
                    .Returns((string name) => frames.ContainsKey(name) ? frames[name] : null);
                extension.Setup(e => e.SelectByID2(It.IsAny<string>(), It.IsAny<string>(), 0, 0, 0, false, 0,
                    It.IsAny<Callout>(), 0)).Returns((string name, string kind, double x, double y, double z,
                        bool append, int mark, Callout callout, int option) =>
                    {
                        selected = name;
                        return kind == "AXIS" ? name == AxisName : frames.ContainsKey(name);
                    });
                document.SetupGet(d => d.Extension).Returns(extension.Object);

                frames["Origin_global"] = Transform(new double[] { 0, 0, 0 });
                frames[Frame] = Transform(Place);

                baseComponent = NewComponent("base-1");
                component = NewComponent("arm-1");
                component.Setup(c => c.GetMates()).Returns(() => mates.ToArray());
                Mock<MathPoint> point = new Mock<MathPoint>();
                point.SetupGet(p => p.ArrayData).Returns(Place);
                Mock<MathVector> axis = new Mock<MathVector>();
                axis.SetupGet(v => v.ArrayData).Returns(new double[] { 0, 0, 1 });
                int r1 = freedom.R1, r2 = freedom.R2, l1 = freedom.L1, l2 = freedom.L2, direction = 0;
                MathPoint rPoint = point.Object;
                MathVector vector = axis.Object;
                component.Setup(c => c.GetRemainingDOFs(out r1, out rPoint, out direction, out vector,
                    out r2, out rPoint, out direction, out vector, out l1, out vector, out l2, out vector))
                    .Returns(freedom.Result);

                Parent.Joint.CoordinateSystemName = "Origin_global";
                Parent.SWComponents.Add(baseComponent.Object);
                Parent.SWMainComponent = baseComponent.Object;
                Child.SWComponents.Add(component.Object);
                Child.SWMainComponent = component.Object;
                Child.Joint.Name = "arm_joint";
                Child.Joint.Type = type;
                Child.Joint.CoordinateSystemName = Auto;
                Child.Joint.AxisName = Auto;

                Exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                Exporter.ActiveSWModel = document.Object;
                Helper = new PrivateObject(Exporter);
                Helper.SetField("ReferenceCoordinateSystemNames", new List<string>(frames.Keys));
                Helper.SetField("ReferenceAxesNames", new List<string> { AxisName });
                Helper.SetField("ComputeJointLimits", true);
            }

            private static Mock<Component2> NewComponent(string name)
            {
                Mock<Component2> result = new Mock<Component2>();
                result.SetupGet(c => c.Name2).Returns(name);
                result.SetupGet(c => c.Transform2).Returns(Transform(Place));
                result.Setup(c => c.GetBox(false, false)).Returns(new double[] { -1, -1, -1, 1, 1, 1 });
                return result;
            }

            private static MathTransform Transform(double[] place)
            {
                Mock<MathTransform> transform = new Mock<MathTransform>();
                transform.SetupGet(t => t.ArrayData).Returns(new double[]
                    { 1, 0, 0, 0, 1, 0, 0, 0, 1, place[0], place[1], place[2], 1, 0, 0, 0 });
                return transform.Object;
            }

            private Feature SelectedFeature()
            {
                Mock<Feature> sketch = new Mock<Feature>();
                sketch.SetupGet(f => f.Name).Returns("URDF Reference");
                Mock<RefAxis> axis = new Mock<RefAxis>();
                axis.Setup(a => a.GetRefAxisParams()).Returns(new double[] { 0, 0, 1, 0, 0, 0 });
                Mock<Feature> feature = new Mock<Feature>();
                feature.SetupGet(f => f.Name).Returns(selected);
                feature.Setup(f => f.GetSpecificFeature2()).Returns(axis.Object);
                feature.Setup(f => f.GetParents()).Returns(new object[] { sketch.Object });
                return feature.Object;
            }

            public FakeAssembly WithLimitMate(swMateType_e type, double minimum, double maximum)
            {
                Mock<Mate2> mate = new Mock<Mate2>();
                mate.SetupGet(m => m.Type).Returns((int)type);
                mate.SetupGet(m => m.MinimumVariation).Returns(minimum);
                mate.SetupGet(m => m.MaximumVariation).Returns(maximum);
                mate.SetupGet(m => m.Flipped).Returns(type == swMateType_e.swMateANGLE);
                mate.Setup(m => m.GetMateEntityCount()).Returns(2);
                for (int i = 0; i < 2; i++)
                {
                    Mock<MateEntity2> entity = new Mock<MateEntity2>();
                    entity.SetupGet(e => e.ReferenceComponent).Returns((i == 0 ? baseComponent : component).Object);
                    int index = i;
                    mate.Setup(m => m.MateEntity(index)).Returns(entity.Object);
                }
                mate.As<Feature>().SetupGet(f => f.Name).Returns("Limit1");
                mates.Add(mate.Object);
                return this;
            }

            public FakeAssembly WithAxis(string name)
            {
                Child.Joint.AxisName = name;
                return this;
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

        private static FakeAssembly HingeWithMate(string type)
        {
            return new FakeAssembly(Hinge, type).WithLimitMate(swMateType_e.swMateANGLE, -0.5, 1);
        }

        private static FakeAssembly SlideWithMate(string type)
        {
            return new FakeAssembly(Slide, type).WithLimitMate(swMateType_e.swMateDISTANCE, -0.01, 0.02);
        }

        private static bool HasLimits(Joint joint)
        {
            return joint.Limit.ElementContainsData();
        }

        private static void CheckCreated(FakeAssembly a, string type, bool limits)
        {
            Assert.True(a.CreateJoint(), a.Error);
            Assert.Equal("", a.Error);
            Assert.Equal(type, a.Child.Joint.Type);
            Assert.Equal(limits, HasLimits(a.Child.Joint));
            Assert.Equal(limits, a.Child.Joint.LimitInfo != null);
            Assert.Equal(Place, a.Child.Joint.Origin.GetXYZ());
            Assert.Equal(Frame, a.Child.Joint.CoordinateSystemName);
            if (type != "fixed")
            {
                Assert.Equal(new double[] { 0, 0, 1 }, a.Child.Joint.Axis.GetXYZ());
                Assert.Equal(AxisName, a.Child.Joint.AxisName);
            }
        }

        private static void CheckRejected(FakeAssembly a, string type, string reason)
        {
            a.Child.Joint.Limit.Lower = -7;
            Assert.False(a.CreateJoint(), type + " was accepted");
            Assert.Contains("arm_joint", a.Error);
            Assert.Contains(type, a.Error);
            Assert.Contains(reason, a.Error);
            // Nothing was decided for the user.
            Assert.Equal(type, a.Child.Joint.Type);
            Assert.Equal(-7, a.Child.Joint.Limit.Lower);
        }

        [Fact]
        public void AutomaticallyDetectTakesTheEstimate()
        {
            foreach (string type in new[] { Detect, "" })
            {
                CheckCreated(new FakeAssembly(Hinge, type), "continuous", false);
                CheckCreated(HingeWithMate(type), "revolute", true);
                CheckCreated(new FakeAssembly(Slide, type), "prismatic", false);
                CheckCreated(SlideWithMate(type), "prismatic", true);
                CheckCreated(new FakeAssembly(Cylinder, type), "continuous", false);
                foreach (Freedom freedom in NoDirection)
                {
                    CheckCreated(new FakeAssembly(freedom, type), "fixed", false);
                }
            }
        }

        [Fact]
        public void RevoluteOnAHingeIsKept()
        {
            CheckCreated(new FakeAssembly(Hinge, "revolute"), "revolute", false);
            FakeAssembly a = HingeWithMate("revolute");
            CheckCreated(a, "revolute", true);
            Assert.Equal(-0.5, a.Child.Joint.Limit.Lower, 12);
            Assert.Equal(1, a.Child.Joint.Limit.Upper, 12);
        }

        [Fact]
        public void TypedLimitsOfARevoluteJointWithoutALimitMateAreKept()
        {
            FakeAssembly a = new FakeAssembly(Hinge, "revolute");
            a.Child.Joint.Limit.Lower = -0.25;
            Assert.True(a.CreateJoint(), a.Error);
            Assert.Equal(-0.25, a.Child.Joint.Limit.Lower);
            Assert.Null(a.Child.Joint.LimitInfo);
        }

        [Fact]
        public void ContinuousOnAHingeIsKeptAndGetsNoLimits()
        {
            CheckCreated(new FakeAssembly(Hinge, "continuous"), "continuous", false);
            CheckCreated(HingeWithMate("continuous"), "continuous", false);
        }

        [Fact]
        public void PrismaticOnASlideIsKept()
        {
            CheckCreated(new FakeAssembly(Slide, "prismatic"), "prismatic", false);
            CheckCreated(SlideWithMate("prismatic"), "prismatic", true);
        }

        [Fact]
        public void FixedIsKeptAndGetsNoLimits()
        {
            CheckCreated(new FakeAssembly(Hinge, "fixed"), "fixed", false);
            CheckCreated(HingeWithMate("fixed"), "fixed", false);
            CheckCreated(new FakeAssembly(Slide, "fixed"), "fixed", false);
            CheckCreated(SlideWithMate("fixed"), "fixed", false);
            foreach (Freedom freedom in NoDirection)
            {
                CheckCreated(new FakeAssembly(freedom, "fixed"), "fixed", false);
            }
            FakeAssembly a = new FakeAssembly(Hinge, "fixed");
            double[] before = a.Child.Joint.Axis.GetXYZ();
            Assert.True(a.CreateJoint(), a.Error);
            Assert.Equal(before, a.Child.Joint.Axis.GetXYZ());
        }

        [Fact]
        public void BothDirectionsOfACylinderCanBeChosen()
        {
            CheckCreated(new FakeAssembly(Cylinder, "prismatic"), "prismatic", false);
            CheckCreated(new FakeAssembly(Cylinder, "revolute"), "revolute", false);
            CheckCreated(new FakeAssembly(Cylinder, "continuous"), "continuous", false);
        }

        [Fact]
        public void TypeThatDoesNotFitTheFreeDirectionIsReported()
        {
            foreach (string axis in new[] { Auto, AxisName })
            {
                CheckRejected(new FakeAssembly(Slide, "revolute").WithAxis(axis), "revolute", "slide");
                CheckRejected(new FakeAssembly(Slide, "continuous").WithAxis(axis), "continuous", "slide");
                CheckRejected(new FakeAssembly(Hinge, "prismatic").WithAxis(axis), "prismatic", "turn");
                CheckRejected(SlideWithMate("revolute").WithAxis(axis), "revolute", "slide");
                CheckRejected(HingeWithMate("prismatic").WithAxis(axis), "prismatic", "turn");
            }
        }

        [Fact]
        public void TypeWithoutAFreeDirectionNeedsAnExistingAxis()
        {
            foreach (string type in new[] { "revolute", "continuous", "prismatic" })
            {
                foreach (Freedom freedom in NoDirection)
                {
                    CheckRejected(new FakeAssembly(freedom, type), type, "axis");
                    CheckCreated(new FakeAssembly(freedom, type).WithAxis(AxisName), type, false);
                }
            }
        }

        [Fact]
        public void JointThatIsNotEstimatedKeepsEverything()
        {
            foreach (string type in new[] { "revolute", "continuous", "prismatic", "fixed" })
            {
                foreach (FakeAssembly a in new[] { HingeWithMate(type), SlideWithMate(type), new FakeAssembly(new Freedom(), type) })
                {
                    // Frame and axis of an earlier export, generated by the exporter, which are checked
                    // against the geometry with an estimate of their own.
                    a.Child.Joint.CoordinateSystemName = Frame;
                    CheckCreated(a.WithAxis(AxisName), type, false);
                }
            }
        }

        [Fact]
        public void GeneratedFrameStillFollowsTheGeometry()
        {
            foreach (string type in new[] { "revolute", "continuous", "prismatic", "fixed" })
            {
                foreach (Freedom freedom in new[] { Hinge, Slide, Cylinder, new Freedom() })
                {
                    FakeAssembly a = new FakeAssembly(freedom, type).WithAxis(AxisName);
                    a.Child.Joint.CoordinateSystemName = "Origin_global";
                    a.Helper.Invoke("RefreshGeneratedReferences", a.Parent, a.Child);
                    Assert.Equal(Frame, a.Child.Joint.CoordinateSystemName);
                    Assert.Equal(AxisName, a.Child.Joint.AxisName);
                    Assert.Equal(type, a.Child.Joint.Type);
                    Assert.False(HasLimits(a.Child.Joint));
                }
            }
        }
    }
}
