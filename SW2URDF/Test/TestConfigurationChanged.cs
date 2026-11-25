using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Another configuration is activated while the exporter is open: its settings are kept. Uses a
    /// fake document, so it does not need SolidWorks; the page and the form are never shown.
    /// </summary>
    public class TestConfigurationChanged
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static T Opened<T>(FakeSettingsModel model, string configuration)
        {
            object target = FormatterServices.GetUninitializedObject(typeof(T));
            GC.SuppressFinalize(target);
            Set(target, "swApp", model.App);
            Set(target, "ActiveSWModel", model.Document);
            Set(target, "ui", new UiText(null));
            Set(target, "loadedConfiguration", configuration);
            return (T)target;
        }

        private static void Set(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Members);
            if (field != null) field.SetValue(target, value);
        }

        private static void Call(object target, string method, params object[] arguments)
        {
            target.GetType().GetMethod(method, Members).Invoke(target, arguments);
        }

        private static LinkNode Tree(string name)
        {
            return new LinkNode(new Link { Name = name });
        }

        /// <summary>The exporter was opened for A, then B was activated.</summary>
        private static FakeSettingsModel Switched()
        {
            FakeSettingsModel model = new FakeSettingsModel("A", "B");
            model.AddEntry("A", "", "A");
            model.AddEntry("B", "", "B");
            model.Activate("B");
            ConfigurationSerialization.SaveConfigTreeXML(model.App, model.Document, Tree("of_b"), false);
            model.Saves = 0;
            return model;
        }

        private static void CheckSave(Action<FakeSettingsModel> save)
        {
            FakeSettingsModel model = Switched();
            string before = model.DataOf("B");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                save(model);
                Assert.Equal(before, model.DataOf("B"));
                Assert.Equal(0, model.Saves);
                Assert.Contains("\"A\"", Assert.Single(box.Messages));
                Assert.Contains("\"B\"", box.Messages[0]);
            }

            // Back in the configuration it was opened for, the save works
            model.Activate("A");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                save(model);
                Assert.Contains("of_a", model.DataOf("A"));
                Assert.Empty(box.Messages);
            }
        }

        [Fact]
        public void ThePageDoesNotSaveItsTreeForTheOtherConfiguration()
        {
            CheckSave(model => Opened<ExportPropertyManager>(model, "A").SaveConfigTree(model.Document, Tree("of_a"), false));
        }

        [Fact]
        public void TheFormDoesNotSaveItsTreeForTheOtherConfiguration()
        {
            CheckSave(model => Opened<AssemblyExportForm>(model, "A").SaveConfigTree(model.Document, Tree("of_a"), false));
        }

        [Fact]
        public void ThePageDoesNotExportTheOtherConfiguration()
        {
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Call(Opened<ExportPropertyManager>(Switched(), "A"), "ExportButtonPress");
                Assert.Single(box.Messages);
            }
        }

        [Fact]
        public void TheFormDoesNotExportTheOtherConfiguration()
        {
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Call(Opened<AssemblyExportForm>(Switched(), "A"), "FinishExport", true);
                Assert.Single(box.Messages);
            }
        }

        [Fact]
        public void ARenamedConfigurationIsTheSameOne()
        {
            FakeSettingsModel model = new FakeSettingsModel("A", "B");
            model.RenameConfiguration("A", "New name");
            using (FakeMessageBox box = new FakeMessageBox())
            {
                Opened<AssemblyExportForm>(model, "A").SaveConfigTree(model.Document, Tree("of_a"), false);
                Assert.Contains("of_a", model.DataOf("New name"));
                Assert.Empty(box.Messages);
            }
        }

        [Fact]
        public void WithoutAKnownConfigurationTheSaveWorksAsBefore()
        {
            FakeSettingsModel model = new FakeSettingsModel("A");
            Opened<AssemblyExportForm>(model, null).SaveConfigTree(model.Document, Tree("of_a"), false);
            Assert.Contains("of_a", model.DataOf("A"));
        }

        [Fact]
        public void TheMessageIsTranslated()
        {
            FakeSettingsModel model = Switched();
            AssemblyExportForm form = Opened<AssemblyExportForm>(model, "A");
            Set(form, "ui", new UiText("chinese"));
            using (FakeMessageBox box = new FakeMessageBox())
            {
                form.SaveConfigTree(model.Document, Tree("of_a"), false);
                Assert.Contains("「A」", Assert.Single(box.Messages));
            }
        }
    }
}
