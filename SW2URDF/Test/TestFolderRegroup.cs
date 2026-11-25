using SW2URDF.UI;
using SW2URDF.URDFExport;
using System;
using System.Linq;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// The folder of the settings entries is deleted and created again to change its content. What
    /// happens when SolidWorks does not create it. Uses a fake document, so it does not need SolidWorks.
    /// </summary>
    public class TestFolderRegroup
    {
        private const string Folder = ConfigurationSerialization.ConfigurationAttributeFolderName;

        /// <summary>The folder holds the entry of A, the active configuration is B.</summary>
        private static FakeSettingsModel ModelToRegroup()
        {
            // The warning is shown once per document, so every test has its own
            FakeSettingsModel model = new FakeSettingsModel("A", "B") { Path = Guid.NewGuid().ToString() };
            model.AddEntry("A", "settings of a", "A");
            model.AddEntry("B", "settings of b", "B");
            model.AddFolder(Folder, "A");
            model.Activate("B");
            return model;
        }

        private static void CheckDataIsKept(FakeSettingsModel model)
        {
            Assert.Equal("settings of b", model.DataOf("B"));
            model.Activate("A");
            Assert.Equal("settings of a", model.DataOf("A"));
            model.Activate("B");
        }

        [Fact]
        public void TheEntryOfTheActiveConfigurationJoinsTheFolder()
        {
            FakeSettingsModel model = ModelToRegroup();
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Assert.True(ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document));
                Assert.Equal(new[] { "A", "B" }, model.ContentOf(Folder).OrderBy(n => n));
                Assert.Equal(1, model.FoldersInserted);
                Assert.False(ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document));
                Assert.Empty(box.Messages);
            }
        }

        [Fact]
        public void TheFolderIsCreatedOnTheSecondTry()
        {
            FakeSettingsModel model = ModelToRegroup();
            model.InsertFails = n => n == 1;
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
                Assert.Equal(new[] { "A", "B" }, model.ContentOf(Folder).OrderBy(n => n));
                Assert.Empty(box.Messages);
            }
        }

        [Fact]
        public void TheUserIsToldOnceWhenTheFolderIsGone()
        {
            FakeSettingsModel model = ModelToRegroup();
            model.InsertFails = n => true;
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
                Assert.Null(model.ContentOf(Folder));
                Assert.Contains(Folder, Assert.Single(box.Messages));
                CheckDataIsKept(model);

                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
                Assert.Single(box.Messages);
            }
        }

        [Fact]
        public void AnExceptionOfSolidWorksIsHandledTheSameWay()
        {
            FakeSettingsModel model = ModelToRegroup();
            model.InsertThrows = true;
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
                Assert.Contains(Folder, Assert.Single(box.Messages));
                CheckDataIsKept(model);
            }
        }

        [Fact]
        public void TheFolderComesBackTheNextTime()
        {
            FakeSettingsModel model = ModelToRegroup();
            model.InsertThrows = true;
            using (new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
                model.InsertThrows = false;
                Assert.True(ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document));
                Assert.Contains("B", model.ContentOf(Folder));
                CheckDataIsKept(model);
            }
        }

        [Fact]
        public void TheWarningIsTranslated()
        {
            FakeSettingsModel model = ModelToRegroup();
            model.InsertFails = n => true;
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document, new UiText("chinese"));
                Assert.Contains("「" + Folder + "」", Assert.Single(box.Messages));
            }
        }
    }
}
