using SW2URDF.UI;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Runs the 3DXML to OBJ conversion on a worker thread. When a window is open, a small modal
    /// dialog with a progress bar and a Cancel button keeps the UI thread free to pump messages;
    /// otherwise (tests, command-line tools) the caller simply waits for the worker.
    /// </summary>
    internal static class ObjConversionRunner
    {
        public static void Run(string source, string target, string linkName, CancellationToken externalToken, UiText ui = null)
        {
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(externalToken))
            {
                IProgress<double> progress = null;
                ProgressDialog dialog = null;
                if (Application.OpenForms.Count > 0)
                {
                    dialog = new ProgressDialog(cancel, ui ?? new UiText(null));
                    progress = new Progress<double>(dialog.SetFraction);   // captures the UI context
                }
                Task<ObjConversionResult> task = ObjConverter.ConvertAsync(source, target, new ObjConversionOptions
                {
                    LinkName = linkName,
                    Progress = progress,
                    CancellationToken = cancel.Token
                });
                if (dialog != null)
                {
                    task.ContinueWith(t => dialog.CloseFromWorker(), TaskScheduler.Default);
                    using (dialog) dialog.ShowDialog(Form.ActiveForm);
                }
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException("OBJ conversion was cancelled; existing output was kept.");
                }
            }
        }

        private sealed class ProgressDialog : Form
        {
            private readonly Label label = new Label();
            private readonly ProgressBar bar = new ProgressBar { Maximum = 1000 };
            private readonly Button button = new Button { TabIndex = 0 };
            private readonly CancellationTokenSource cancel;
            private float layoutDpi = UiLayout.ScreenDpi();
            private volatile bool finished;

            public ProgressDialog(CancellationTokenSource cancel, UiText ui)
            {
                this.cancel = cancel;
                Text = ui.Get("Converting mesh to OBJ");
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                ControlBox = false;
                MinimizeBox = MaximizeBox = false;
                ShowInTaskbar = false;
                button.Text = ui.Get("Cancel");
                button.Click += (s, e) => { button.Enabled = false; cancel.Cancel(); };
                label.Text = ui.Get("Converting 3DXML to OBJ + MTL...");
                Controls.Add(button);
                Controls.Add(bar);
                Controls.Add(label);
                CancelButton = button;
                // Only the end of the conversion closes the window
                button.DialogResult = DialogResult.None;
                FitToFont();
            }

            // Every size follows the font, which follows the display scaling.
            private void FitToFont()
            {
                int line = Font.Height;
                int gap = line * 2 / 3;
                Size text = TextRenderer.MeasureText(label.Text, label.Font);
                int width = Math.Max(24 * line, Math.Max(text.Width, label.PreferredWidth));
                label.SetBounds(gap, gap, width, Math.Max(text.Height, label.PreferredHeight));
                bar.SetBounds(gap, label.Bottom + gap, width, line * 3 / 2);
                int buttonWidth = Math.Max(6 * line, TextRenderer.MeasureText(button.Text, button.Font).Width + 2 * line);
                button.SetBounds(bar.Right - buttonWidth, bar.Bottom + gap, buttonWidth, 2 * line);
                ClientSize = new Size(bar.Right + gap, button.Bottom + gap);
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                FitToFont();
            }

            // The font is made for the main display; the window may be on one with another scaling.
            private void FollowWindowDpi()
            {
                float dpi = UiLayout.WindowDpi(this);
                if (Math.Abs(dpi - layoutDpi) < 1f)
                {
                    return;
                }
                Font = new Font(Font.FontFamily, Font.SizeInPoints * dpi / layoutDpi, Font.Style);
                layoutDpi = dpi;
            }

            protected override void OnLoad(EventArgs e)
            {
                FollowWindowDpi();
                base.OnLoad(e);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == UiLayout.WM_DPICHANGED)
                {
                    FollowWindowDpi();
                }
            }

            public void SetFraction(double fraction)
            {
                if (InvokeRequired) { BeginInvoke(new Action<double>(SetFraction), fraction); return; }
                bar.Value = Math.Max(0, Math.Min(bar.Maximum, (int)(fraction * bar.Maximum)));
            }

            public void CloseFromWorker()
            {
                // The task may complete before the dialog handle exists.
                finished = true;
                if (IsHandleCreated) BeginInvoke(new Action(Close));
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                if (finished) Close();
            }
        }
    }
}
