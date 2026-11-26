using SW2URDF.UI;
using SW2URDF.URDFExport;
using System.Linq;
using System.Windows;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks and shows no dialog. The question about missing mesh files (issue 166).</summary>
    public class TestMissingMeshPrompt
    {
        private sealed class FakeMessageBox : IMessageBox
        {
            public MessageBoxResult Answer = MessageBoxResult.Cancel;
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

        private static MissingMeshChoice Choice(MessageBoxResult answer)
        {
            return MissingMeshPrompt.Ask(new[] { "arm" }, new FakeMessageBox { Answer = answer }, English);
        }

        [Fact]
        public void NothingIsAskedWhenNoMeshIsMissing()
        {
            FakeMessageBox box = new FakeMessageBox();
            Assert.Equal(MissingMeshChoice.ModelFileOnly, MissingMeshPrompt.Ask(new string[0], box, English));
            Assert.Equal(MissingMeshChoice.ModelFileOnly, MissingMeshPrompt.Ask(null, box, English));
            Assert.Equal(0, box.Shown);
        }

        [Fact]
        public void AnswersAreMapped()
        {
            Assert.Equal(MissingMeshChoice.ExportMeshes, Choice(MessageBoxResult.Yes));
            Assert.Equal(MissingMeshChoice.ModelFileOnly, Choice(MessageBoxResult.No));
            Assert.Equal(MissingMeshChoice.Cancel, Choice(MessageBoxResult.Cancel));
            Assert.Equal(MissingMeshChoice.Cancel, Choice(MessageBoxResult.None));
        }

        [Fact]
        public void MessageListsTheLinksOnce()
        {
            FakeMessageBox box = new FakeMessageBox();
            MissingMeshPrompt.Ask(new[] { "arm_1", "手臂" }, box, English);
            Assert.Equal(1, box.Shown);
            Assert.Equal(MessageBoxButton.YesNoCancel, box.Buttons);
            Assert.Contains("\r\n\r\narm_1, 手臂\r\n\r\n", box.Message);
        }

        [Fact]
        public void LinkNamesWithBracesAreShownAsTheyAre()
        {
            FakeMessageBox box = new FakeMessageBox();
            MissingMeshPrompt.Ask(new[] { "a{0}b" }, box, English);
            Assert.Contains("a{0}b", box.Message);
        }

        [Fact]
        public void TextsAreTranslated()
        {
            FakeMessageBox english = new FakeMessageBox();
            FakeMessageBox chinese = new FakeMessageBox();
            MissingMeshPrompt.Ask(new[] { "arm" }, english, English);
            MissingMeshPrompt.Ask(new[] { "arm" }, chinese, Chinese);
            Assert.Equal("Mesh files are missing", english.Caption);
            Assert.Equal("缺少網格檔", chinese.Caption);
            string[] separator = { "\r\n\r\n" };
            string[] a = english.Message.Split(separator, System.StringSplitOptions.None);
            string[] b = chinese.Message.Split(separator, System.StringSplitOptions.None);
            Assert.Equal(3, a.Length);
            Assert.Equal(3, b.Length);
            Assert.NotEqual(a[0], b[0]);
            Assert.Equal("arm", b[1]);
            Assert.NotEqual(a[2], b[2]);
            // Full-width brackets and colons next to Latin text are drawn with wide gaps.
            Assert.DoesNotContain(b[0], c => "（）：".Contains(c));
        }
    }
}
