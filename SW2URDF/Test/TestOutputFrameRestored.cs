using System.Collections.Generic;
using System.Linq;
using Moq;
using SolidWorks.Interop.swconst;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The coordinate system a mesh is saved relative to is a setting of the document.
    /// An export sets it for each link, and the document must have the choice of the user again afterwards.
    /// </summary>
    public class TestOutputFrameRestored
    {
        private const int NewSetting = (int)swUserPreferenceStringValue_e.swExportOutputCoordinateSystem;
        private const int OldSetting = (int)swUserPreferenceStringValue_e.swFileSaveAsCoordinateSystem;
        private const string ChoiceOfTheUser = "Frame of the user";

        private sealed class Settings
        {
            public readonly Dictionary<int, string> Values = new Dictionary<int, string>();
            public readonly List<string> UsedForMeshes = new List<string>();

            /// <summary>The document knows the accepted settings; a setting it does not know stays empty.</summary>
            public Settings(FakeExport export, params int[] accepted)
            {
                foreach (int setting in accepted) Values[setting] = ChoiceOfTheUser;
                export.Extension.Setup(e => e.GetUserPreferenceString(It.IsAny<int>(), It.IsAny<int>()))
                    .Returns((int setting, int option) => Values.ContainsKey(setting) ? Values[setting] : "");
                export.Extension.Setup(e => e.SetUserPreferenceString(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                    .Returns((int setting, int option, string value) =>
                    {
                        if (!Values.ContainsKey(setting)) return false;
                        Values[setting] = value;
                        return true;
                    });
                var save = export.OnSaveAs;
                export.OnSaveAs = path =>
                {
                    UsedForMeshes.Add(Values[accepted[0]]);
                    return save(path);
                };
            }
        }

        private static void AssertRestored(Settings settings)
        {
            Assert.All(settings.Values.Values, value => Assert.Equal(ChoiceOfTheUser, value));
        }

        [Fact]
        public void AfterAnAssemblyExport()
        {
            foreach (int[] accepted in new[] { new[] { NewSetting }, new[] { OldSetting }, new[] { NewSetting, OldSetting } })
            {
                using (FakeExport export = new FakeExport("base_link", "arm"))
                {
                    Settings settings = new Settings(export, accepted);
                    export.Helper.ExportRobot(true);
                    Assert.Equal(new[] { "Origin_global", "Origin_global" }, settings.UsedForMeshes);
                    AssertRestored(settings);
                }
            }
        }

        [Fact]
        public void AfterAFailedMesh()
        {
            using (FakeExport export = new FakeExport("base_link", "arm"))
            {
                export.OnSaveAs = path => false;
                Settings settings = new Settings(export, NewSetting);
                export.Helper.ExportRobot(true);
                AssertRestored(settings);
            }
        }

        [Fact]
        public void AfterAPartExport()
        {
            using (FakeExport export = new FakeExport("part"))
            {
                Settings settings = new Settings(export, NewSetting);
                export.Helper.ExportLink(true);
                Assert.Equal("", settings.UsedForMeshes.Single());
                AssertRestored(settings);
            }
        }

        [Fact]
        public void AChoiceMadeBetweenTwoExportsIsKept()
        {
            using (FakeExport export = new FakeExport("base_link"))
            {
                Settings settings = new Settings(export, NewSetting);
                export.Helper.ExportRobot(true);
                settings.Values[NewSetting] = "Another frame of the user";
                export.Helper.ExportRobot(true);
                Assert.Equal("Another frame of the user", settings.Values[NewSetting]);
            }
        }

        // An export without meshes does not touch the setting.
        [Fact]
        public void NothingIsSetWithoutMeshes()
        {
            using (FakeExport export = new FakeExport("base_link"))
            {
                new Settings(export, NewSetting);
                export.Helper.ExportRobot(false);
                export.Extension.Verify(e => e.SetUserPreferenceString(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
            }
        }
    }
}
