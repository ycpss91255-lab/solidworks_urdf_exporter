using System.IO;
using SW2URDF.ROS;
using SW2URDF.URDF;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Names and package files of the part export. The part export names the robot and its URDF file
    /// after the package, as the assembly export does, and writes the same launch, config and CMake
    /// files. Files of earlier exports, such as a URDF named after the document title, are left as
    /// they are.
    /// </summary>
    public static class PartPackage
    {
        /// <summary>
        /// Save As path the part form proposes: the package name made from the document title, so
        /// without the .SLDPRT that SolidWorks shows in the title.
        /// </summary>
        public static string DefaultSaveAs(string savePath, string documentTitle)
        {
            return Path.Combine(savePath, FileNames.SanitizePackageName(documentTitle));
        }

        /// <summary>
        /// Name of the link of the part: the package name actually used, which is the Save As name
        /// as URDFPackage cleans it.
        /// </summary>
        public static string LinkName(string saveAsName)
        {
            return FileNames.SanitizePackageName(saveAsName);
        }

        /// <summary>File name of the mesh of the link, without a directory.</summary>
        public static string MeshFileName(Link link, string extension)
        {
            return FileNames.SanitizeFileName(link.Name) + extension;
        }

        /// <summary>
        /// Names the robot after the package and returns the Windows path of its URDF (or xacro) file.
        /// </summary>
        public static string NameRobot(Robot robot, URDFPackage package, bool writeXacro)
        {
            robot.Name = package.PackageName;
            return package.WindowsRobotsDirectory + RobotFileName(robot, writeXacro);
        }

        /// <summary>File name of the robot file in the urdf folder, without a directory.</summary>
        public static string RobotFileName(Robot robot, bool writeXacro)
        {
            return XacroOutput.RobotFileName(FileNames.SanitizeFileName(robot.Name, "robot"), writeXacro);
        }

        /// <summary>
        /// Writes CMakeLists.txt, the joint names config and the display and Gazebo launch files that
        /// the assembly export writes. The package directories must exist.
        /// </summary>
        public static void WriteRosFiles(Robot robot, URDFPackage package, bool writeXacro)
        {
            string robotFile = RobotFileName(robot, writeXacro);
            package.CreateCMakeLists();
            // A single part has no joints; its base link carries an empty joint that is not written
            package.CreateConfigYAML(new string[0]);
            new Rviz(package.PackageName, robotFile, writeXacro).WriteFiles(package.WindowsLaunchDirectory);
            new Gazebo(robot.Name, package.PackageName, robotFile, writeXacro)
                .WriteFile(package.WindowsLaunchDirectory);
        }
    }
}
