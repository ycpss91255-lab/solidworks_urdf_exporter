using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using SW2URDF.UI;
using SW2URDF.URDF;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The link page has one set of origin boxes for the visual and the
    /// collision mesh (issue #175). A value the user edits there applies to both meshes; a value that
    /// is not edited changes neither, also when the two origins differ. Every value is compared exactly.
    /// </summary>
    public class TestMeshOriginBoxes
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] BoxNames =
        {
            "textBoxVisualOriginX", "textBoxVisualOriginY", "textBoxVisualOriginZ",
            "textBoxVisualOriginRoll", "textBoxVisualOriginPitch", "textBoxVisualOriginYaw"
        };

        // Values that a text with 12 digits does not give back.
        private static readonly double[] VisualValues =
            { -0.019999999999999997, 1.2345678901234567, 0.00249115384615384, -Math.PI / 2, Math.PI / 6, 0.012345678901234567 };

        private static readonly double[] OtherValues =
            { 0.30000000000000004, -7.6543210987654321, 1.0 / 3.0, Math.PI / 2, -Math.PI / 3, 1.2345678901234567e-17 };

        // Number format of the boxes: the form's own (null), and the full and a rounded one.
        private static readonly string[] Formats = { null, "R", "G12" };

        private static TextBox Box(AssemblyExportForm form, int index)
        {
            return (TextBox)typeof(AssemblyExportForm).GetField(BoxNames[index], Members).GetValue(form);
        }

        // The assembly form without its constructor, which needs SolidWorks. It is never shown.
        private static AssemblyExportForm NewForm()
        {
            AssemblyExportForm form = (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            typeof(AssemblyExportForm).GetField("ui", Members).SetValue(form, new UiText("english"));
            typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
            typeof(AssemblyExportForm).GetField("linkBoxes", Members).SetValue(form,
                Enumerable.Range(0, BoxNames.Length).Select(i => (Control)Box(form, i)).ToArray());
            // The boxes hold radians here, also in a build where the form can show degrees.
            MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
            if (setUnit != null)
            {
                setUnit.Invoke(form, new[] { Enum.Parse(setUnit.GetParameters()[0].ParameterType, "Radians") });
            }
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

        // Fills the link page; with a format, the origin boxes show the values in that format.
        private static void Fill(AssemblyExportForm form, Link link, string format)
        {
            form.FillLinkPropertyBoxes(link);
            if (format != null)
            {
                link.Visual.Origin.FillBoxes(Box(form, 0), Box(form, 1), Box(form, 2),
                    Box(form, 3), Box(form, 4), Box(form, 5), format);
            }
        }

        [Fact]
        public void EditedValueMovesBothMeshes()
        {
            foreach (string format in Formats)
            {
                for (int i = 0; i < BoxNames.Length; i++)
                {
                    Link link = NewLink(VisualValues, VisualValues);
                    using (AssemblyExportForm form = NewForm())
                    {
                        Fill(form, link, format);
                        Box(form, i).Text = "0.123456789012345";
                        form.SaveLinkDataFromPropertyBoxes(link);
                    }
                    double[] expected = With(VisualValues, i, 0.123456789012345);
                    Assert.Equal(expected, Values(link.Visual.Origin));
                    Assert.Equal(expected, Values(link.Collision.Origin));
                }
            }
        }

        [Fact]
        public void EditedValueLeavesTheOtherValuesOfADifferentCollisionOrigin()
        {
            foreach (string format in Formats)
            {
                for (int i = 0; i < BoxNames.Length; i++)
                {
                    Link link = NewLink(VisualValues, OtherValues);
                    using (AssemblyExportForm form = NewForm())
                    {
                        Fill(form, link, format);
                        Box(form, i).Text = "-2.5";
                        form.SaveLinkDataFromPropertyBoxes(link);
                        form.SaveLinkDataFromPropertyBoxes(link);
                    }
                    Assert.Equal(With(VisualValues, i, -2.5), Values(link.Visual.Origin));
                    Assert.Equal(With(OtherValues, i, -2.5), Values(link.Collision.Origin));
                }
            }
        }

        [Fact]
        public void UneditedSaveChangesNeitherOrigin()
        {
            foreach (string format in Formats)
            {
                foreach (double[] collision in new[] { VisualValues, OtherValues })
                {
                    Link link = NewLink(VisualValues, collision);
                    using (AssemblyExportForm form = NewForm())
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            Fill(form, link, format);
                            form.SaveLinkDataFromPropertyBoxes(link);
                            form.SaveLinkDataFromPropertyBoxes(link);
                        }
                    }
                    Assert.Equal(VisualValues, Values(link.Visual.Origin));
                    Assert.Equal(collision, Values(link.Collision.Origin));
                }
            }
        }

        [Fact]
        public void RoundedTextTypedIntoAnotherBoxIsAnEdit()
        {
            Link link = NewLink(VisualValues, OtherValues);
            using (AssemblyExportForm form = NewForm())
            {
                Fill(form, link, "G12");
                Assert.Equal("-0.02", Box(form, 0).Text);
                Box(form, 1).Text = "-0.02";
                form.SaveLinkDataFromPropertyBoxes(link);
            }
            Assert.Equal(With(VisualValues, 1, -0.02), Values(link.Visual.Origin));
            Assert.Equal(With(OtherValues, 1, -0.02), Values(link.Collision.Origin));
        }

        [Fact]
        public void TheSameNumberTypedDifferentlyAppliesToBoth()
        {
            // The way to bring a different collision value back to the visual one.
            double[] visual = { 0.5, 0, 0, 0, 0, 0 };
            Link link = NewLink(visual, OtherValues);
            using (AssemblyExportForm form = NewForm())
            {
                form.FillLinkPropertyBoxes(link);
                Box(form, 0).Text = "0.50";
                form.SaveLinkDataFromPropertyBoxes(link);
            }
            Assert.Equal(visual, Values(link.Visual.Origin));
            Assert.Equal(With(OtherValues, 0, 0.5), Values(link.Collision.Origin));
        }

        [Fact]
        public void ClearedBoxIsZeroForBothMeshes()
        {
            Link link = NewLink(VisualValues, OtherValues);
            using (AssemblyExportForm form = NewForm())
            {
                form.FillLinkPropertyBoxes(link);
                Box(form, 2).Text = "";
                form.SaveLinkDataFromPropertyBoxes(link);
            }
            Assert.Equal(With(VisualValues, 2, 0), Values(link.Visual.Origin));
            Assert.Equal(With(OtherValues, 2, 0), Values(link.Collision.Origin));
        }

        [Fact]
        public void EditStaysWithItsLink()
        {
            Link first = NewLink(VisualValues, VisualValues);
            Link second = NewLink(OtherValues, VisualValues);
            using (AssemblyExportForm form = NewForm())
            {
                form.FillLinkPropertyBoxes(first);
                Box(form, 0).Text = "4";
                form.SaveLinkDataFromPropertyBoxes(first);
                form.FillLinkPropertyBoxes(second);
                form.SaveLinkDataFromPropertyBoxes(second);
                form.FillLinkPropertyBoxes(first);
                Assert.Equal("4", Box(form, 0).Text);
                form.SaveLinkDataFromPropertyBoxes(first);
            }
            Assert.Equal(With(VisualValues, 0, 4), Values(first.Visual.Origin));
            Assert.Equal(With(VisualValues, 0, 4), Values(first.Collision.Origin));
            Assert.Equal(OtherValues, Values(second.Visual.Origin));
            Assert.Equal(VisualValues, Values(second.Collision.Origin));
        }

        [Fact]
        public void UneditedBoxesLeaveACollisionOriginThatChangedAfterTheFill()
        {
            // An export moves both origins into the mesh frame; the boxes still show the values before.
            Link link = NewLink(VisualValues, VisualValues);
            using (AssemblyExportForm form = NewForm())
            {
                form.FillLinkPropertyBoxes(link);
                Set(link.Visual.Origin, OtherValues);
                Set(link.Collision.Origin, OtherValues);
                form.SaveLinkDataFromPropertyBoxes(link);
            }
            Assert.Equal(OtherValues, Values(link.Collision.Origin));
        }

        [Fact]
        public void FixedFrameLinkIsNotSaved()
        {
            Link link = NewLink(VisualValues, OtherValues);
            link.isFixedFrame = true;
            using (AssemblyExportForm form = NewForm())
            {
                form.FillLinkPropertyBoxes(link);
                Box(form, 0).Text = "4";
                form.SaveLinkDataFromPropertyBoxes(link);
            }
            Assert.Equal(VisualValues, Values(link.Visual.Origin));
            Assert.Equal(OtherValues, Values(link.Collision.Origin));
        }
    }
}
