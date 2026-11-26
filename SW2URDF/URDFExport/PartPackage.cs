using SW2URDF.URDF;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Names and package files of the part export. The part export names the robot and its URDF file
    /// after the package, as the assembly export does. Files of earlier exports, such as a URDF named after the document title, are left as
    /// they are.
    /// </summary>
    public static class PartPackage
    {
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
    }
}
