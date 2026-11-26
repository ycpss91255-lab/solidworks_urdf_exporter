using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks. Names of the mesh files and the check for missing ones (issue 166).</summary>
    public class TestMeshFiles : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "sw2urdf_meshfiles_" + Guid.NewGuid().ToString("N"));
        private readonly URDFPackage package;

        public TestMeshFiles()
        {
            Directory.CreateDirectory(dir);
            package = new URDFPackage("robot", dir);
        }

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        private static Link MakeLink(string name, params Link[] children)
        {
            Link link = new Link { Name = name };
            link.Children.AddRange(children);
            return link;
        }

        private void CreateMesh(string fileName)
        {
            Directory.CreateDirectory(package.WindowsMeshesDirectory);
            File.WriteAllBytes(package.WindowsMeshesDirectory + fileName, new byte[] { 1 });
        }

        private List<string> Missing(Link baseLink, MeshExportFormat format = MeshExportFormat.STL)
        {
            return MeshFiles.LinksWithoutFile(baseLink, package, format);
        }

        [Fact]
        public void UrdfReferenceAndFileOnDiskBelongTogether()
        {
            MeshFile file = MeshFiles.ForRobot(MakeLink("arm"), package, MeshExportFormat.STL).Single();
            Assert.Equal("arm", file.Link.Name);
            Assert.Equal("package://robot/meshes/arm.STL", file.Filename);
            Assert.Equal(Path.Combine(dir, "robot", "meshes", "arm.STL"), file.WindowsFilename);
        }

        [Fact]
        public void ExtensionFollowsTheFormat()
        {
            Link link = MakeLink("arm");
            Assert.EndsWith("/arm.STL", MeshFiles.ForRobot(link, package, MeshExportFormat.STL)[0].Filename);
            Assert.EndsWith("/arm.obj", MeshFiles.ForRobot(link, package, MeshExportFormat.OBJ)[0].Filename);
            Assert.EndsWith("/arm.3dxml", MeshFiles.ForRobot(link, package, MeshExportFormat.THREEDXML)[0].Filename);
        }

        [Fact]
        public void ChildrenAreNamedBeforeTheirParent()
        {
            // "a/b" and "a:b" both become "a_b"; the child comes first and keeps the plain name.
            Link child = MakeLink("a/b");
            Link sibling = MakeLink("A_B");
            Link root = MakeLink("a:b", child, sibling);
            List<MeshFile> files = MeshFiles.ForRobot(root, package, MeshExportFormat.STL);
            Assert.Equal(new[] { child, sibling, root }, files.Select(f => f.Link).ToArray());
            Assert.Equal(
                new[] { "a_b.STL", "A_B_2.STL", "a_b_3.STL" },
                files.Select(f => Path.GetFileName(f.WindowsFilename)).ToArray());
        }

        [Fact]
        public void ReferenceFrameHasNoMeshButItsDescendantsDo()
        {
            Link frame = MakeLink("frame", MakeLink("tool"));
            frame.isFixedFrame = true;
            Assert.Equal(new[] { "tool", "base" }, Missing(MakeLink("base", frame)).ToArray());
        }

        [Fact]
        public void FirstExportMissesEveryMesh()
        {
            Assert.Equal(new[] { "arm", "base" }, Missing(MakeLink("base", MakeLink("arm"))).ToArray());
            Assert.False(Directory.Exists(package.WindowsPackageDirectory)); // the check creates nothing
        }

        [Fact]
        public void NothingIsMissingWhenEveryMeshExists()
        {
            CreateMesh("base.STL");
            CreateMesh("arm.STL");
            Assert.Empty(Missing(MakeLink("base", MakeLink("arm"))));
        }

        [Fact]
        public void RenamedLinkIsMissing()
        {
            CreateMesh("base.STL");
            CreateMesh("arm.STL");
            Assert.Equal(new[] { "upper_arm" }, Missing(MakeLink("base", MakeLink("upper_arm"))).ToArray());
        }

        [Fact]
        public void AnotherFormatIsMissing()
        {
            CreateMesh("base.STL");
            CreateMesh("base.3dxml");
            Assert.Empty(Missing(MakeLink("base")));
            Assert.Equal(new[] { "base" }, Missing(MakeLink("base"), MeshExportFormat.OBJ).ToArray());
        }

        [Fact]
        public void OldMeshCountsAsPresent()
        {
            CreateMesh("base.STL");
            File.SetLastWriteTimeUtc(package.WindowsMeshesDirectory + "base.STL", new DateTime(2000, 1, 1));
            Assert.Empty(Missing(MakeLink("base")));
        }

        [Fact]
        public void SuffixedNameIsCheckedPerLink()
        {
            CreateMesh("a_b.STL");
            // Only the first of the two links has its file; the list shows the link name, not the file name.
            Assert.Equal(new[] { "a:b" }, Missing(MakeLink("a:b", MakeLink("a/b"))).ToArray());
        }

        [Fact]
        public void PackageNameIsTheSanitizedOne()
        {
            URDFPackage odd = new URDFPackage("my robot", dir);
            Directory.CreateDirectory(odd.WindowsMeshesDirectory);
            File.WriteAllBytes(Path.Combine(dir, "my_robot", "meshes", "base.STL"), new byte[] { 1 });
            Assert.Empty(MeshFiles.LinksWithoutFile(MakeLink("base"), odd, MeshExportFormat.STL));
        }
    }
}
