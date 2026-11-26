using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. Only the components of one link are visible while its mesh is saved, also
    /// after the mesh of the link before failed.
    /// </summary>
    public class TestMeshVisibility
    {
        private static readonly MeshExportFormat[] Formats =
            { MeshExportFormat.STL, MeshExportFormat.OBJ, MeshExportFormat.THREEDXML };

        // The visible components for each saved mesh. The mesh of the failing link ends with an exception.
        private static Dictionary<string, string> Export(MeshExportFormat format, string failing)
        {
            Dictionary<string, string> visible = new Dictionary<string, string>();
            using (FakeExport export = new FakeExport("base_link", "arm", "hand"))
            {
                export.OnSaveAs = path =>
                {
                    string link = Path.GetFileNameWithoutExtension(path);
                    visible[link] = string.Join(", ", export.Visible.OrderBy(name => name));
                    if (link == failing) throw new COMException("SolidWorks failed");
                    FakeExport.WriteStl(path);
                    return true;
                };
                export.Helper.ExportRobot(true, format);
                Assert.Equal(3, export.Visible.Count);
            }
            return visible;
        }

        private static void AssertOneLinkEach(Dictionary<string, string> visible)
        {
            Assert.Equal(new[] { "arm", "base_link", "hand" }, visible.Keys.OrderBy(name => name));
            foreach (KeyValuePair<string, string> link in visible)
            {
                Assert.Equal(link.Key, link.Value);
            }
        }

        [Fact]
        public void OnlyTheLinkIsVisible()
        {
            foreach (MeshExportFormat format in Formats) AssertOneLinkEach(Export(format, null));
        }

        [Fact]
        public void OnlyTheLinkIsVisibleAfterAFailure()
        {
            foreach (MeshExportFormat format in Formats) AssertOneLinkEach(Export(format, "arm"));
        }
    }
}
