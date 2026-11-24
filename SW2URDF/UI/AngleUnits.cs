using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SW2URDF.UI
{
    /// <summary>Unit the export forms show angles in. The URDF and the saved data always use radians.</summary>
    public enum AngleUnit
    {
        Radians,
        Degrees
    }

    /// <summary>
    /// The angle unit chosen by the user, remembered per Windows user (not per model) under
    /// HKEY_CURRENT_USER. Radians when nothing is stored or the value cannot be read.
    /// </summary>
    public sealed class AngleUnitSetting
    {
        public const string DefaultKeyPath = @"Software\SW2URDF";
        private const string ValueName = "AngleUnit";
        private readonly string keyPath;

        public AngleUnitSetting() : this(DefaultKeyPath)
        {
        }

        public AngleUnitSetting(string keyPath)
        {
            this.keyPath = keyPath;
        }

        public AngleUnit Load()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    string value = key == null ? null : key.GetValue(ValueName) as string;
                    return value == "deg" ? AngleUnit.Degrees : AngleUnit.Radians;
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException ||
                e is UnauthorizedAccessException || e is System.IO.IOException)
            {
                return AngleUnit.Radians;
            }
        }

        public void Save(AngleUnit unit)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue(ValueName, unit == AngleUnit.Degrees ? "deg" : "rad", RegistryValueKind.String);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException ||
                e is UnauthorizedAccessException || e is System.IO.IOException)
            {
                Utilities.Logger.GetLogger().Warn("Could not remember the angle unit: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Shows angle text boxes in degrees or radians. The model fills the boxes with radians (full
    /// precision) and reads them back as radians; this class only changes what the box shows in
    /// between. A box the user did not edit gives back exactly the radian text it was filled with,
    /// so switching units or saving never changes a stored value. A value typed in degrees is
    /// converted to radians when it is read.
    /// </summary>
    public sealed class AngleDisplay
    {
        private sealed class Shown
        {
            public string Radians;
            public string Display;
        }

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private const NumberStyles Style = NumberStyles.Float;
        private readonly Dictionary<TextBox, Shown> shown = new Dictionary<TextBox, Shown>();

        public AngleDisplay(AngleUnit unit)
        {
            Unit = unit;
        }

        public AngleUnit Unit { get; private set; }

        // Text for a radian value in the given unit. Degrees are shown with 12 significant digits,
        // so 0.523598775598299 rad reads 30; the exact radian value stays behind the box.
        public static string Format(double radians, AngleUnit unit)
        {
            return unit == AngleUnit.Degrees
                ? (radians * 180.0 / Math.PI).ToString("G12", Invariant)
                : radians.ToString("R", Invariant);
        }

        // Radians for a value typed in the given unit.
        public static double ToRadians(double value, AngleUnit unit)
        {
            return unit == AngleUnit.Degrees ? value * Math.PI / 180.0 : value;
        }

        /// <summary>Call after the model filled the boxes with radian text.</summary>
        public void Show(params TextBox[] boxes)
        {
            foreach (TextBox box in boxes)
            {
                Shown entry = new Shown { Radians = box.Text, Display = box.Text };
                double radians;
                if (Unit == AngleUnit.Degrees && double.TryParse(box.Text, Style, Invariant, out radians))
                {
                    entry.Display = Format(radians, Unit);
                }
                shown[box] = entry;
                box.Text = entry.Display;
            }
        }

        /// <summary>Boxes that no longer show angles (blanked or refilled for another joint).</summary>
        public void Forget(IEnumerable<Control> boxes)
        {
            foreach (Control box in boxes)
            {
                TextBox text = box as TextBox;
                if (text != null)
                {
                    shown.Remove(text);
                }
            }
        }

        /// <summary>
        /// Puts radian text back into the angle boxes so the model can read them: the original text
        /// when the box was not edited, otherwise the typed value converted to radians. Text that is
        /// not a number is left for the model to handle as before.
        /// </summary>
        public void Restore()
        {
            foreach (KeyValuePair<TextBox, Shown> pair in shown)
            {
                TextBox box = pair.Key;
                if (box.Text == pair.Value.Display)
                {
                    box.Text = pair.Value.Radians;
                }
                else
                {
                    double typed;
                    if (Unit == AngleUnit.Degrees && double.TryParse(box.Text, Style, Invariant, out typed))
                    {
                        box.Text = ToRadians(typed, Unit).ToString("R", Invariant);
                    }
                }
            }
        }

        /// <summary>Shows the boxes in the display unit again after Restore.</summary>
        public void ShowAgain()
        {
            Show(new List<TextBox>(shown.Keys).ToArray());
        }

        /// <summary>Changes the unit of the boxes on display, keeping edits and exact values.</summary>
        public void SetUnit(AngleUnit unit)
        {
            if (unit == Unit)
            {
                return;
            }
            Restore();
            Unit = unit;
            ShowAgain();
        }
    }
}
