using SW2URDF.UI;
using System.IO;
using System.Linq;
using System.Windows;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Asks before an export writes into a package folder that exists and holds files: the export replaces them,
    /// files edited by hand included. Kept free of SolidWorks calls so it can be unit tested.
    /// </summary>
    public static class ExistingPackagePrompt
    {
        /// <param name="packageDirectory">The folder of the package, as URDFPackage.WindowsPackageDirectory</param>
        /// <returns>True when the export may write: a new or empty folder, or the user chose to overwrite</returns>
        public static bool Confirm(string packageDirectory, IMessageBox messageBox, UiText ui)
        {
            string folder = packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return true;
            }
            string message =
                ui.Get("The package folder exists already") +
                "\r\n\r\n" + folder + "\r\n\r\n" +
                ui.Get("The export replaces the files in it, files edited by hand included: the URDF, package.xml, CMakeLists.txt, the launch and config files, the meshes and the textures. Overwrite them? Choose No to go back and choose another folder or name.");
            return messageBox.Show(message, ui.Get("The package exists already"), MessageBoxButton.YesNo) ==
                MessageBoxResult.Yes;
        }
    }
}
