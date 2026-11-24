using System;
using System.Globalization;
using SW2URDF.UI;

namespace SW2URDF.URDF
{
    /// <summary>
    /// Where the limits of a joint came from, kept next to the joint for the joint form only. It is not
    /// serialised and never changes what is exported. Lengths are in metres and angles in radians, as
    /// SolidWorks reports them. Formatting does not call SolidWorks, so it can be tested.
    /// </summary>
    public class LimitMateInfo
    {
        public string MateName;
        public bool IsAngle;
        /// <summary>Value of the mate dimension when the joint was estimated; NaN when it could not be read.</summary>
        public double Reference = double.NaN;
        /// <summary>Variation SolidWorks reports, relative to the reference.</summary>
        public double MinimumVariation;
        public double MaximumVariation;
        /// <summary>Range written to the URDF.</summary>
        public double Lower;
        public double Upper;
        public bool DirectionMeasured;
        /// <summary>When the direction was not measured: why (English text, translated when shown), or null.</summary>
        public string NotMeasuredReason;

        private const double Tolerance = 1e-9;

        /// <summary>True while the limits of the joint are still the ones computed from the mate.</summary>
        public bool Matches(double lower, double upper)
        {
            return Math.Abs(lower - Lower) < Tolerance && Math.Abs(upper - Upper) < Tolerance;
        }

        public LimitMateInfo Clone()
        {
            return (LimitMateInfo)MemberwiseClone();
        }

        private static string Number(double value)
        {
            return Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }

        private string Display(double si)
        {
            return IsAngle ? Number(si * 180.0 / Math.PI) + " deg" : Number(si * 1000.0) + " mm";
        }

        private string Urdf(double si)
        {
            return IsAngle ? Number(si) + " rad" : Number(si) + " m";
        }

        /// <summary>The one-line description of limits that are entered by hand (or loaded from a configuration).</summary>
        public static string ManualText(UiText ui)
        {
            return ui.Get("Limits: entered manually, not read from a limit mate");
        }

        /// <summary>The read-only text for a joint, from its limit mate info or, when null or no longer matching, the manual text.</summary>
        public static string Describe(LimitMateInfo info, double lower, double upper, UiText ui)
        {
            if (info == null || !info.Matches(lower, upper))
            {
                return ManualText(ui);
            }
            return info.Describe(ui);
        }

        // The reasons come from LimitMateProbe.Blocker; each is looked up with a literal key.
        private string Reason(UiText ui)
        {
            switch (NotMeasuredReason)
            {
                case "the dimension of the mate was not found": return ui.Get("the dimension of the mate was not found");
                case "the dimension is read-only": return ui.Get("the dimension is read-only");
                case "the dimension is a driven (reference) dimension": return ui.Get("the dimension is a driven (reference) dimension");
                case "the dimension is controlled by a design table": return ui.Get("the dimension is controlled by a design table");
                case "the dimension is controlled by an equation or a global variable": return ui.Get("the dimension is controlled by an equation or a global variable");
                case "the value of the dimension cannot be read back": return ui.Get("the value of the dimension cannot be read back");
                default: return ui.Get("the direction could not be measured");
            }
        }

        public string Describe(UiText ui)
        {
            string type = IsAngle ? ui.Get("angle limit mate") : ui.Get("distance limit mate");
            string range = double.IsNaN(Reference)
                ? string.Format(ui.Get("SolidWorks variation {0} to {1}"),
                    Display(MinimumVariation), Display(MaximumVariation))
                : string.Format(ui.Get("SolidWorks range {0} to {1}, reference {2}"),
                    Display(Reference + MinimumVariation), Display(Reference + MaximumVariation), Display(Reference));
            string written = string.Format(ui.Get("URDF (relative): {0} to {1} = {2} to {3}"),
                Display(Lower), Display(Upper), Urdf(Lower), Urdf(Upper));
            string direction = DirectionMeasured
                ? ui.Get("Direction: measured on the model")
                : string.Format(ui.Get("Direction: taken from the mate type rule ({0})"),
                    Reason(ui));
            return string.Format(ui.Get("Limit mate {0} ({1})"), MateName, type) + "\n" +
                range + "\n" + written + "\n" + direction;
        }
    }
}
