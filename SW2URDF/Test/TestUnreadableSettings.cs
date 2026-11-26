using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Stored settings that cannot be read are reported, kept and never overwritten. Uses a fake
    /// document, so it does not need SolidWorks.
    /// </summary>
    public class TestUnreadableSettings
    {
        private const string Broken = "<Link xmlns=\"http://schemas.datacontract.org/2004/07/SW2URDF\"><cut off";
        private const string Entry = "Default";

        private static LinkNode Tree(string name)
        {
            return new LinkNode(new Link { Name = name });
        }

        private static string Stored(string name)
        {
            FakeSettingsModel model = new FakeSettingsModel();
            ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, Tree(name), false);
            return model.DataOf(Entry);
        }

        private static void CheckLoadIsRefused(string data, double version)
        {
            FakeSettingsModel model = new FakeSettingsModel();
            model.AddEntry(Entry, data, null, version);
            using (FakeMessageBox box = new FakeMessageBox())
            {
                LinkNode node = ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out bool error);

                Assert.True(error);
                Assert.Null(node);
                Assert.Single(box.Messages);
            }
        }

        private static void CheckSaveKeeps(string data, double version)
        {
            FakeSettingsModel model = new FakeSettingsModel();
            model.AddEntry(Entry, data, null, version);
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, Tree("new"), false);

                Assert.Equal(data, model.DataOf(Entry));
                Assert.Equal(0, model.Saves);
                Assert.Single(box.Messages);
            }
        }

        [Fact]
        public void LoadingDataThatIsCutOffIsAnError()
        {
            CheckLoadIsRefused(Broken, 1.4);
        }

        [Fact]
        public void LoadingTextThatIsNoXmlIsAnError()
        {
            CheckLoadIsRefused("not xml", 1.4);
        }

        [Fact]
        public void LoadingAnotherKindOfDataIsAnError()
        {
            CheckLoadIsRefused("<string xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/\">a</string>", 1.4);
        }

        [Fact]
        public void LoadingBrokenOldXmlIsAnError()
        {
            CheckLoadIsRefused("<SerialNode><linkName>base", 1.2);
        }

        [Fact]
        public void LoadingOtherXmlAsOldXmlIsAnError()
        {
            CheckLoadIsRefused("<other />", 1.2);
        }

        [Fact]
        public void BrokenDataIsNotOverwritten()
        {
            CheckSaveKeeps(Broken, 1.4);
        }

        [Fact]
        public void BrokenOldXmlIsNotOverwritten()
        {
            CheckSaveKeeps("<SerialNode><linkName>base", 1.2);
        }

        [Fact]
        public void DataOfANewerExporterIsNotOverwritten()
        {
            CheckSaveKeeps(Stored("base"), 9.9);
        }

        [Fact]
        public void BrokenSharedDataDoesNotBlockTheEntryOfTheConfiguration()
        {
            // The shared data stays as it is when the configuration's own entry is written
            FakeSettingsModel model = new FakeSettingsModel();
            model.AddShared(ConfigurationFormat.SharedAttributeName, Broken);
            model.AddEntry(Entry, "");
            using (new FakeMessageBox())
            {
                ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, Tree("new"), false);
            }
            Assert.Contains("new", model.DataOf(Entry));
            Assert.Equal(Broken, model.DataOf(ConfigurationFormat.SharedAttributeName));
        }

        [Fact]
        public void ReadableSettingsAreLoadedAndReplaced()
        {
            FakeSettingsModel model = new FakeSettingsModel();
            model.AddEntry(Entry, Stored("base"));
            using (FakeMessageBox box = new FakeMessageBox())
            {
                LinkNode node = ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out bool error);
                Assert.False(error);
                Assert.Equal("base", node.Link.Name);

                ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, Tree("other"), false);
                Assert.Equal(Stored("other"), model.DataOf(Entry));
                Assert.Equal(1, model.Saves);
                Assert.Empty(box.Messages);
            }
        }

        [Fact]
        public void ReadableOldXmlIsLoaded()
        {
            FakeSettingsModel model = new FakeSettingsModel();
            model.AddEntry(Entry, "<SerialNode><linkName>base</linkName><jointName>joint</jointName><Nodes /></SerialNode>", null, 1.2);
            LinkNode node = ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out bool error);
            Assert.False(error);
            Assert.Equal("base", node.Link.Name);
        }

        [Fact]
        public void NothingStoredIsNoError()
        {
            FakeSettingsModel model = new FakeSettingsModel();
            Assert.Null(ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out bool error));
            Assert.False(error);
            model.AddEntry(Entry, "");
            Assert.Null(ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out error));
            Assert.False(error);
        }

        [Fact]
        public void TheMessagesNameTheConfigurationAndAreTranslated()
        {
            foreach (string language in new[] { null, "chinese" })
            {
                FakeSettingsModel model = new FakeSettingsModel("Wide 2");
                model.AddEntry("Wide 2", Broken);
                UiText ui = new UiText(language);
                using (FakeMessageBox box = new FakeMessageBox())
                {
                    ConfigurationSerialization.LoadBaseNodeFromModel(model.Document, out bool error, ui);
                    ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, Tree("new"), false, ui);
                    Assert.Equal(2, box.Messages.Count);
                    foreach (string message in box.Messages)
                    {
                        Assert.Contains("Wide 2", message);
                        Assert.Equal(language == "chinese", message.Contains("設定"));
                    }
                }
            }
        }
    }
}
