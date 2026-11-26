using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Moq;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. An export leaves SolidWorks the way it was: the progress bar ends, the user
    /// preferences and the visible components come back, also when the export fails.
    /// </summary>
    public class TestExportRestoresState
    {
        private static FakeExport NewExport()
        {
            FakeExport export = new FakeExport("base_link", "arm");
            export.AddComponent("hidden by the user");
            export.Visible.Remove("hidden by the user");
            return export;
        }

        private static void AssertRestored(FakeExport export)
        {
            Assert.Equal("", export.ChangedPreferences());
            Assert.Equal(new[] { "arm", "base_link" }, export.Visible.OrderBy(name => name));
            Assert.Equal(1, export.ProgressStarts);
            Assert.Equal(1, export.ProgressEnds);
        }

        private static void Throw()
        {
            throw new COMException("SolidWorks failed");
        }

        [Fact]
        public void AfterSuccess()
        {
            using (FakeExport export = NewExport())
            {
                export.Helper.ExportRobot(true);
                Assert.True(File.Exists(export.UrdfFile));
                AssertRestored(export);
            }
        }

        [Fact]
        public void PreferencesAreRestoredOnce()
        {
            using (FakeExport export = NewExport())
            {
                export.Helper.ExportRobot(true);
                export.App.Verify(a => a.SetUserPreferenceIntegerValue(It.IsAny<int>(), 7), Times.Once);
            }
        }

        [Fact]
        public void AfterAMeshThatCouldNotBeSaved()
        {
            using (FakeExport export = NewExport())
            {
                export.OnSaveAs = path => false;
                export.Helper.ExportRobot(true);
                Assert.Equal(2, export.Messages.Count);
                AssertRestored(export);
            }
        }

        [Fact]
        public void AfterAFailureToSelectTheComponents()
        {
            using (FakeExport export = NewExport())
            {
                export.OnSelectAll = Throw;
                Record.Exception(() => export.Helper.ExportRobot(true));
                AssertRestored(export);
                Assert.False(File.Exists(export.UrdfFile));
                Assert.Contains("failed unexpectedly", export.Messages.Last());
            }
        }

        [Fact]
        public void AfterAFailureToHideTheComponents()
        {
            using (FakeExport export = NewExport())
            {
                export.OnHide = Throw;
                Record.Exception(() => export.Helper.ExportRobot(true));
                AssertRestored(export);
                Assert.False(File.Exists(export.UrdfFile));
                Assert.Contains("failed unexpectedly", export.Messages.Last());
            }
        }

        [Fact]
        public void AfterAFailureToShowTheComponents()
        {
            using (FakeExport export = NewExport())
            {
                int shown = 0;
                // The base link and the arm are shown one by one, then everything.
                export.OnShow = () => { if (++shown == 3) Throw(); };
                Record.Exception(() => export.Helper.ExportRobot(true));
                Assert.Equal(3, shown);
                Assert.Equal("", export.ChangedPreferences());
                Assert.Equal(1, export.ProgressEnds);
            }
        }

        [Fact]
        public void AfterAFailureToSetAPreference()
        {
            using (FakeExport export = NewExport())
            {
                // Only the value of the export, not the one of the user
                export.App.Setup(a => a.SetUserPreferenceIntegerValue(It.IsAny<int>(), 2)).Throws(new COMException("SolidWorks failed"));
                Record.Exception(() => export.Helper.ExportRobot(true));
                AssertRestored(export);
            }
        }

        [Fact]
        public void AfterAPackageThatCannotBeCreated()
        {
            using (FakeExport export = NewExport())
            {
                File.WriteAllText(Path.Combine(export.Directory, "package"), "a file where the folder belongs");
                Assert.NotNull(Record.Exception(() => export.Helper.ExportRobot(true)));
                AssertRestored(export);
            }
        }

        [Fact]
        public void AfterAUrdfThatCannotBeWritten()
        {
            using (FakeExport export = NewExport())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(export.UrdfFile));
                using (new FileStream(export.UrdfFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Assert.NotNull(Record.Exception(() => export.Helper.ExportRobot(true)));
                }
                AssertRestored(export);
            }
        }

        [Fact]
        public void PreferencesAfterAPartExport()
        {
            using (FakeExport export = new FakeExport("part"))
            {
                export.Helper.ExportLink(true);
                Assert.Equal("", export.ChangedPreferences());
                Assert.True(File.Exists(Path.Combine(export.Directory, "package", "urdf", "package.urdf")));
            }
        }

        [Fact]
        public void PreferencesAfterAPartExportThatFailsToSetThem()
        {
            using (FakeExport export = new FakeExport("part"))
            {
                export.Extension.Setup(e => e.SetUserPreferenceString(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                    .Throws(new COMException("SolidWorks failed"));
                Record.Exception(() => export.Helper.ExportLink(true));
                Assert.Equal("", export.ChangedPreferences());
            }
        }

        [Fact]
        public void PreferencesAfterAPartMeshThatCouldNotBeSaved()
        {
            using (FakeExport export = new FakeExport("part"))
            {
                export.OnSaveAs = path => false;
                export.Helper.ExportLink(true);
                Assert.Equal("", export.ChangedPreferences());
            }
        }
    }
}
