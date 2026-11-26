using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A texture that is in the package already, as after an export before, or that is
    /// gone does not end the export.
    /// </summary>
    public class TestTextureCopy
    {
        private static void Run(string textureFile, bool exists, Action<FakeExport> export, Action<FakeExport, string, string> check)
        {
            using (FakeExport fake = new FakeExport("base_link"))
            {
                string texture = Path.Combine(fake.Directory, textureFile);
                Directory.CreateDirectory(Path.GetDirectoryName(texture));
                if (exists) File.WriteAllText(texture, "picture");
                fake.Robot.BaseLink.Visual.Material.Name = "painted";
                fake.Robot.BaseLink.Visual.Material.Texture.wFilename = texture;
                Assert.Null(Record.Exception(() => export(fake)));
                Assert.Single(fake.Messages);
                Assert.True(File.Exists(fake.UrdfFile));
                XElement element = XDocument.Load(fake.UrdfFile).Descendants("texture").SingleOrDefault();
                check(fake, texture, element == null ? null : (string)element.Attribute("filename"));
            }
        }

        private static readonly Action<FakeExport>[] Exports =
        {
            fake => fake.Helper.ExportRobot(true),
            fake => fake.Helper.ExportLink(true)
        };

        [Fact]
        public void TextureIsCopied()
        {
            foreach (Action<FakeExport> export in Exports)
            {
                Run("picture.png", true, export, (fake, texture, inUrdf) =>
                {
                    Assert.Equal("package://package/textures/picture.png", inUrdf);
                    Assert.Equal("picture", File.ReadAllText(Path.Combine(fake.Directory, @"package\textures\picture.png")));
                });
            }
        }

        [Fact]
        public void TextureInThePackageStaysThere()
        {
            foreach (Action<FakeExport> export in Exports)
            {
                Run(@"package\textures\picture.png", true, export, (fake, texture, inUrdf) =>
                {
                    Assert.Equal("package://package/textures/picture.png", inUrdf);
                    Assert.Equal("picture", File.ReadAllText(texture));
                });
            }
        }

        [Fact]
        public void SamePathWrittenInAnotherWayStaysThere()
        {
            foreach (Action<FakeExport> export in Exports)
            {
                Run(@"package\urdf\..\TEXTURES\Picture.png", true, export, (fake, texture, inUrdf) =>
                {
                    Assert.Equal("package://package/textures/Picture.png", inUrdf);
                    Assert.Equal("picture", File.ReadAllText(texture));
                });
            }
        }

        [Fact]
        public void MissingTextureIsLeftOut()
        {
            foreach (Action<FakeExport> export in Exports)
            {
                Run("missing.png", false, export, (fake, texture, inUrdf) => Assert.Null(inUrdf));
            }
        }

        // Such as on another computer that has the package but not the source of the picture.
        [Fact]
        public void MissingTextureThatIsInThePackageIsKept()
        {
            foreach (Action<FakeExport> export in Exports)
            {
                Run("picture.png", false, fake =>
                {
                    Directory.CreateDirectory(Path.Combine(fake.Directory, @"package\textures"));
                    File.WriteAllText(Path.Combine(fake.Directory, @"package\textures\picture.png"), "picture");
                    export(fake);
                }, (fake, texture, inUrdf) => Assert.Equal("package://package/textures/picture.png", inUrdf));
            }
        }

        [Fact]
        public void TextureThatWentMissingIsLeftOut()
        {
            foreach (Action<FakeExport> export in Exports)
            {
                Run("picture.png", true, fake =>
                {
                    export(fake);
                    fake.Messages.Clear();
                    File.Delete(Path.Combine(fake.Directory, "picture.png"));
                    Directory.Delete(Path.Combine(fake.Directory, @"package\textures"), true);
                    export(fake);
                }, (fake, texture, inUrdf) => Assert.Null(inUrdf));
            }
        }
    }
}
