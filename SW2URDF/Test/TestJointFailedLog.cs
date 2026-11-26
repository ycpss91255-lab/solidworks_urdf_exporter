using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using PrivateObject = Microsoft.VisualStudio.TestTools.UnitTesting.PrivateObject;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The log says that creating a joint failed when it failed, and only then
    /// (issue #205). The link code of the exporter runs on a fake component.
    /// </summary>
    public class TestJointFailedLog
    {
        private class FakeAssembly
        {
            private readonly Link parent = new Link { Name = "base_link" };
            private readonly LinkNode node = new LinkNode();
            private readonly PrivateObject helper;

            // An arm that turns about the z axis at the given place, or cannot be inferred.
            public FakeAssembly(bool inferred, double[] place)
            {
                Dictionary<string, MathTransform> frames = new Dictionary<string, MathTransform>
                {
                    { "Origin_global", Transform(0, 0, 0) },
                    { "Origin_arm_joint", Transform(place) }
                };
                Mock<ModelDoc2> document = new Mock<ModelDoc2>();
                document.As<AssemblyDoc>();
                Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
                selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
                Mock<ModelDocExtension> extension = new Mock<ModelDocExtension>();
                extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>()))
                    .Returns((string name) => frames.ContainsKey(name) ? frames[name] : null);
                document.SetupGet(d => d.Extension).Returns(extension.Object);

                Mock<Component2> component = new Mock<Component2>();
                component.SetupGet(c => c.Name2).Returns("arm-1");
                component.SetupGet(c => c.Transform2).Returns(Transform(place));
                component.Setup(c => c.GetBox(false, false)).Returns(new double[] { -1, -1, -1, 1, 1, 1 });
                Mock<MathPoint> point = new Mock<MathPoint>();
                point.SetupGet(p => p.ArrayData).Returns(place);
                Mock<MathVector> axis = new Mock<MathVector>();
                axis.SetupGet(v => v.ArrayData).Returns(new double[] { 0, 0, 1 });
                int r1 = inferred ? 1 : 0, none = 0, direction = 0;
                MathPoint rPoint = point.Object;
                MathVector vector = axis.Object;
                component.Setup(c => c.GetRemainingDOFs(out r1, out rPoint, out direction, out vector,
                    out none, out rPoint, out direction, out vector, out none, out vector, out none, out vector))
                    .Returns(inferred ? 0 : 1);

                parent.Joint.CoordinateSystemName = "Origin_global";
                node.Link.Name = "arm";
                node.Link.SWComponents.Add(component.Object);
                node.Link.Joint.Name = "arm_joint";
                node.Link.Joint.Type = "Automatically Detect";
                node.Link.Joint.CoordinateSystemName = "Origin_arm_joint";
                node.Link.Joint.AxisName = "Axis_arm_joint";

                ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                exporter.ActiveSWModel = document.Object;
                helper = new PrivateObject(exporter);
                helper.SetFieldOrProperty("ComputeJointKinematics", true);
                helper.SetField("ReferenceCoordinateSystemNames", new List<string>(frames.Keys));
                helper.SetField("ReferenceAxesNames", new List<string> { "Axis_arm_joint" });
            }

            public void MakeEmptyLink(string coordinateSystem)
            {
                node.Link.SWComponents.Clear();
                node.Link.isFixedFrame = true;
                node.Link.Joint.CoordinateSystemName = coordinateSystem;
            }

            private static MathTransform Transform(params double[] place)
            {
                Mock<MathTransform> transform = new Mock<MathTransform>();
                transform.SetupGet(t => t.ArrayData).Returns(new double[]
                    { 1, 0, 0, 0, 1, 0, 0, 0, 1, place[0], place[1], place[2], 1, 0, 0, 0 });
                return transform.Object;
            }

            public string Error
            {
                get { return (string)helper.GetField("ExportErrorWhy") ?? ""; }
            }

            // Creates the link and its joint, and returns what the log says about a joint that failed.
            public string[] FailedInLog()
            {
                SW2URDF.Utilities.Logger.Setup();
                Hierarchy hierarchy = (Hierarchy)LogManager.GetRepository();
                MemoryAppender memory = new MemoryAppender();
                hierarchy.Root.AddAppender(memory);
                try
                {
                    helper.Invoke("CreateLinkFromComponents", parent, node);
                }
                finally
                {
                    hierarchy.Root.RemoveAppender(memory);
                }
                return memory.GetEvents().Where(e => e.Level == Level.Warn).Select(e => e.RenderedMessage)
                    .Where(message => message.StartsWith("Creating joint")).ToArray();
            }
        }

        private static readonly string[] Failed = { "Creating joint from parent base_link to child arm failed" };

        [Fact]
        public void JointThatWasCreatedIsNotLoggedAsFailed()
        {
            FakeAssembly assembly = new FakeAssembly(true, new[] { 0.1, 0.2, 0 });
            Assert.Empty(assembly.FailedInLog());
            Assert.Equal("", assembly.Error);
        }

        [Fact]
        public void JointThatCouldNotBeInferredIsLoggedAsFailed()
        {
            FakeAssembly assembly = new FakeAssembly(false, new double[] { 0, 0, 0 });
            Assert.Equal(Failed, assembly.FailedInLog());
            Assert.Contains("arm_joint", assembly.Error);
        }

        [Fact]
        public void EmptyLinkThatWasCreatedIsNotLoggedAsFailed()
        {
            FakeAssembly assembly = new FakeAssembly(true, new[] { 0.1, 0.2, 0 });
            assembly.MakeEmptyLink("Origin_arm_joint");
            Assert.Empty(assembly.FailedInLog());
            Assert.Equal("", assembly.Error);
        }

        [Fact]
        public void EmptyLinkWithoutCoordinateSystemIsLoggedAsFailed()
        {
            FakeAssembly assembly = new FakeAssembly(true, new[] { 0.1, 0.2, 0 });
            assembly.MakeEmptyLink("Automatically Generate");
            Assert.Equal(Failed, assembly.FailedInLog());
            Assert.Contains("arm", assembly.Error);
        }
    }
}