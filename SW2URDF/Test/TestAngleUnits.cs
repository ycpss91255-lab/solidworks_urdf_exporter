using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
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

        // The exact value the model holds for a box it filled. AngleDisplay only gets the box so far.
        private static void Show(AngleDisplay angles, TextBox box, double exact)
        {
            angles.Show(box);
        }

        private static void ShowRpy(AngleDisplay angles, TextBox[] boxes, Origin origin)
        {
            for (int i = 0; i < 3; i++)
            {
                Show(angles, boxes[3 + i], origin.GetRPY()[i]);
            }
        }

        private static void ShowLimit(AngleDisplay angles, TextBox[] boxes, Limit limit)
        {
            Show(angles, boxes[0], limit.Lower);
            Show(angles, boxes[1], limit.Upper);
            Show(angles, boxes[3], limit.Velocity);
        }

        [Fact]
        public void DegreesComeFromTheExactValueNotFromRoundedBoxText()
        {
            // The forms may fill the boxes with rounded text (G12): -pi/2 is then -1.57079632679, which is
            // -89.9999999997 deg when the text is converted.
            double[] exact = { -Math.PI / 2, Math.PI / 2, Math.PI / 6, 1 };
            Limit limit = new Limit { Lower = exact[0], Upper = exact[1], Effort = 2, Velocity = exact[3] };
            Origin origin = new Origin(true);
            origin.SetRPY(new double[] { exact[0], exact[1], exact[2] });
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            TextBox[] l = Boxes(4);
            limit.FillBoxes(l[0], l[1], l[2], l[3], "G12");
            ShowLimit(angles, l, limit);
            TextBox[] o = Boxes(6);
            origin.FillBoxes(o[0], o[1], o[2], o[3], o[4], o[5], "G12");
            ShowRpy(angles, o, origin);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(new[] { "-90", "90", "57.2957795131" }, new[] { l[0].Text, l[1].Text, l[3].Text });
                Assert.Equal(new[] { "-90", "90", "30" }, new[] { o[3].Text, o[4].Text, o[5].Text });

                // In radians the boxes show the text the model put there.
                angles.SetUnit(AngleUnit.Radians);
                Assert.Equal("-1.57079632679", l[0].Text);
                Assert.Equal("0.523598775598", o[5].Text);
                angles.SetUnit(AngleUnit.Degrees);

                // Saved without edits: the exact values, and the same degrees afterwards.
                angles.Restore();
                limit.SetValues(l[0], l[1], l[2], l[3]);
                origin.Update(o[0], o[1], o[2], o[3], o[4], o[5]);
                angles.ShowAgain();
                Assert.Equal(exact, new[] { limit.Lower, limit.Upper, origin.GetRPY()[2], limit.Velocity });
                Assert.Equal(new[] { exact[0], exact[1], exact[2] }, origin.GetRPY());
            }
        }

        [Fact]
        public void TypedValuesStayAsTyped()
        {
            Limit limit = new Limit { Lower = -Math.PI / 2, Upper = Math.PI / 6, Effort = 2, Velocity = 1 };
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            TextBox[] l = Boxes(4);
            limit.FillBoxes(l[0], l[1], l[2], l[3], "G12");
            ShowLimit(angles, l, limit);
            l[0].Text = "-45.50";
            l[1].Text = "12.3456789012345";
            for (int i = 0; i < 3; i++)
            {
                angles.Restore();
                limit.SetValues(l[0], l[1], l[2], l[3]);
                angles.ShowAgain();
                angles.SetUnit(AngleUnit.Radians);
                angles.SetUnit(AngleUnit.Degrees);
                Assert.Equal("-45.50", l[0].Text);
                Assert.Equal("12.3456789012345", l[1].Text);
                Assert.Equal(-45.5 * Math.PI / 180.0, limit.Lower);
                Assert.Equal(12.3456789012345 * Math.PI / 180.0, limit.Upper);
                Assert.Equal(1.0, limit.Velocity);
            }

            // The original degrees typed again are the typed value.
            l[0].Text = "-90";
            angles.Restore();
            limit.SetValues(l[0], l[1], l[2], l[3]);
            angles.ShowAgain();
            Assert.Equal(-Math.PI / 2, limit.Lower);

            // Typed in radians: the text stays in radians, degrees show its value, and it is stored exactly.
            angles.SetUnit(AngleUnit.Radians);
            l[1].Text = "0.10";
            angles.SetUnit(AngleUnit.Degrees);
            Assert.Equal("5.72957795131", l[1].Text);
            angles.SetUnit(AngleUnit.Radians);
            Assert.Equal("0.10", l[1].Text);
            angles.SetUnit(AngleUnit.Degrees);
            angles.Restore();
            limit.SetValues(l[0], l[1], l[2], l[3]);
            Assert.Equal(0.1, limit.Upper);
        }

        [Fact]
        public void TypedDegreesGiveTheSameDoubleWhenTheModelReadsThem()
        {
            // The radian text handed to the model must parse back to the converted value. The "R" text of
            // these values does not, on .NET Framework.
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            TextBox box = new TextBox { Text = "0" };
            Show(angles, box, 0);
            foreach (string typed in new[] { "1.2345", "2.469", "4.938", "9.876", "19.752", "45", "-90" })
            {
                box.Text = typed;
                angles.Restore();
                double degrees = double.Parse(typed, System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(degrees * Math.PI / 180.0, double.Parse(box.Text, System.Globalization.CultureInfo.InvariantCulture));
                angles.ShowAgain();
            }
        }

        [Fact]
        public void RestoringTwiceChangesNothing()
        {
            TextBox box = new TextBox { Text = "-1.57079632679" };
            AngleDisplay angles = new AngleDisplay(AngleUnit.Degrees);
            Show(angles, box, -Math.PI / 2);
            angles.Restore();
            angles.Restore();
            Assert.Equal("-1.57079632679", box.Text);
            angles.ShowAgain();
            Assert.Equal("-90", box.Text);
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] JointAngles =
        {
            "textBoxJointRoll", "textBoxJointPitch", "textBoxJointYaw", "textBoxLimitLower", "textBoxLimitUpper",
            "textBoxLimitVelocity", "textBoxSoftLower", "textBoxSoftUpper"
        };

        private static readonly string[] LinkAngles =
        {
            "textBoxVisualOriginRoll", "textBoxVisualOriginPitch", "textBoxVisualOriginYaw",
            "textBoxInertialOriginRoll", "textBoxInertialOriginPitch", "textBoxInertialOriginYaw"
        };

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, Members).SetValue(target, value);
        }

        private static Control[] FormBoxes(AssemblyExportForm form, string[] names)
        {
            return names.Select(name => (Control)typeof(AssemblyExportForm).GetField(name, Members).GetValue(form)).ToArray();
        }

        private static string[] Texts(AssemblyExportForm form, string[] names)
        {
            return FormBoxes(form, names).Select(box => box.Text).ToArray();
        }

        // The assembly form without its constructor, which needs SolidWorks. It is never shown.
        private static AssemblyExportForm NewForm(Link baseLink)
        {
            AssemblyExportForm form = (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            SetField(form, "ui", new UiText("english"));
            typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
            ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
            SetField(exporter, "ReferenceCoordinateSystemNames", new List<string>());
            SetField(exporter, "ReferenceAxesNames", new List<string>());
            Robot robot = new Robot();
            robot.SetBaseLink(baseLink);
            typeof(ExportHelper).GetProperty("URDFRobot", Members).SetValue(exporter, robot, null);
            form.Exporter = exporter;
            SetField(form, "jointBoxes", FormBoxes(form, JointAngles));
            SetField(form, "linkBoxes", FormBoxes(form, LinkAngles));
            return form;
        }

        private static Link NewLink(Link parent, string name, double[] jointRpy, double lower, double upper)
        {
            Link link = parent == null ? new Link() : new Link(parent);
            link.Name = name;
            link.Joint.Name = name + "_joint";
            link.Joint.Type = "revolute";
            link.Joint.Origin.SetRPY(jointRpy);
            link.Joint.Limit.Lower = lower;
            link.Joint.Limit.Upper = upper;
            link.Joint.Limit.Effort = 2;
            link.Joint.Limit.Velocity = 1;
            link.Joint.Safety.SoftLower = lower;
            link.Joint.Safety.SoftUpper = upper;
            link.Joint.Safety.KPosition = 3;
            link.Joint.Safety.KVelocity = 4;
            link.Visual.Origin.SetRPY(new[] { -Math.PI / 2, Math.PI / 2, Math.PI / 6 });
            link.Inertial.Origin.SetRPY(new[] { Math.PI / 2, 1, -Math.PI / 2 });
            return link;
        }

        private static double[] JointValues(Joint joint)
        {
            double[] rpy = joint.Origin.GetRPY();
            return new[] { rpy[0], rpy[1], rpy[2], joint.Limit.Lower, joint.Limit.Upper, joint.Limit.Velocity,
                joint.Safety.SoftLower, joint.Safety.SoftUpper };
        }

        [Fact]
        public void EveryAngleBoxOfTheFormShowsAndKeepsItsExactValue()
        {
            // Through the real fill and save code, with whatever number format the form fills its boxes with.
            Link root = NewLink(null, "base", new double[] { 0, 0, 0 }, 0, 1);
            Link first = NewLink(root, "first", new[] { -Math.PI / 2, Math.PI / 2, 1 }, -Math.PI / 2, Math.PI / 6);
            Link second = NewLink(root, "second", new[] { Math.PI / 6, 0, Math.PI / 2 }, -Math.PI / 6, Math.PI / 2);
            string[] firstDegrees = { "-90", "90", "57.2957795131", "-90", "30", "57.2957795131", "-90", "30" };
            string[] secondDegrees = { "30", "0", "90", "-30", "90", "57.2957795131", "-30", "90" };
            string[] linkDegrees = { "-90", "90", "30", "90", "57.2957795131", "-90" };
            double[] firstValues = JointValues(first.Joint);
            double[] secondValues = JointValues(second.Joint);
            using (AssemblyExportForm form = NewForm(root))
            {
                MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
                Action<AngleUnit> use = unit => setUnit.Invoke(form, new object[] { unit });
                use(AngleUnit.Degrees);
                for (int i = 0; i < 2; i++)
                {
                    form.FillJointPropertyBoxes(first.Joint);
                    Assert.Equal(firstDegrees, Texts(form, JointAngles));
                    use(AngleUnit.Radians);
                    use(AngleUnit.Degrees);
                    Assert.Equal(firstDegrees, Texts(form, JointAngles));
                    form.FillLinkPropertyBoxes(first);
                    Assert.Equal(linkDegrees, Texts(form, LinkAngles));
                    form.SaveJointDataFromPropertyBoxes(first.Joint);
                    Assert.Equal(firstValues, JointValues(first.Joint));
                    Assert.Equal(firstDegrees, Texts(form, JointAngles));

                    // Another joint, saved in radians, and back.
                    form.FillJointPropertyBoxes(second.Joint);
                    Assert.Equal(secondDegrees, Texts(form, JointAngles));
                    use(AngleUnit.Radians);
                    form.SaveJointDataFromPropertyBoxes(second.Joint);
                    use(AngleUnit.Degrees);
                    Assert.Equal(secondDegrees, Texts(form, JointAngles));
                    form.SaveJointDataFromPropertyBoxes(second.Joint);
                    Assert.Equal(secondValues, JointValues(second.Joint));
                    form.SaveLinkDataFromPropertyBoxes(first);
                    Assert.Equal(new[] { -Math.PI / 2, Math.PI / 2, Math.PI / 6 }, first.Visual.Origin.GetRPY());
                    Assert.Equal(new[] { Math.PI / 2, 1, -Math.PI / 2 }, first.Inertial.Origin.GetRPY());
                    Assert.Equal(linkDegrees, Texts(form, LinkAngles));
                }
            }
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
            ShowRpy(angles, b, origin);
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
            ShowRpy(angles, b, origin);
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
            ShowRpy(angles, b, origin);
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
            ShowLimit(angles, b, limit);
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
            Show(angles, empty, double.NaN);
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
            Show(angles, box, 0.5);
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
