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
        // back as 59.999999999999986). Such a value is put back on its decimal; any other value, for
        // example one an equation computed, is kept as it is.
        internal static double Typed(double value)
        {
            double rounded = Math.Round(value, 10);
            double tolerance = 8 * Math.Max(Math.Abs(value), 1.0) * 2.220446049250313e-16;
            return Math.Abs(value - rounded) <= tolerance ? rounded : value;
        }
    }
}
