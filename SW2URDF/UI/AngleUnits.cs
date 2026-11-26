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
    /// Shows angle text boxes in degrees or radians. The model fills the boxes with radian text and
    /// reads them back as radians; this class only changes what the box shows in between. Degrees are
    /// made from the exact value the model stores, not from the text in the box, which may be rounded
    /// (-pi/2 as -1.57079632679 would read -89.9999999997 deg). A box the user did not edit gives back
    /// exactly the text it was filled with, so the model recognizes it as unedited and keeps its value.
    /// A value typed in degrees is converted to radians when it is read, and stays as typed in its unit.
    /// </summary>
    public sealed class AngleDisplay
    {
        private sealed class Shown
        {
            // Text the model put in the box, or that stands for the typed value.
            public string Radians;
            // Exact value behind the box; null when the box holds no number.
            public double? Exact;
            public string Display;
            // What the user typed, and the unit it was typed in; null when the box was not edited.
            public string Typed;
            public AngleUnit TypedUnit;
            public bool Restored;
        }

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private const NumberStyles Style = NumberStyles.Float;
        private readonly Dictionary<TextBox, Shown> shown = new Dictionary<TextBox, Shown>();

        public AngleDisplay(AngleUnit unit)
        {
            Unit = unit;
        }

        public AngleUnit Unit { get; private set; }

        // Text for a radian value in the given unit. Degrees are rounded here only, to 12 significant
        // digits, so 0.523598775598299 rad reads 30; the exact radian value stays behind the box.
        public static string Format(double radians, AngleUnit unit)
        {
            return unit == AngleUnit.Degrees
                ? (radians * 180.0 / Math.PI).ToString("G12", Invariant)
                : RoundTrip(radians);
        }

        // Radians for a value typed in the given unit.
        public static double ToRadians(double value, AngleUnit unit)
        {
            return unit == AngleUnit.Degrees ? value * Math.PI / 180.0 : value;
        }

        // Text that parses back to the same double. "R" does not always do that on .NET Framework.
        private static string RoundTrip(double value)
        {
            string text = value.ToString("R", Invariant);
            double parsed;
            return double.TryParse(text, Style, Invariant, out parsed) && parsed.Equals(value)
                ? text
                : value.ToString("G17", Invariant);
        }

        /// <summary>
        /// Call after the model filled the box with radian text. stored gives the exact value the model
        /// holds for it; it is not asked when the box is blank, which is how the model shows no value.
        /// </summary>
        public void Show(TextBox box, Func<double> stored)
        {
            Shown entry = new Shown { Radians = box.Text };
            if (!string.IsNullOrWhiteSpace(box.Text))
            {
                entry.Exact = stored();
            }
            shown[box] = entry;
            Display(box, entry);
        }

        private void Display(TextBox box, Shown entry)
        {
            if (entry.Typed != null && entry.TypedUnit == Unit)
            {
                entry.Display = entry.Typed;
            }
            else
            {
                entry.Display = Unit == AngleUnit.Degrees && entry.Exact.HasValue
                    ? Format(entry.Exact.Value, Unit)
                    : entry.Radians;
            }
            entry.Restored = false;
            box.Text = entry.Display;
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
                Shown entry = pair.Value;
                if (entry.Restored && box.Text == entry.Radians)
                {
                    continue;
                }
                if (box.Text != entry.Display)
                {
                    TakeTyped(box.Text, entry);
                }
                entry.Restored = true;
                box.Text = entry.Radians;
            }
        }

        private void TakeTyped(string text, Shown entry)
        {
            double typed;
            entry.Typed = text;
            entry.TypedUnit = Unit;
            entry.Display = text;
            if (double.TryParse(text, Style, Invariant, out typed))
            {
                entry.Exact = ToRadians(typed, Unit);
                entry.Radians = Unit == AngleUnit.Degrees ? RoundTrip(entry.Exact.Value) : text;
            }
            else
            {
                entry.Exact = null;
                entry.Radians = text;
            }
        }

        /// <summary>Shows the boxes in the display unit again after Restore, from the exact values.</summary>
        public void ShowAgain()
        {
            foreach (KeyValuePair<TextBox, Shown> pair in shown)
            {
                Display(pair.Key, pair.Value);
            }
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
