using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System.Windows;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// A configuration that was renamed gets the settings of its old name when the user wants them.
    /// Uses a fake document, so it does not need SolidWorks.
    /// </summary>
    public class TestRenamedConfiguration
    {
        private static string Stored(string name)
        {
            FakeSettingsModel model = new FakeSettingsModel();
            ConfigurationSerialization.SaveConfigTreeXML(
                model.App, model.Document, new LinkNode(new Link { Name = name }), false);
            return model.DataOf("Default");
        }

        private static FakeSettingsModel Renamed()
        {
            FakeSettingsModel model = new FakeSettingsModel("Old", "Other");
            model.AddEntry("Old", "settings of old");
            model.RenameConfiguration("Old", "New");
            return model;
        }

        private static bool Open(FakeSettingsModel model, FakeMessageBox box, params MessageBoxResult[] answers)
        {
            foreach (MessageBoxResult answer in answers)
            {
                box.Answers.Enqueue(answer);
            }
            return ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document);
        }

        [Fact]
        public void TheSettingsMoveToTheNewNameWhenTheUserWantsThem()
        {
            FakeSettingsModel model = Renamed();
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Assert.True(Open(model, box, MessageBoxResult.Yes));
                Assert.Equal("settings of old", model.DataOf("New"));
                // Only one entry keeps them: otherwise renaming the configuration back shows old settings
                Assert.Equal("", model.DataOf("Old"));
                Assert.Contains("\"New\"", Assert.Single(box.Messages));
                Assert.Contains("\"Old\"", box.Messages[0]);

                Open(model, box);
                Assert.Single(box.Messages);
            }
        }

        [Fact]
        public void TheMovedSettingsAreLoadedWithTheirVersion()
        {
            FakeSettingsModel model = new FakeSettingsModel("Old");
            model.AddEntry("Old", Stored("base"), null, 1.3);
            model.RenameConfiguration("Old", "New");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.Yes);
                LinkNode node = ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out bool error);
                Assert.False(error);
                Assert.Equal("base", node.Link.Name);
                Assert.Contains(1.3, model.Find("New").Version.Values);
            }
        }

        [Fact]
        public void NothingMovesWhenTheUserSaysNo()
        {
            FakeSettingsModel model = Renamed();
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.No);
                Assert.Equal("", model.DataOf("New"));
                Assert.Equal("settings of old", model.DataOf("Old"));

                // Nothing is saved under the new name yet: the offer stands
                Open(model, box, MessageBoxResult.Yes);
                Assert.Equal(2, box.Messages.Count);
                Assert.Equal("settings of old", model.DataOf("New"));
            }
        }

        [Fact]
        public void ABlankEntryOfAnEarlierVersionDoesNotHideTheOffer()
        {
            FakeSettingsModel model = Renamed();
            model.AddEntry("New", "");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Assert.True(Open(model, box, MessageBoxResult.Yes));
                Assert.Equal("settings of old", model.DataOf("New"));
                Assert.Equal("", model.DataOf("Old"));
            }
        }

        [Fact]
        public void ThereIsNoOfferWhenTheConfigurationHasSettings()
        {
            FakeSettingsModel model = Renamed();
            model.AddEntry("New", "own settings");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.Yes);
                Assert.Empty(box.Messages);
                Assert.Equal("own settings", model.DataOf("New"));
                Assert.Equal("settings of old", model.DataOf("Old"));
            }
        }

        [Fact]
        public void SettingsOfAnotherConfigurationAreNotOffered()
        {
            // The renamed configuration is not the active one
            FakeSettingsModel model = Renamed();
            model.Activate("Other");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.Yes);
                Assert.Empty(box.Messages);
                Assert.Equal("", model.DataOf("Other"));
            }
        }

        [Fact]
        public void EntriesThatMatchAConfigurationAreNotOffered()
        {
            FakeSettingsModel model = new FakeSettingsModel("A", "B");
            model.AddShared("A", "settings of a");
            model.Activate("B");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.Yes);
                Assert.Empty(box.Messages);
                Assert.Equal("settings of a", model.DataOf("A"));
            }
        }

        [Fact]
        public void TheOldSettingsComeBeforeTheSharedOnes()
        {
            FakeSettingsModel model = Renamed();
            model.AddShared(ConfigurationFormat.SharedAttributeName, "shared settings");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.Yes);
                Assert.Equal("settings of old", model.DataOf("New"));
                Assert.Equal("shared settings", model.DataOf(ConfigurationFormat.SharedAttributeName));
                Assert.Single(box.Messages);
            }

            model = Renamed();
            model.AddShared(ConfigurationFormat.SharedAttributeName, "shared settings");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.No);
                Assert.Equal("shared settings", model.DataOf("New"));
            }
        }

        [Fact]
        public void SeveralOldNamesAreOfferedOneByOne()
        {
            FakeSettingsModel model = Renamed();
            model.AddEntry("Older", "settings of older");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.No, MessageBoxResult.Yes);
                Assert.Equal(2, box.Messages.Count);
                Assert.Equal("settings of older", model.DataOf("New"));
                Assert.Equal("settings of old", model.DataOf("Old"));
                Assert.Equal("", model.DataOf("Older"));
            }
        }

        [Fact]
        public void TheOldEntryKeepsItsSettingsWhenTheyCannotBeMoved()
        {
            FakeSettingsModel model = Renamed();
            model.AddEntry("New", "").CanBeWritten = false;
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Open(model, box, MessageBoxResult.Yes);
                Assert.Equal("settings of old", model.DataOf("Old"));
            }
        }

        [Fact]
        public void TheQuestionIsTranslated()
        {
            FakeSettingsModel model = Renamed();
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.EnsureConfigurationAttributes(model.App, model.Document, new UiText("chinese"));
                string message = Assert.Single(box.Messages);
                Assert.Contains("「New」", message);
                Assert.Contains("「Old」", message);
            }
        }
    }
}
