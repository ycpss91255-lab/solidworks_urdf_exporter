using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The part form has boxes for the visual and for the collision origin
    /// (issue #176). Each group shows and saves its own origin, a box that is not edited keeps the exact
    /// value whatever number format the boxes use, and an edited one is taken as typed. Every value is
    /// compared exactly.
    /// </summary>
    public class TestPartOriginBoxes
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] Ends = { "x", "y", "z", "roll", "pitch", "yaw" };

        // Values that a text with 12 digits does not give back.
        private static readonly double[] VisualValues =
            { -0.019999999999999997, 1.2345678901234567, 0.00249115384615384, -Math.PI / 2, Math.PI / 6, 0.012345678901234567 };

        private static readonly double[] OtherValues =
            { 0.30000000000000004, -7.6543210987654321, 1.0 / 3.0, Math.PI / 2, -Math.PI / 3, 1.2345678901234567e-17 };

        // Number format of the boxes: the form's own (null), and the full and a rounded one.
        private static readonly string[] Formats = { null, "R", "G12" };

        private static TextBox Box(PartExportForm form, string group, int index)
        {
            return (TextBox)typeof(PartExportForm).GetField("textBox_" + group + "_origin_" + Ends[index], Members).GetValue(form);
        }

        private static string[] Texts(PartExportForm form, string group)
        {
            return Enumerable.Range(0, Ends.Length).Select(i => Box(form, group, i).Text).ToArray();
        }

        // What an origin shows in the form's own number format.
        private static string[] TextsOf(Origin origin, string format)
        {
            TextBox[] b = Ends.Select(end => new TextBox()).ToArray();
            origin.FillBoxes(b[0], b[1], b[2], b[3], b[4], b[5], format);
            return b.Select(box => box.Text).ToArray();
        }

        // The part form without its constructor, which needs SolidWorks. It is never shown.
        private static PartExportForm NewForm(Link link)
        {
            PartExportForm form = (PartExportForm)FormatterServices.GetUninitializedObject(typeof(PartExportForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            typeof(PartExportForm).GetField("ui", Members).SetValue(form, new UiText("english"));
            typeof(PartExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
            ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
            Robot robot = new Robot();
            robot.SetBaseLink(link);
            typeof(ExportHelper).GetProperty("URDFRobot", Members).SetValue(exporter, robot, null);
            form.Exporter = exporter;
            return form;
        }

        private static Link NewLink(double[] visual, double[] collision)
        {
            Link link = new Link { Name = "link" };
            Set(link.Visual.Origin, visual);
            Set(link.Collision.Origin, collision);
            return link;
        }

        private static void Set(Origin origin, double[] values)
        {
            origin.SetXYZ(values.Take(3).ToArray());
            origin.SetRPY(values.Skip(3).ToArray());
        }

        private static double[] Values(Origin origin)
        {
            return origin.GetXYZ().Concat(origin.GetRPY()).ToArray();
        }

        private static double[] With(double[] values, int index, double value)
        {
            double[] result = (double[])values.Clone();
            result[index] = value;
            return result;
        }

        // Fills the form; with a format, the origin boxes show the values in that format.
        private static void Fill(PartExportForm form, Link link, string format)
        {
            form.FillPropertyBoxes();
            if (format != null)
            {
                Refill(form, "visual", link.Visual.Origin, format);
                Refill(form, "collision", link.Collision.Origin, format);
            }
        }

        private static void Refill(PartExportForm form, string group, Origin origin, string format)
        {
            origin.FillBoxes(Box(form, group, 0), Box(form, group, 1), Box(form, group, 2),
                Box(form, group, 3), Box(form, group, 4), Box(form, group, 5), format);
        }

        [Fact]
        public void EachGroupShowsItsOwnOrigin()
        {
            Link link = NewLink(VisualValues, OtherValues);
            using (PartExportForm form = NewForm(link))
            {
                form.FillPropertyBoxes();
                // The visual boxes tell which format the form uses.
                string format = Formats.Skip(1).First(f => TextsOf(link.Visual.Origin, f).SequenceEqual(Texts(form, "visual")));
                Assert.Equal(TextsOf(link.Collision.Origin, format), Texts(form, "collision"));
            }
        }

        [Fact]
        public void UneditedSaveKeepsBothOriginsExactly()
        {
            foreach (string format in Formats)
            {
                foreach (double[] collision in new[] { VisualValues, OtherValues })
                {
                    Link link = NewLink(VisualValues, collision);
                    using (PartExportForm form = NewForm(link))
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            Fill(form, link, format);
                            form.SavePropertyBoxes();
                            form.SavePropertyBoxes();
                        }
                    }
                    Assert.Equal(VisualValues, Values(link.Visual.Origin));
                    Assert.Equal(collision, Values(link.Collision.Origin));
                }
            }
        }

        [Fact]
        public void EditedCollisionValueIsTakenAsTypedAndChangesNothingElse()
        {
            foreach (string format in Formats)
            {
                for (int i = 0; i < Ends.Length; i++)
                {
                    Link link = NewLink(VisualValues, OtherValues);
                    using (PartExportForm form = NewForm(link))
                    {
                        Fill(form, link, format);
                        Box(form, "collision", i).Text = "0.123456789012345";
                        form.SavePropertyBoxes();
                    }
                    Assert.Equal(VisualValues, Values(link.Visual.Origin));
                    Assert.Equal(With(OtherValues, i, 0.123456789012345), Values(link.Collision.Origin));
                }
            }
        }

        [Fact]
        public void EditedVisualValueLeavesTheCollisionOrigin()
        {
            foreach (string format in Formats)
            {
                for (int i = 0; i < Ends.Length; i++)
                {
                    Link link = NewLink(VisualValues, OtherValues);
                    using (PartExportForm form = NewForm(link))
                    {
                        Fill(form, link, format);
                        Box(form, "visual", i).Text = "-2.5";
                        form.SavePropertyBoxes();
                    }
                    Assert.Equal(With(VisualValues, i, -2.5), Values(link.Visual.Origin));
                    Assert.Equal(OtherValues, Values(link.Collision.Origin));
                }
            }
        }

        [Fact]
        public void ClearedCollisionBoxIsZero()
        {
            Link link = NewLink(VisualValues, OtherValues);
            using (PartExportForm form = NewForm(link))
            {
                form.FillPropertyBoxes();
                Box(form, "collision", 5).Text = "";
                form.SavePropertyBoxes();
            }
            Assert.Equal(VisualValues, Values(link.Visual.Origin));
            Assert.Equal(With(OtherValues, 5, 0), Values(link.Collision.Origin));
        }

        [Fact]
        public void SavedCollisionOriginSurvivesReopeningTheForm()
        {
            // As the form does it: the link is built from the part, the saved settings replace its values.
            Link saved = NewLink(VisualValues, OtherValues);
            for (int i = 0; i < 2; i++)
            {
                Link built = NewLink(new double[6], new double[6]);
                ExportHelper.ApplySavedSettings(built, saved);
                using (PartExportForm form = NewForm(built))
                {
                    form.FillPropertyBoxes();
                    form.SavePropertyBoxes();
                }
                saved = built;
            }
            Assert.Equal(VisualValues, Values(saved.Visual.Origin));
            Assert.Equal(OtherValues, Values(saved.Collision.Origin));
        }
    }
}
