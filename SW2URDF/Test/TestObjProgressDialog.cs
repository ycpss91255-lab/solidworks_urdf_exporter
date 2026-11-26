using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SW2URDF.UI;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// The small window with the progress of an OBJ conversion (issue 131). It is never shown here:
    /// its controls are checked for the fonts and display scalings it may be drawn with.
    /// </summary>
    public class TestObjProgressDialog
    {
        private static readonly string[] FontNames = { "Microsoft Sans Serif", "Segoe UI", "Microsoft JhengHei UI" };
        private static readonly float[] Scales = { 1f, 1.25f, 1.5f, 1.75f, 2f };

        private static Form Create(CancellationTokenSource cancel, string language)
        {
            Type type = typeof(ObjConversionRunner).GetNestedType("ProgressDialog", BindingFlags.NonPublic);
            return (Form)Activator.CreateInstance(type, cancel, new UiText(language));
        }

        private static void ForEachDrawing(Action<Form, string, float> check)
        {
            foreach (string language in new[] { "english", "chinese" })
            {
                foreach (string fontName in FontNames)
                {
                    foreach (float scale in Scales)
                    {
                        using (CancellationTokenSource cancel = new CancellationTokenSource())
                        using (Font font = new Font(fontName, 9f * scale))
                        using (Form dialog = Create(cancel, language))
                        {
                            dialog.Font = font;
                            check(dialog, language + ", " + fontName + ", " + scale, scale);
                        }
                    }
                }
            }
        }

        private static T Child<T>(Form dialog) where T : Control
        {
            return dialog.Controls.OfType<T>().Single();
        }

        [Fact]
        public void EveryControlIsInsideTheWindowAndNothingOverlaps()
        {
            ForEachDrawing((dialog, name, scale) =>
            {
                List<Control> controls = dialog.Controls.Cast<Control>().ToList();
                foreach (Control control in controls)
                {
                    Assert.True(dialog.ClientRectangle.Contains(control.Bounds),
                        name + ": " + control.GetType().Name + " " + control.Bounds + " leaves " + dialog.ClientRectangle);
                    foreach (Control other in controls.Where(other => other != control))
                    {
                        Assert.False(control.Bounds.IntersectsWith(other.Bounds),
                            name + ": " + control.GetType().Name + " overlaps " + other.GetType().Name);
                    }
                }
            });
        }

        [Fact]
        public void TheTextsAreNotCut()
        {
            ForEachDrawing((dialog, name, scale) =>
            {
                Label label = Child<Label>(dialog);
                Size text = TextRenderer.MeasureText(label.Text, label.Font);
                Assert.True(label.ClientSize.Width >= text.Width + label.Padding.Horizontal &&
                    label.ClientSize.Height >= text.Height + label.Padding.Vertical,
                    name + ": the text needs " + text + ", its label has " + label.ClientSize);
                Button button = Child<Button>(dialog);
                text = TextRenderer.MeasureText(button.Text, button.Font);
                Assert.True(button.Width >= text.Width + button.Font.Height, name + ": the caption is cut at the sides");
                Assert.True(UiLayout.FitButtonHeights(dialog, 96f * scale) == 0, name + ": the button is too low for its caption");
            });
        }

        [Fact]
        public void TheWindowGrowsWithTheDisplayScaling()
        {
            Dictionary<float, Size> sizes = new Dictionary<float, Size>();
            ForEachDrawing((dialog, name, scale) =>
            {
                if (name.StartsWith("english, Segoe UI"))
                {
                    sizes[scale] = dialog.ClientSize;
                    Assert.True(Child<ProgressBar>(dialog).Width >= 300 * scale, name + ": the bar is short");
                }
            });
            Assert.True(sizes[2f].Width >= 1.8 * sizes[1f].Width, sizes[1f] + " to " + sizes[2f]);
            Assert.True(sizes[2f].Height >= 1.8 * sizes[1f].Height, sizes[1f] + " to " + sizes[2f]);
        }

        [Fact]
        public void EscapeCancelsLikeTheButtonAndDoesNotCloseTheWindow()
        {
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            using (Form dialog = Create(cancel, "english"))
            {
                Button button = Child<Button>(dialog);
                Assert.Same(button, dialog.CancelButton);
                Assert.Equal(0, button.TabIndex);
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(button, new object[] { EventArgs.Empty });
                Assert.True(cancel.IsCancellationRequested);
                Assert.False(button.Enabled);
                Assert.Equal(DialogResult.None, dialog.DialogResult);
            }
        }
    }
}
