using System;
using System.IO;
using SW2URDF.SW;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks. Checks where the icons of the menu items are looked for (issue 131).</summary>
    public class TestMenuImages
    {
        [Fact]
        public void TheIconsAreInTheFolderOfTheAddIn()
        {
            string folder = Path.Combine(Path.GetDirectoryName(typeof(SwAddin).Assembly.Location), "images");
            foreach (string image in SwAddin.MenuImages())
            {
                Assert.Equal(folder, Path.GetDirectoryName(image), StringComparer.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void TheIconsAreBuiltWithTheAddIn()
        {
            foreach (string image in SwAddin.MenuImages())
            {
                string built = Path.Combine(Path.GetDirectoryName(typeof(SwAddin).Assembly.Location),
                    "images", Path.GetFileName(image));
                Assert.True(File.Exists(built), built);
            }
        }

        // From the smallest to the largest, as AddMenuItem5 expects them
        [Fact]
        public void TheIconsCoverTheSizesOfSolidWorks()
        {
            Assert.Equal(new[] { "20", "32", "40", "64", "96", "128" },
                Array.ConvertAll(SwAddin.MenuImages(), image => Path.GetFileNameWithoutExtension(image).Split('_', 'x')[2]));
        }
    }
}
