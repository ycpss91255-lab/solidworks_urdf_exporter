using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// SolidWorks records deleting the folder and inserting the new one as two undo steps; undoing
    /// only the last one leaves the entries without their folder. Every change of the folder in one
    /// operation is recorded as one undo step. Uses a fake document, so it does not need SolidWorks.
    /// </summary>
    public class TestRegroupUndo
    {
        private const string Folder = ConfigurationSerialization.ConfigurationAttributeFolderName;

        /// <summary>The folder holds the entry of A, the active configuration is B.</summary>
        private static FakeSettingsModel ModelToRegroup()
        {
            FakeSettingsModel model = new FakeSettingsModel("A", "B") { Path = Guid.NewGuid().ToString() };
            model.AddEntry("A", Stored("a"), "A");
            model.AddEntry("B", Stored("b"), "B");
            model.AddFolder(Folder, "A");
            model.Activate("B");
            return model;
        }

        private static string Stored(string name)
        {
            FakeSettingsModel model = new FakeSettingsModel();
            ConfigurationSerialization.SaveConfigTreeXML(
                model.App, model.Document, new LinkNode(new Link { Name = name }), false);
            return model.DataOf("Default");
        }

        /// <summary>All tree changes happen inside one recording, and there is only one.</summary>
        private static void AssertOneUndoStep(List<string> events)
        {
            Assert.Contains(events, e => e != "start" && e != "finish");
            Assert.Equal(1, events.Count(e => e == "start"));
            Assert.Equal(1, events.Count(e => e == "finish"));
            int start = events.IndexOf("start");
            int finish = events.IndexOf("finish");
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] != "start" && events[i] != "finish")
                {
                    Assert.True(i > start && i < finish, events[i] + " is outside the undo step: " + string.Join(", ", events));
                }
            }
        }

        [Fact]
        public void OpeningTheExporterRegroupsInOneUndoStep()
        {
            FakeSettingsModel model = ModelToRegroup();
            using (new FakeMessageBox())
            {
                Assert.True(ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document));
            }
            Assert.Equal(new[] { "A", "B" }, model.ContentOf(Folder).OrderBy(n => n));
            AssertOneUndoStep(model.UndoEvents);
        }

        [Fact]
        public void StrayFoldersAndOldSharedSettingsGoInTheSameUndoStep()
        {
            FakeSettingsModel model = new FakeSettingsModel("A") { Path = Guid.NewGuid().ToString() };
            model.AddShared(ConfigurationFormat.SharedAttributeName, Stored("same"));
            model.AddEntry("A", Stored("same"), "A");
            model.AddFolder("Folder1", "A");
            using (new FakeMessageBox())
            {
                Assert.True(ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document));
            }
            Assert.Null(model.Find("Folder1"));
            Assert.Null(model.Find(ConfigurationFormat.SharedAttributeName));
            Assert.Equal(new[] { "A" }, model.ContentOf(Folder));
            AssertOneUndoStep(model.UndoEvents);
        }

        [Fact]
        public void SavingTheSettingsRegroupsInOneUndoStep()
        {
            FakeSettingsModel model = ModelToRegroup();
            using (new FakeMessageBox())
            {
                ConfigurationSerialization.SaveConfigTreeXML(
                    model.App, model.Document, new LinkNode(new Link { Name = "new" }), false);
            }
            Assert.Equal(new[] { "A", "B" }, model.ContentOf(Folder).OrderBy(n => n));
            AssertOneUndoStep(model.UndoEvents);
        }

        [Fact]
        public void TidyingTheFolderAfterAnExportIsOneUndoStep()
        {
            FakeSettingsModel model = ModelToRegroup();
            using (new FakeMessageBox())
            {
                ConfigurationSerialization.GroupUrdfFeatures(model.Document, new UiText(null));
            }
            Assert.Equal(new[] { "A", "B" }, model.ContentOf(Folder).OrderBy(n => n));
            AssertOneUndoStep(model.UndoEvents);
        }

        [Fact]
        public void TheRecordingIsFinishedWhenSolidWorksFails()
        {
            FakeSettingsModel model = ModelToRegroup();
            model.InsertThrows = true;
            using (new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
            }
            AssertOneUndoStep(model.UndoEvents);
        }
    }
}
