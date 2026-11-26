using Moq;
using SW2URDF.URDFExport;
using System;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// The exporter works with the active configuration and does not switch to another one. Uses
    /// a fake document, so it does not need SolidWorks.
    /// </summary>
    public class TestConfigurationSwitch
    {
        private sealed class StopHere : Exception
        {
        }

        private static FakeSettingsModel OpenPage(params string[] configurations)
        {
            FakeSettingsModel model = new FakeSettingsModel(configurations);
            Mock.Get(model.App).SetupGet(a => a.ActiveDoc).Returns(model.Document);
            int errors = 0;
            // The page itself needs SolidWorks: stop where it is created
            Mock.Get(model.App).Setup(a => a.CreatePropertyManagerPage(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<object>(), ref errors)).Throws(new StopHere());
            Assert.Throws<StopHere>(() => new ExportPropertyManager(model.App));
            return model;
        }

        [Fact]
        public void OpeningThePageKeepsTheActiveConfiguration()
        {
            FakeSettingsModel model = OpenPage("Wide", "URDF Export");
            Assert.Equal("Wide", ConfigurationSerialization.GetActiveConfigurationName(model.Document));
            Assert.Empty(model.Shown);
        }

        [Fact]
        public void OpeningThePageDoesNotAskForAConfigurationThatDoesNotExist()
        {
            Assert.Empty(OpenPage("Default").Shown);
        }
    }
}
