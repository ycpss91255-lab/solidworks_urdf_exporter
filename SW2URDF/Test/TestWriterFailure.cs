using System;
using System.IO;
using SW2URDF.ROS;
using SW2URDF.URDF;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A file that could not be written completely must be closed, and it must not
    /// look like a complete one.
    /// </summary>
    public class TestWriterFailure
    {
        // Not allowed in XML: writing it fails in the middle of the file.
        private const string Unwritable = "bad\u0001name";

        private static void InDirectory(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "sw2urdf_writer_" + Guid.NewGuid().ToString("N")) + @"\";
            Directory.CreateDirectory(directory);
            try { test(directory); }
            finally
            {
                // A file that is still open is closed by the finalizer of its stream
                GC.Collect();
                GC.WaitForPendingFinalizers();
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        private static void AssertClosed(string file)
        {
            // Fails while a writer is open. A scanner that reads the file does not matter.
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        }

        private static Robot RobotThatFails()
        {
            Robot robot = new Robot { Name = "robot" };
            robot.BaseLink.Name = "base_link";
            Link child = new Link(robot.BaseLink) { Name = Unwritable };
            robot.BaseLink.Children.Add(child);
            return robot;
        }

        [Fact]
        public void UrdfIsClosedWhenWritingItFails()
        {
            InDirectory(directory =>
            {
                string file = directory + "robot.urdf";
                URDFWriter writer = new URDFWriter(file);
                Assert.ThrowsAny<Exception>(() => RobotThatFails().WriteURDF(writer.writer));
                AssertClosed(file);
            });
        }

        [Fact]
        public void IncompleteUrdfDoesNotLookComplete()
        {
            InDirectory(directory =>
            {
                string file = directory + "robot.urdf";
                URDFWriter writer = new URDFWriter(file);
                Assert.ThrowsAny<Exception>(() => RobotThatFails().WriteURDF(writer.writer));
                AssertClosed(file);
                Assert.DoesNotContain("</robot>", File.ReadAllText(file));
            });
        }

        [Fact]
        public void UrdfCanBeWrittenAfterAFailure()
        {
            InDirectory(directory =>
            {
                string file = directory + "robot.urdf";
                URDFWriter failed = new URDFWriter(file);
                Assert.ThrowsAny<Exception>(() => RobotThatFails().WriteURDF(failed.writer));

                Robot robot = new Robot { Name = "robot" };
                robot.BaseLink.Name = "base_link";
                robot.WriteURDF(new URDFWriter(file).writer);
                Assert.EndsWith("</robot>", File.ReadAllText(file).TrimEnd());
            });
        }

        [Fact]
        public void UrdfIsClosedAfterSuccess()
        {
            InDirectory(directory =>
            {
                string file = directory + "robot.urdf";
                Robot robot = new Robot { Name = "robot" };
                robot.BaseLink.Name = "base_link";
                robot.WriteURDF(new URDFWriter(file).writer);
                AssertClosed(file);
                Assert.EndsWith("</robot>", File.ReadAllText(file).TrimEnd());
            });
        }

        [Fact]
        public void PackageXmlIsClosedWhenWritingItFails()
        {
            InDirectory(directory =>
            {
                string file = directory + "package.xml";
                PackageXMLWriter writer = new PackageXMLWriter(file);
                Assert.ThrowsAny<Exception>(() => new PackageXML(Unwritable).WriteElement(writer));
                AssertClosed(file);
            });
        }

        [Fact]
        public void RvizLaunchFileIsClosedWhenWritingItFails()
        {
            InDirectory(directory =>
            {
                Assert.ThrowsAny<Exception>(() => new Rviz("package", Unwritable).WriteFiles(directory));
                AssertClosed(directory + "display.launch");
            });
        }

        [Fact]
        public void GazeboLaunchFileIsClosedWhenWritingItFails()
        {
            InDirectory(directory =>
            {
                Assert.ThrowsAny<Exception>(() => new Gazebo("model", "package", Unwritable).WriteFile(directory));
                AssertClosed(directory + "gazebo.launch");
            });
        }
    }
}
