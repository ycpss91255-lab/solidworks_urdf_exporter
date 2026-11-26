using System.IO;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The part export names the robot and the URDF file after the package and
    /// writes the launch, config and CMake files of the assembly export (issue #272).
    /// </summary>
    public class TestPartPackage
    {
        private static Robot TitledRobot()
        {
            // Before the export the robot carries the document title, as ExportHelper sets it
            Robot robot = new Robot { Name = "ARM.SLDPRT" };
            robot.BaseLink.Name = "arm_part";
            return robot;
        }

        [Fact]
        public void RobotAndUrdfFileAreNamedAfterThePackage()
        {
            URDFPackage package = new URDFPackage("arm_part", @"C:\out\");
            Robot robot = TitledRobot();

            string path = PartPackage.NameRobot(robot, package, false);

            Assert.Equal("arm_part", robot.Name);
            Assert.Equal(@"C:\out\arm_part\urdf\arm_part.urdf", path);
            Assert.DoesNotContain("SLDPRT", path);
        }

        [Fact]
        public void XacroFileIsNamedAfterThePackage()
        {
            URDFPackage package = new URDFPackage("arm_part", @"C:\out\");
            string path = PartPackage.NameRobot(TitledRobot(), package, true);
            Assert.Equal(@"C:\out\arm_part\urdf\arm_part.urdf.xacro", path);
        }

        [Fact]
        public void RobotNameIsTheSanitizedPackageName()
        {
            URDFPackage package = new URDFPackage("arm part.SLDPRT", @"C:\out\");
            Robot robot = TitledRobot();

            string path = PartPackage.NameRobot(robot, package, false);

            Assert.Equal(package.PackageName, robot.Name);
            Assert.Equal(package.WindowsRobotsDirectory + package.PackageName + ".urdf", path);
            Assert.DoesNotContain("SLDPRT", robot.Name);
        }

        [Fact]
        public void WrittenUrdfNamesTheRobotAfterThePackage()
        {
            WithPackage("arm_part", package =>
            {
                Robot robot = TitledRobot();
                string path = PartPackage.NameRobot(robot, package, false);
                URDFWriter writer = new URDFWriter(path);
                robot.WriteURDF(writer.writer);
                writer.writer.Close();

                string urdf = File.ReadAllText(path);
                Assert.Matches(@"<robot\s+name=""arm_part""", urdf);
                Assert.DoesNotContain("SLDPRT", urdf);
            });
        }

        [Fact]
        public void LaunchConfigAndCMakeFilesAreWritten()
        {
            WithPackage("arm_part", package =>
            {
                Robot robot = TitledRobot();
                PartPackage.NameRobot(robot, package, false);
                PartPackage.WriteRosFiles(robot, package, false);

                string display = File.ReadAllText(package.WindowsLaunchDirectory + "display.launch");
                Assert.Contains("$(find arm_part)/urdf/arm_part.urdf", display);

                string gazebo = File.ReadAllText(package.WindowsLaunchDirectory + "gazebo.launch");
                Assert.Contains("-file $(find arm_part)/urdf/arm_part.urdf -urdf -model arm_part", gazebo);

                Assert.Equal("controller_joint_names: []",
                    File.ReadAllText(package.WindowsConfigYAML).Trim());
                Assert.Contains("project(arm_part)", File.ReadAllText(package.WindowsCMakeLists));
            });
        }

        [Fact]
        public void XacroLaunchFilesExpandTheXacroFile()
        {
            WithPackage("arm_part", package =>
            {
                Robot robot = TitledRobot();
                PartPackage.NameRobot(robot, package, true);
                PartPackage.WriteRosFiles(robot, package, true);

                string display = File.ReadAllText(package.WindowsLaunchDirectory + "display.launch");
                Assert.Contains("$(find xacro)/xacro $(find arm_part)/urdf/arm_part.urdf.xacro", display);
                string gazebo = File.ReadAllText(package.WindowsLaunchDirectory + "gazebo.launch");
                Assert.Contains("-param robot_description -urdf -model arm_part", gazebo);
            });
        }

        [Fact]
        public void UrdfOfAnEarlierExportIsKept()
        {
            WithPackage("arm_part", package =>
            {
                string old = package.WindowsRobotsDirectory + "ARM.SLDPRT.urdf";
                File.WriteAllText(old, "<robot name=\"ARM.SLDPRT\"/>");

                Robot robot = TitledRobot();
                string path = PartPackage.NameRobot(robot, package, false);
                PartPackage.WriteRosFiles(robot, package, false);

                Assert.NotEqual(old, path);
                Assert.True(File.Exists(old));
                Assert.Equal("<robot name=\"ARM.SLDPRT\"/>", File.ReadAllText(old));
            });
        }

        private static void WithPackage(string name, System.Action<URDFPackage> test)
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                URDFPackage package = new URDFPackage(name, dir + @"\");
                // CreateDirectories shows a message box, so the folders are made here
                Directory.CreateDirectory(package.WindowsRobotsDirectory);
                Directory.CreateDirectory(package.WindowsLaunchDirectory);
                Directory.CreateDirectory(package.WindowsConfigDirectory);
                test(package);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
