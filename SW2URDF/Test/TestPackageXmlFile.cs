using System.IO;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks. Both export paths name the package file package.xml.</summary>
    public class TestPackageXmlFile
    {
        [Fact]
        public void PackageXmlPathIsPackageXmlInPackageDirectory()
        {
            URDFPackage package = new URDFPackage("box_description", @"C:\out\");
            Assert.Equal(package.WindowsPackageDirectory + "package.xml", package.WindowsPackageXML);
            Assert.DoesNotContain("manifest", package.WindowsPackageXML);
        }

        [Fact]
        public void PackageXmlWriterWritesPackageFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                URDFPackage package = new URDFPackage("box_description", dir + @"\");
                Directory.CreateDirectory(package.WindowsPackageDirectory);
                new PackageXML(package.PackageName).WriteElement(new PackageXMLWriter(package.WindowsPackageXML));

                Assert.True(File.Exists(package.WindowsPackageXML));
                Assert.False(File.Exists(package.WindowsPackageDirectory + "manifest.xml"));
                Assert.Contains("<package format=\"2\">", File.ReadAllText(package.WindowsPackageXML));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}