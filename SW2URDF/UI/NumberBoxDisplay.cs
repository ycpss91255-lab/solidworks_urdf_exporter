using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SW2URDF.URDF;

namespace SW2URDF.UI
{
    /// <summary>
    /// Draws a number that is wider than its text box with as many digits as fit, so its exponent is
    /// always on display (2.56527281012E-05 is drawn as 2.565E-05, not cut to 2.5652728). Only the
    /// drawing changes: the text of the box stays the whole value, so what is read back, copied and
    /// edited is exact. While the box has the focus it shows its own text as usual.
    /// </summary>
    internal sealed class NumberBoxDisplay : NativeWindow
    {
        private const int WM_SETFOCUS = 0x0007;
        private const int WM_KILLFOCUS = 0x0008;
        private const int WM_PAINT = 0x000F;
        private const int WM_GETFONT = 0x0031;
        private const int WM_PRINTCLIENT = 0x0318;
        private const int EM_GETRECT = 0x00B2;
        private const int TRANSPARENT = 1;

        private readonly TextBox box;

        private NumberBoxDisplay(TextBox box)
        {
            this.box = box;
            box.HandleCreated += delegate { AssignHandle(this.box.Handle); };
            box.HandleDestroyed += delegate { ReleaseHandle(); };
            // The text box repaints only the characters that changed.
            box.TextChanged += delegate { this.box.Invalidate(); };
            if (box.IsHandleCreated)
            {
                AssignHandle(box.Handle);
            }
        }

        /// <summary>Shows the numbers in box shortened from now on. Call once per box.</summary>
        internal static void Attach(TextBox box)
        {
            new NumberBoxDisplay(box);
        }

        /// <summary>What box shows while it does not have the focus.</summary>
        internal static string ShownText(TextBox box)
        {
            return Fit(box.Text, text => Fits(box, text));
        }

        /// <summary>Whether box shows the whole of text.</summary>
        internal static bool Fits(TextBox box, string text)
        {
            return string.IsNullOrEmpty(text) || TextWidth(box, text) <= TextArea(box).Width;
        }

        /// <summary>
        /// The number in text with as many significant digits as fit. Text that fits, and text that is
        /// not a number, stays as it is.
        /// </summary>
        internal static string Fit(string text, Func<string, bool> fits)
        {
            double value;
            if (string.IsNullOrEmpty(text) || fits(text) || !URDFAttribute.TryParseDouble(text, out value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                return text;
            }
            string shown = text;
            for (int digits = 15; digits > 0; digits--)
            {
                shown = value.ToString("G" + digits.ToString(CultureInfo.InvariantCulture), URDFAttribute.URDFNumberFormat);
                if (fits(shown))
                {
                    break;
                }
            }
            return shown;
        }

        // Where a text box draws its text: the client area without the text margins.
        private static Rectangle TextArea(TextBox box)
        {
            Rectangle client = box.ClientRectangle;
            if (!box.IsHandleCreated)
            {
                return client;
            }
            RECT area = new RECT();
            SendMessage(box.Handle, EM_GETRECT, IntPtr.Zero, ref area);
            return Rectangle.Intersect(client, Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Bottom));
        }

        // Measured with the font the box draws with. TextRenderer makes a font of its own for the same
        // Font, which can have slightly different character widths.
        private static int TextWidth(TextBox box, string text)
        {
            IntPtr boxFont = BoxFont(box);
            IntPtr ownFont = boxFont == IntPtr.Zero ? box.Font.ToHfont() : IntPtr.Zero;
            IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                SIZE size;
                IntPtr old = SelectObject(hdc, boxFont == IntPtr.Zero ? ownFont : boxFont);
                GetTextExtentPoint32(hdc, text, text.Length, out size);
                SelectObject(hdc, old);
                return size.Width;
            }
            finally
            {
                DeleteDC(hdc);
                if (ownFont != IntPtr.Zero)
                {
                    DeleteObject(ownFont);
                }
            }
        }

        private static IntPtr BoxFont(TextBox box)
        {
            return box.IsHandleCreated ? SendMessage(box.Handle, WM_GETFONT, IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_PRINTCLIENT)
            {
                string shown = box.Focused ? box.Text : ShownText(box);
                if (shown != box.Text)
                {
                    Paint(ref m, shown);
                    return;
                }
            }
            base.WndProc(ref m);
            if (m.Msg == WM_SETFOCUS)
            {
                ShowEnd();
                box.Invalidate();
            }
            else if (m.Msg == WM_KILLFOCUS)
            {
                box.Invalidate();
            }
        }

        // A value too long for the box starts being edited at its end, where the exponent is.
        private void ShowEnd()
        {
            if (ShownText(box) == box.Text)
            {
                return;
            }
            if (box.SelectionLength == 0)
            {
                box.SelectionStart = box.TextLength;
            }
            box.ScrollToCaret();
        }

        private void Paint(ref Message m, string shown)
        {
            if (m.WParam != IntPtr.Zero)
            {
                Paint(m.WParam, shown);
            }
            else
            {
                PAINTSTRUCT paint;
                IntPtr hdc = BeginPaint(Handle, out paint);
                try
                {
                    Paint(hdc, shown);
                }
                finally
                {
                    EndPaint(Handle, ref paint);
                }
            }
            m.Result = IntPtr.Zero;
        }

        private void Paint(IntPtr hdc, string shown)
        {
            if (hdc == IntPtr.Zero)
            {
                return;
            }
            System.Drawing.Color back = box.Enabled ? box.BackColor : SystemColors.Control;
            System.Drawing.Color fore = box.Enabled ? box.ForeColor : SystemColors.GrayText;
            using (Graphics g = Graphics.FromHdc(hdc))
            using (Brush brush = new SolidBrush(back))
            {
                g.FillRectangle(brush, box.ClientRectangle);
            }
            Point location = TextArea(box).Location;
            IntPtr old = SelectObject(hdc, BoxFont(box));
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, ColorTranslator.ToWin32(fore));
            TextOut(hdc, location.X, location.Y, shown, shown.Length);
            SelectObject(hdc, old);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr Hdc;
            public int Erase;
            public RECT Paint;
            public int Restore;
            public int IncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Reserved;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref RECT lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool TextOut(IntPtr hdc, int x, int y, string text, int length);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextExtentPoint32(IntPtr hdc, string text, int length, out SIZE size);
    }
}
