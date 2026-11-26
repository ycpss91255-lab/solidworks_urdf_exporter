using SW2URDF.URDF;
using System;
using System.Collections.Generic;
using System.IO;

namespace SW2URDF.URDFExport
{
    /// <summary>Where the mesh of one link is written and how the URDF refers to it.</summary>
    public sealed class MeshFile
    {
        public Link Link { get; }

        /// <summary>The package:// reference written to the URDF.</summary>
        public string Filename { get; }

        /// <summary>The file on disk.</summary>
        public string WindowsFilename { get; }

        internal MeshFile(Link link, string filename, string windowsFilename)
        {
            Link = link;
            Filename = filename;
            WindowsFilename = windowsFilename;
        }
    }

    /// <summary>
    /// Names the mesh file of every link of a robot. Both the export and the check for missing mesh files use
    /// it, so they cannot disagree. Kept free of SolidWorks calls so it can be unit tested.
    /// </summary>
    public static class MeshFiles
    {
        public static string Extension(MeshExportFormat format)
        {
            switch (format)
            {
                case MeshExportFormat.OBJ: return ".obj";
                case MeshExportFormat.THREEDXML: return ".3dxml";
                default: return ".STL";
            }
        }

        /// <summary>
        /// The mesh files in export order: children before their parent. The order decides which link gets a
        /// suffix when two file names collide. A reference frame has no mesh, but its descendants may.
        /// </summary>
        public static List<MeshFile> ForRobot(Link baseLink, URDFPackage package, MeshExportFormat format)
        {
            List<MeshFile> files = new List<MeshFile>();
            Add(baseLink, package, Extension(format), new HashSet<string>(StringComparer.OrdinalIgnoreCase), files);
            return files;
        }

        private static void Add(
            Link link, URDFPackage package, string extension, ISet<string> used, List<MeshFile> files)
        {
            foreach (Link child in link.Children)
            {
                Add(child, package, extension, used, files);
            }
            if (link.isFixedFrame)
            {
                return;
            }
            // Link names may hold characters that are not allowed in file names (SolidWorks likes to use /),
            // so the file name is derived from the name; the link name itself is kept.
            string name = FileNames.UniqueFileName(link.Name, used) + extension;
            files.Add(new MeshFile(link, package.MeshesDirectory + name, package.WindowsMeshesDirectory + name));
        }

        /// <summary>
        /// Names of the links whose mesh file does not exist. A file that exists counts as present, however old
        /// it is.
        /// </summary>
        public static List<string> LinksWithoutFile(Link baseLink, URDFPackage package, MeshExportFormat format)
        {
            List<string> missing = new List<string>();
            foreach (MeshFile file in ForRobot(baseLink, package, format))
            {
                if (!File.Exists(file.WindowsFilename))
                {
                    missing.Add(file.Link.Name);
                }
            }
            return missing;
        }
    }
}
