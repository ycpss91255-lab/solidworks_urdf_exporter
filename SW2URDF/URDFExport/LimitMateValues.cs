using System;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// The variations of a limit mate computed from the mate's own values (its feature data): the
    /// minimum and maximum limit relative to the current mate dimension. SolidWorks also reports
    /// Mate2.MinimumVariation and MaximumVariation, but those are derived from the solved geometry and
    /// carry solver noise. Angles are in radians and distances in metres, as SolidWorks reports them.
    /// Free of SolidWorks calls so it can be tested.
    /// </summary>
    public static class LimitMateValues
    {
        // The values are typed in degrees and millimetres and stored converted, so a plain difference of
        // the stored values is off by a unit in the last place (pi/2 - pi/3 is not pi/6). The difference
        // is taken in the typed unit instead and converted once.
        public static void Variations(bool angle, double current, double minimum, double maximum,
            out double minimumVariation, out double maximumVariation)
        {
            double typedCurrent = Typed(ToTypedUnit(angle, current));
            minimumVariation = FromTypedUnit(angle, Typed(ToTypedUnit(angle, minimum)) - typedCurrent);
            maximumVariation = FromTypedUnit(angle, Typed(ToTypedUnit(angle, maximum)) - typedCurrent);
        }

        private static double ToTypedUnit(bool angle, double value)
        {
            return angle ? value * 180.0 / Math.PI : value * 1000.0;
        }

        private static double FromTypedUnit(bool angle, double value)
        {
            return angle ? value * Math.PI / 180.0 : value / 1000.0;
        }

        // Converting a typed decimal and back leaves it a few units in the last place off (60 deg reads
        // back as 59.999999999999986). When an equation or a global variable sets the value, SolidWorks
        // stores the evaluated value rounded to about 15 significant digits (90 deg is stored as
        // 1.5707963267949 rad and reads back as 90.00000000000019). Such a value, within 1e-14 of its
        // size from a decimal with at most 10 places, is put back on that decimal; any other value, for
        // example 180/7 deg, is kept as it is.
        internal static double Typed(double value)
        {
            double rounded = Math.Round(value, 10);
            double tolerance = 1e-14 * Math.Max(Math.Abs(value), 1.0);
            return Math.Abs(value - rounded) <= tolerance ? rounded : value;
        }
    }
}
