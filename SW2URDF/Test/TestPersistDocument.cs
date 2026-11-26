using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Saving the document after the settings were stored in it. Uses a fake document, so it does
    /// not need SolidWorks.
    /// </summary>
    public class TestPersistDocument
    {
        private static string[] Persist(FakeSettingsModel model)
        {
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.PersistDocument(model.Document, new UiText(null));
                return box.Messages.ToArray();
            }
        }

        [Fact]
        public void ADocumentWithAFileIsSavedSilently()
        {
            FakeSettingsModel model = new FakeSettingsModel();
            Assert.Empty(Persist(model));
            Assert.Equal(1, model.Saves);
            Assert.Equal(1, model.SaveOptions);
        }

        [Fact]
        public void ADocumentThatWasNeverSavedIsNotSavedButReported()
        {
            foreach (string path in new[] { "", null })
            {
                FakeSettingsModel model = new FakeSettingsModel { Path = path };
                Assert.Equal(DocumentPersistence.WarningText(new UiText(null)), Assert.Single(Persist(model)));
                Assert.Equal(0, model.Saves);
            }
        }

        [Fact]
        public void AReadOnlyDocumentIsNotSavedButReported()
        {
            FakeSettingsModel model = new FakeSettingsModel { ReadOnly = true };
            Assert.Single(Persist(model));
            Assert.Equal(0, model.Saves);
        }

        [Fact]
        public void ASaveThatFailsIsReported()
        {
            Assert.Single(Persist(new FakeSettingsModel { SaveResult = false }));
            Assert.Single(Persist(new FakeSettingsModel { SaveThrows = true }));
            Assert.Contains("16", Assert.Single(Persist(new FakeSettingsModel { SaveErrors = 16 })));
        }

        [Fact]
        public void StoringSettingsSavesTheDocumentOnceAndOnlyWhenTheyChanged()
        {
            FakeSettingsModel model = new FakeSettingsModel();
            using (FakeMessageBox box = new FakeMessageBox())
            {
                LinkNode tree = new LinkNode(new Link { Name = "base" });
                ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, tree, false);
                Assert.Equal(1, model.Saves);
                ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, tree, false);
                Assert.Equal(1, model.Saves);
                Assert.Empty(box.Messages);
            }
        }

        [Fact]
        public void SettingsOfADocumentThatWasNeverSavedStayInTheOpenDocument()
        {
            FakeSettingsModel model = new FakeSettingsModel { Path = "" };
            using (FakeMessageBox box = new FakeMessageBox())
            {
                ConfigurationSerialization.SaveConfigTreeXML(
                    model.App, model.Document, new LinkNode(new Link { Name = "base" }), false);
                Assert.Single(box.Messages);
                Assert.Equal(0, model.Saves);
                Assert.Contains("base", model.DataOf("Default"));
            }
        }
    }
}
