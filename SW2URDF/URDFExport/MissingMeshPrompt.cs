using SW2URDF.UI;
using System.Collections.Generic;
using System.Windows;

namespace SW2URDF.URDFExport
{
    public enum MissingMeshChoice
    {
        ModelFileOnly,
        ExportMeshes,
        Cancel
    }

    /// <summary>
    /// Asks what to do when a model-file-only export would refer to mesh files that do not exist. Kept free of
    /// SolidWorks calls so it can be unit tested.
    /// </summary>
    public static class MissingMeshPrompt
    {
        /// <param name="linksWithoutFile">Links whose mesh file does not exist; no question when empty</param>
        public static MissingMeshChoice Ask(IList<string> linksWithoutFile, IMessageBox messageBox, UiText ui)
        {
            if (linksWithoutFile == null || linksWithoutFile.Count == 0)
            {
                return MissingMeshChoice.ModelFileOnly;
            }
            string message =
                ui.Get("The model file (URDF or xacro) refers to mesh files that do not exist in the package folder, so RViz and Gazebo cannot show these links:") +
                "\r\n\r\n" + MeshExportCheck.JoinLinkNames(linksWithoutFile) + "\r\n\r\n" +
                ui.Get("Export the meshes as well? Choose No to export only the model file anyway, or Cancel to go back.");
            switch (messageBox.Show(message, ui.Get("Mesh files are missing"), MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes: return MissingMeshChoice.ExportMeshes;
                case MessageBoxResult.No: return MissingMeshChoice.ModelFileOnly;
                default: return MissingMeshChoice.Cancel;
            }
        }
    }
}
