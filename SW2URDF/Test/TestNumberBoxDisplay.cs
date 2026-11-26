using System.Globalization;
using System.Windows.Forms;
using SW2URDF.UI;
using SW2URDF.URDF;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks. Numbers wider than their box (issue 173).</summary>
    public class TestNumberBoxDisplay
    {
        private static string Fit(string text, int characters)
        {
            return NumberBoxDisplay.Fit(text, shown => shown.Length <= characters);
        }

        [Fact]
        public void TextThatFitsStaysAsItIs()
        {
            Assert.Equal("2.56527281012E-05", Fit("2.56527281012E-05", 17));
            Assert.Equal("1.500", Fit("1.500", 5));
            Assert.Equal("", Fit("", 0));
        }

        [Fact]
        public void ExponentIsKeptAndDigitsAreRounded()
        {
            Assert.Equal("2.56527E-05", Fit("2.56527281012E-05", 11));
            Assert.Equal("2.5653E-05", Fit("2.56527281012E-05", 10));
            Assert.Equal("-2.6E-05", Fit("-2.56527281012E-05", 8));
            Assert.Equal("3E-05", Fit("2.56527281012E-05", 6));
            Assert.Equal("-1.5E-300", Fit("-1.49999999999999E-300", 10));
            Assert.Equal("2.5653E-05", Fit("2.56527281012e-5", 10));
        }

        [Fact]
        public void NumbersWithoutExponentKeepTheirSize()
        {
            Assert.Equal("-0.02", Fit("-0.019999999999999997", 9));
            Assert.Equal("1.2345679", Fit("1.23456789012345", 9));
            Assert.Equal("1.235E+14", Fit("123456789012345", 9));
        }

        [Fact]
        public void TooNarrowForOneDigitStillGivesTheShortestForm()
        {
            Assert.Equal("-3E-05", Fit("-2.56527281012E-05", 2));
        }

        [Fact]
        public void OtherTextIsNotShortened()
        {
            Assert.Equal(@"C:\textures\wood.png", Fit(@"C:\textures\wood.png", 5));
            Assert.Equal("1,234567890123", Fit("1,234567890123", 5));
            Assert.Equal("-", Fit("-", 0));
            Assert.Equal("Infinity", Fit("Infinity", 3));
        }

        [Fact]
        public void BoxKeepsTheExactValue()
        {
            const double Value = 2.5652728101234567e-5;
            using (Form form = new Form())
            using (TextBox box = new TextBox { Width = 66 })
            {
                form.Controls.Add(box);
                NumberBoxDisplay.Attach(box);
                Mass mass = new Mass { Value = Value };
                mass.FillBoxes(box, "R");
                string text = Value.ToString("R", CultureInfo.InvariantCulture);
                Assert.Equal(text, box.Text);
                string shown = NumberBoxDisplay.ShownText(box);
                Assert.NotEqual(text, shown);
                Assert.EndsWith("E-05", shown);
                Assert.Equal(text, UiLayout.FullValueTip(box));
                Assert.Equal(text, box.Text);
                mass.Update(box);
                Assert.Equal(Value, mass.Value);
                box.Text = "0.5";
                Assert.Equal("0.5", NumberBoxDisplay.ShownText(box));
                Assert.Null(UiLayout.FullValueTip(box));
            }
        }
    }
}
