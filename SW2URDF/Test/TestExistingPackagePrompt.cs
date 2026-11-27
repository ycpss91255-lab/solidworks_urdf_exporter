using SW2URDF.UI;
using SW2URDF.URDFExport;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks and shows no dialog. The question before an existing package is overwritten (issue 280).</summary>
    public class TestExistingPackagePrompt : IDisposable
    {
        private sealed class FakeMessageBox : IMessageBox
        {
            public MessageBoxResult Answer = MessageBoxResult.No;
            public int Shown;
            public string Message;
            public string Caption;
            public MessageBoxButton Buttons;

            public MessageBoxResult Show(string message)
            {
                Shown++;
                return Answer;
            }

            public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons)
            {
                Shown++;
                Message = message;
                Caption = caption;
                Buttons = buttons;
                return Answer;
            }
        }

        private static readonly UiText English = new UiText(null);
        private static readonly UiText Chinese = new UiText("chinese");
        private readonly string folder = Path.Combine(Path.GetTempPath(), "sw2urdf_existing_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        private string Package()
        {
            Directory.CreateDirectory(Path.Combine(folder, "urdf"));
            File.WriteAllText(Path.Combine(folder, "package.xml"), "<package/>");
            return folder + @"\";
        }

        [Fact]
        public void NothingIsAskedForANewFolder()
        {
            FakeMessageBox box = new FakeMessageBox();
            Assert.True(ExistingPackagePrompt.Confirm(folder + @"\", box, English));
            Assert.Equal(0, box.Shown);
        }

        [Fact]
        public void NothingIsAskedForAnEmptyFolder()
        {
            Directory.CreateDirectory(folder);
            FakeMessageBox box = new FakeMessageBox();
            Assert.True(ExistingPackagePrompt.Confirm(folder + @"\", box, English));
            Assert.Equal(0, box.Shown);
        }

        [Fact]
        public void AFolderWithFilesIsAskedAboutOnce()
        {
            string package = Package();
            FakeMessageBox box = new FakeMessageBox();
            ExistingPackagePrompt.Confirm(package, box, English);
            Assert.Equal(1, box.Shown);
            Assert.Equal(MessageBoxButton.YesNo, box.Buttons);
            Assert.Contains("\r\n\r\n" + folder + "\r\n\r\n", box.Message);
        }

        [Fact]
        public void AFolderWithAnyFileIsAskedAbout()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "hand written");
            FakeMessageBox box = new FakeMessageBox { Answer = MessageBoxResult.Yes };
            Assert.True(ExistingPackagePrompt.Confirm(folder + @"\", box, English));
            Assert.Equal(1, box.Shown);
        }

        [Fact]
        public void OnlyYesOverwrites()
        {
            string package = Package();
            Assert.True(ExistingPackagePrompt.Confirm(package, new FakeMessageBox { Answer = MessageBoxResult.Yes }, English));
            Assert.False(ExistingPackagePrompt.Confirm(package, new FakeMessageBox { Answer = MessageBoxResult.No }, English));
            Assert.False(ExistingPackagePrompt.Confirm(package, new FakeMessageBox { Answer = MessageBoxResult.None }, English));
            Assert.True(File.Exists(Path.Combine(folder, "package.xml")));
        }

        [Fact]
        public void TextsAreTranslated()
        {
            string package = Package();
            FakeMessageBox english = new FakeMessageBox();
            FakeMessageBox chinese = new FakeMessageBox();
            ExistingPackagePrompt.Confirm(package, english, English);
            ExistingPackagePrompt.Confirm(package, chinese, Chinese);
            Assert.Equal("The package exists already", english.Caption);
            Assert.Equal("套件已經存在", chinese.Caption);
            string[] separator = { "\r\n\r\n" };
            string[] a = english.Message.Split(separator, StringSplitOptions.None);
            string[] b = chinese.Message.Split(separator, StringSplitOptions.None);
            Assert.Equal(3, a.Length);
            Assert.Equal(3, b.Length);
            Assert.NotEqual(a[0], b[0]);
            Assert.Equal(folder, b[1]);
            Assert.NotEqual(a[2], b[2]);
            // Full-width brackets and colons next to Latin text are drawn with wide gaps.
            Assert.DoesNotContain(b[0], c => "（）：".Contains(c));
        }
    }
}
