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
            // The xacro suffix is the one of XacroOutput, whatever it is
            Assert.Equal(@"C:\out\arm_part\urdf\arm_part" + XacroOutput.XacroExtension, path);
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
                Assert.Contains("$(find xacro)/xacro $(find arm_part)/urdf/arm_part" + XacroOutput.XacroExtension,
                    display);
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

        // Issue #275: the default Save As value of the part form

        [Fact]
        public void DefaultSaveAsDropsTheExtensionOfTheTitle()
        {
            // ExportHelper starts with the document title, which SolidWorks shows with .SLDPRT
            Assert.Equal(@"C:\home\ARM", PartPackage.DefaultSaveAs(@"C:\home", "ARM.SLDPRT"));
            Assert.Equal(@"C:\home\arm", PartPackage.DefaultSaveAs(@"C:\home\", "arm.sldprt"));
            Assert.Equal(@"C:\home\x_part", PartPackage.DefaultSaveAs(@"C:\home", "x_part"));
        }

        [Fact]
        public void DefaultSaveAsIsThePackageNameThatIsUsed()
        {
            string saveAs = PartPackage.DefaultSaveAs(@"C:\home", "my arm.SLDPRT");
            URDFPackage package = new URDFPackage(Path.GetFileName(saveAs), Path.GetDirectoryName(saveAs));
            Assert.Equal(package.PackageName, Path.GetFileName(saveAs));
        }

        [Fact]
        public void LinkIsNamedAfterThePackageThatIsUsed()
        {
            // The form passes the file name of the Save As box, as typed or as left from an older default
            foreach (string saveAsName in new[] { "ARM.SLDPRT", "ARM", "my arm.SLDPRT", "x_part" })
            {
                URDFPackage package = new URDFPackage(saveAsName, @"C:\out\");
                Assert.Equal(package.PackageName, PartPackage.LinkName(saveAsName));
            }
            Assert.Equal("ARM", PartPackage.LinkName("ARM.SLDPRT"));
            Assert.Equal("x_part", PartPackage.LinkName("x_part"));
        }

        [Fact]
        public void MeshIsNamedAfterTheLink()
        {
            Robot robot = TitledRobot();
            robot.BaseLink.Name = PartPackage.LinkName("ARM.SLDPRT");
            Assert.Equal("ARM.STL", PartPackage.MeshFileName(robot.BaseLink, ".STL"));
            robot.BaseLink.Name = PartPackage.LinkName("x_part");
            Assert.Equal("x_part.STL", PartPackage.MeshFileName(robot.BaseLink, ".STL"));
        }

        [Fact]
        public void DefaultExportNamesPackageRobotLinkAndMeshAlike()
        {
            // The values that ButtonFinishClick and ExportLink use when the default is left as it is
            string saveAs = PartPackage.DefaultSaveAs(@"C:\home", "ARM.SLDPRT");
            string saveAsName = Path.GetFileName(saveAs);
            URDFPackage package = new URDFPackage(saveAsName, Path.GetDirectoryName(saveAs));
            Robot robot = TitledRobot();
            robot.BaseLink.Name = PartPackage.LinkName(saveAsName);
            PartPackage.NameRobot(robot, package, false);

            Assert.Equal("ARM", package.PackageName);
            Assert.Equal("ARM", robot.Name);
            Assert.Equal("ARM", robot.BaseLink.Name);
            Assert.Equal("package://ARM/meshes/ARM.STL",
                package.MeshesDirectory + PartPackage.MeshFileName(robot.BaseLink, ".STL"));
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
