using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System;
using System.Linq;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Walking the feature tree costs a SolidWorks call per feature, so the number of walks must not
    /// grow with the number of configurations, and an operation that does not change the tree walks
    /// it once. What a walk received from SolidWorks is released again. Uses a fake document, so it
    /// does not need SolidWorks.
    /// </summary>
    public class TestTreeScans
    {
        private static int WalksToOpen(int configurations, bool shared)
        {
            string[] names = Enumerable.Range(1, configurations).Select(n => "Configuration " + n).ToArray();
            FakeSettingsModel model = new FakeSettingsModel(names);
            foreach (string name in names)
            {
                model.AddEntry(name, "settings", name);
            }
            if (shared)
            {
                // Cannot be read, so the cleanup stops after it looked for the entries
                model.AddShared(ConfigurationFormat.SharedAttributeName, "not readable");
            }
            using (new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
            }
            return model.Walks;
        }

        [Fact]
        public void PreparingTheEntriesDoesNotWalkTheTreeForEveryConfiguration()
        {
            Assert.Equal(WalksToOpen(2, false), WalksToOpen(30, false));
        }

        [Fact]
        public void LookingForSharedSettingsDoesNotWalkTheTreeForEveryConfiguration()
        {
            Assert.Equal(WalksToOpen(2, true), WalksToOpen(30, true));
        }

        private const string Folder = ConfigurationSerialization.ConfigurationAttributeFolderName;

        /// <summary>Features of the user around the entries of A and B, both in the folder; A is active.</summary>
        private static FakeSettingsModel Prepared(bool sharedOnly = false)
        {
            FakeSettingsModel model = new FakeSettingsModel("A", "B") { Path = Guid.NewGuid().ToString() };
            model.Add("RefPlane", "Front Plane");
            model.Add("Extrusion", "Boss-Extrude1");
            if (sharedOnly)
            {
                model.AddShared(ConfigurationFormat.SharedAttributeName, Stored("shared"));
                model.AddEntry("A", "", "A");
                model.AddEntry("B", "", "B");
                model.AddFolder(Folder, "A", "B", ConfigurationFormat.SharedAttributeName);
            }
            else
            {
                model.AddEntry("A", Stored("a"), "A");
                model.AddEntry("B", Stored("b"), "B");
                model.AddFolder(Folder, "A", "B");
            }
            model.Add("Attribute", "Other attribute");
            model.Add("CoordSys", "Coordinate System1");
            return model;
        }

        private static string Stored(string name)
        {
            FakeSettingsModel model = new FakeSettingsModel();
            ConfigurationSerialization.SaveConfigTreeXML(
                model.App, model.Document, new LinkNode(new Link { Name = name }), false);
            return model.DataOf("Default");
        }

        /// <summary>Runs one operation: the number of walks, with every received feature released.</summary>
        private static int Walks(FakeSettingsModel model, Action<FakeSettingsModel> operation)
        {
            int before = model.Walks;
            using (new FakeMessageBox())
            using (model.TrackReleases())
            {
                operation(model);
            }
            Assert.Empty(model.ReleaseErrors);
            Assert.Empty(model.NotReleased());
            return model.Walks - before;
        }

        [Fact]
        public void LoadingTheSettingsWalksTheTreeOnce()
        {
            FakeSettingsModel model = Prepared();
            Assert.Equal(1, Walks(model, m =>
                Assert.Equal("a", ConfigurationSerialization.LoadBaseNodeFromModel(m.Document, out bool error).Link.Name)));
        }

        [Fact]
        public void LoadingSharedSettingsWalksTheTreeOnce()
        {
            FakeSettingsModel model = Prepared(true);
            Assert.Equal(1, Walks(model, m =>
                Assert.Equal("shared", ConfigurationSerialization.LoadBaseNodeFromModel(m.Document, out bool error).Link.Name)));
        }

        [Fact]
        public void SavingIntoTheEntryOfTheConfigurationWalksTheTreeOnce()
        {
            FakeSettingsModel model = Prepared();
            Assert.Equal(1, Walks(model, m => ConfigurationSerialization.SaveConfigTreeXML(
                m.App, m.Document, new LinkNode(new Link { Name = "new" }), false)));
            Assert.Equal(Stored("new"), model.DataOf("A"));
        }

        [Fact]
        public void OpeningAPreparedModelWalksTheTreeOnce()
        {
            FakeSettingsModel model = Prepared();
            Assert.Equal(1, Walks(model, m =>
                Assert.False(ConfigurationSerialization.EnsureConfigurationAttributes(m.App, m.Document))));
        }

        [Fact]
        public void TidyingAGroupedFolderWalksTheTreeOnce()
        {
            FakeSettingsModel model = Prepared();
            Assert.Equal(1, Walks(model, m => ConfigurationSerialization.GroupUrdfFeatures(m.Document, new UiText(null))));
            Assert.Equal(0, model.Saves);
        }

        [Fact]
        public void DescribingTheSettingsWalksTheTreeOnce()
        {
            Assert.Equal(1, Walks(Prepared(), m =>
                Assert.Equal(SettingsSource.Configuration, ConfigurationSerialization.GetSettingsSource(m.Document))));
            Assert.Equal(1, Walks(Prepared(true), m =>
                Assert.Equal(SettingsSource.Shared, ConfigurationSerialization.GetSettingsSource(m.Document))));
        }

        [Fact]
        public void ChangingTheTreeReleasesWhatItReceived()
        {
            // A new entry for B, the folder made again around both, the shared settings removed
            FakeSettingsModel model = new FakeSettingsModel("A", "B") { Path = Guid.NewGuid().ToString() };
            model.Add("RefPlane", "Front Plane");
            model.AddShared(ConfigurationFormat.SharedAttributeName, Stored("shared"));
            model.AddEntry("A", Stored("shared"), "A");
            model.AddFolder(Folder, "A");
            model.Add("FtrFolder", "Folder1");
            model.Activate("B");
            Walks(model, m => Assert.True(ConfigurationSerialization.EnsureConfigurationAttributes(m.App, m.Document)));
            Assert.Equal(new[] { "A", "B" }, model.ContentOf(Folder).OrderBy(n => n));
            Assert.Null(model.Find(ConfigurationFormat.SharedAttributeName));
            Assert.Equal(Stored("shared"), model.DataOf("B"));
        }
    }
}
