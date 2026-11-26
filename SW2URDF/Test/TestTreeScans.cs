using SW2URDF.URDFExport;
using System.Linq;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Walking the feature tree costs a SolidWorks call per feature, so the number of walks must not
    /// grow with the number of configurations. Uses a fake document, so it does not need SolidWorks.
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
    }
}
