using System;
using System.Windows.Forms;
using Microsoft.Win32;
using SW2URDF.UI;
using SW2URDF.URDF;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The joint and link pages can show angles in degrees (issue #155).
    /// Only the boxes change: the model is filled and read in radians, an unedited value keeps its
    /// exact radian value through any number of unit switches and saves, and a value typed in
    /// degrees is stored as radians.
    /// </summary>
    public class TestAngleUnits
    {
        private const double Thirty = 0.523598775598299;
        private const double Roll = -1.5707963267949;
        private const double Odd = 0.0123456789012345;

        private static TextBox[] Boxes(int count)
        {
            TextBox[] boxes = new TextBox[count];
            for (int i = 0; i < count; i++)
            {
                boxes[i] = new TextBox();
            }
            return boxes;
        }

        private static Origin NewOrigin()
        {
            Origin origin = new Origin(true);
            origin.SetXYZ(new double[] { 0.1, 0.2, 0.3 });
            origin.SetRPY(new double[] { Roll, Thirty, Odd });
            return origin;
        }

        [Fact]
        public void DegreesAreShownRoundedAndRadiansInFull()
        {
            Assert.Equal("30", AngleDisplay.Format(Thirty, AngleUnit.Degrees));
            Assert.Equal("-90", AngleDisplay.Format(Roll, AngleUnit.Degrees));
            Assert.Equal("0.523598775598299", AngleDisplay.Format(Thirty, AngleUnit.Radians));
            Assert.Equal(Math.PI / 4, AngleDisplay.ToRadians(45, AngleUnit.Degrees));
            Assert.Equal(0.5, AngleDisplay.ToRadians(0.5, AngleUnit.Radians));
        }

        [Fact]
        public void UneditedAnglesKeepTheirExactRadians()
        {
            Origin origin = NewOrigin();
            TextBox[] b = Boxes(6);
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            origin.FillBoxes(b[0], b[1], b[2], b[3], b[4], b[5], "R");
            angles.Show(b[3], b[4], b[5]);
            Assert.Equal("-90", b[3].Text);
            Assert.Equal("30", b[4].Text);
            Assert.Equal("0.1", b[0].Text);

            for (int i = 0; i < 3; i++)
            {
                angles.Restore();
                origin.Update(b[0], b[1], b[2], b[3], b[4], b[5]);
                angles.ShowAgain();
            }
            Assert.Equal(new double[] { Roll, Thirty, Odd }, origin.GetRPY());
            Assert.Equal(new double[] { 0.1, 0.2, 0.3 }, origin.GetXYZ());
            Assert.Equal("30", b[4].Text);
        }

        [Fact]
        public void SwitchingUnitsBackAndForthChangesNothing()
        {
            Origin origin = NewOrigin();
            TextBox[] b = Boxes(6);
            AngleDisplay angles = new AngleDisplay(AngleUnit.Radians);
            origin.FillBoxes(b[0], b[1], b[2], b[3], b[4], b[5], "R");
            angles.Show(b[3], b[4], b[5]);
            Assert.Equal("0.523598775598299", b[4].Text);
            for (int i = 0; i < 5; i++)
            {
                angles.SetUnit(AngleUnit.Degrees);
                Assert.Equal("30", b[4].Text);
                angles.SetUnit(AngleUnit.Radians);
                Assert.Equal("0.523598775598299", b[4].Text);
            }
            angles.SetUnit(AngleUnit.Degrees);
            angles.Restore();
            origin.Update(b[0], b[1], b[2], b[3], b[4], b[5]);
            Assert.Equal(new double[] { Roll, Thirty, Odd }, origin.GetRPY());
        }

        [Fact]
        public void TypedDegreesAreStoredAsRadians()
        {
            Origin origin = NewOrigin();
            TextBox[] b = Boxes(6);
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            origin.FillBoxes(b[0], b[1], b[2], b[3], b[4], b[5], "R");
            angles.Show(b[3], b[4], b[5]);
            b[4].Text = "45";
            b[0].Text = "0.25";
            angles.Restore();
            origin.Update(b[0], b[1], b[2], b[3], b[4], b[5]);
            Assert.Equal(new double[] { Roll, 45 * Math.PI / 180, Odd }, origin.GetRPY());
            Assert.Equal(0.25, origin.GetXYZ()[0]);

            // Typed in degrees, then switched to radians before saving: still 45 degrees.
            angles.ShowAgain();
            b[5].Text = "-12.5";
            angles.SetUnit(AngleUnit.Radians);
            Assert.Equal((-12.5 * Math.PI / 180).ToString("R", System.Globalization.CultureInfo.InvariantCulture), b[5].Text);
            angles.Restore();
            origin.Update(b[0], b[1], b[2], b[3], b[4], b[5]);
            Assert.Equal(-12.5 * Math.PI / 180, origin.GetRPY()[2]);
        }

        [Fact]
        public void LimitsInDegreesRoundTripAndEmptyBoxesStayEmpty()
        {
            Limit limit = new Limit { Lower = -Thirty, Upper = Odd, Effort = 12.5, Velocity = 3.14159265358979 };
            TextBox[] b = Boxes(4);
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            limit.FillBoxes(b[0], b[1], b[2], b[3], "R");
            angles.Show(b[0], b[1], b[3]);
            Assert.Equal("-30", b[0].Text);
            Assert.Equal("12.5", b[2].Text);
            Assert.Equal("180", b[3].Text);
            angles.Restore();
            limit.SetValues(b[0], b[1], b[2], b[3]);
            Assert.Equal(-Thirty, limit.Lower);
            Assert.Equal(Odd, limit.Upper);
            Assert.Equal(3.14159265358979, limit.Velocity);
            Assert.Equal(12.5, limit.Effort);

            TextBox empty = new TextBox();
            angles.Show(empty);
            angles.Restore();
            Assert.Equal("", empty.Text);
            empty.Text = "90";
            angles.Restore();
            Assert.Equal(Math.PI / 2, double.Parse(empty.Text, System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void ForgottenBoxesAreLeftAlone()
        {
            TextBox box = new TextBox { Text = "0.5" };
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            angles.Show(box);
            angles.Forget(new Control[] { box });
            box.Text = "2";
            angles.Restore();
            Assert.Equal("2", box.Text);
        }

        [Fact]
        public void TheUnitIsRememberedPerUserAndDefaultsToRadians()
        {
            string path = @"Software\SW2URDF.Tests\" + Guid.NewGuid().ToString("N");
            try
            {
                AngleUnitSetting setting = new AngleUnitSetting(path);
                Assert.Equal(AngleUnit.Radians, setting.Load());
                setting.Save(AngleUnit.Degrees);
                Assert.Equal(AngleUnit.Degrees, new AngleUnitSetting(path).Load());
                setting.Save(AngleUnit.Radians);
                Assert.Equal(AngleUnit.Radians, new AngleUnitSetting(path).Load());
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\SW2URDF.Tests", false);
            }
        }
    }
}
