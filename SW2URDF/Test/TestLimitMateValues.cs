using System;
using System.Globalization;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. Issue #168: a limit mate's limits must come out exactly as typed. The
    /// mate values are given the way SolidWorks stores them (radians, metres), converted from the typed
    /// degrees and millimetres. The expected limits are computed from the typed numbers, and the
    /// comparison is exact: no tolerance.
    /// </summary>
    public class TestLimitMateValues
    {
        private static double Radians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static void Limits(bool angle, int direction, double current, double minimum, double maximum,
            out double lower, out double upper)
        {
            LimitMateValues.Variations(angle, current, minimum, maximum, out double minimumVariation,
                out double maximumVariation);
            ExportHelper.LimitFromDirection(direction, minimumVariation, maximumVariation, out lower, out upper);
        }

        [Fact]
        public void AngleVariationsAreTheTypedDifferences()
        {
            // minimum 60 deg, maximum 180 deg, mate angle 90 deg: -30 .. +90 deg
            LimitMateValues.Variations(true, Radians(90), Radians(60), Radians(180), out double minimum, out double maximum);
            Assert.Equal(Radians(60 - 90), minimum);
            Assert.Equal(Radians(180 - 90), maximum);
            Assert.Equal(-Math.PI / 6, minimum);
            Assert.Equal(Math.PI / 2, maximum);
        }

        [Fact]
        public void AngleLimitsAreExactForBothDirections()
        {
            Limits(true, -1, Radians(90), Radians(60), Radians(180), out double lower, out double upper);
            Assert.Equal(-Math.PI / 2, lower);
            Assert.Equal(Math.PI / 6, upper);
            Limits(true, 1, Radians(90), Radians(60), Radians(180), out lower, out upper);
            Assert.Equal(-Math.PI / 6, lower);
            Assert.Equal(Math.PI / 2, upper);
        }

        [Fact]
        public void ValuesAsSolidWorksStoresThemGiveTheSameLimits()
        {
            // A live read of the example mate: the minimum is stored one unit in the last place below pi/3.
            Limits(true, -1, 1.5707963267948966, 1.0471975511965974, 3.141592653589793, out double lower, out double upper);
            Assert.Equal(-Math.PI / 2, lower);
            Assert.Equal(Math.PI / 6, upper);
        }

        [Fact]
        public void DistanceLimitsAreExactForBothDirections()
        {
            // minimum 0 mm, maximum 30 mm, mate distance 10 mm: -10 .. +20 mm
            LimitMateValues.Variations(false, 10 / 1000.0, 0, 30 / 1000.0, out double minimum, out double maximum);
            Assert.Equal(-0.01, minimum);
            Assert.Equal(0.02, maximum);
            Limits(false, 1, 0.01, 0, 0.03, out double lower, out double upper);
            Assert.Equal(-0.01, lower);
            Assert.Equal(0.02, upper);
            Limits(false, -1, 0.01, 0, 0.03, out lower, out upper);
            Assert.Equal(-0.02, lower);
            Assert.Equal(0.01, upper);
        }

        [Fact]
        public void WrittenAndShownValuesHaveNoNoise()
        {
            Limits(true, -1, Radians(90), Radians(60), Radians(180), out double lower, out double upper);
            // The URDF writes the default 15-digit text of the value.
            Assert.Equal((-Math.PI / 2).ToString(CultureInfo.InvariantCulture), lower.ToString(CultureInfo.InvariantCulture));
            Assert.Equal((Math.PI / 6).ToString(CultureInfo.InvariantCulture), upper.ToString(CultureInfo.InvariantCulture));
            // The joint page shows degrees with 12 significant digits.
            Assert.Equal("-90", (lower * 180.0 / Math.PI).ToString("G12", CultureInfo.InvariantCulture));
            Assert.Equal("30", (upper * 180.0 / Math.PI).ToString("G12", CultureInfo.InvariantCulture));
        }

        [Fact]
        public void UntypedValuesAreKept()
        {
            // A value an equation computed (here 180/7 deg) is not a typed decimal: it is not rounded.
            double current = Radians(180.0 / 7.0);
            LimitMateValues.Variations(true, current, 0, Radians(90), out double minimum, out double maximum);
            Assert.InRange(minimum, -current - 1e-15, -current + 1e-15);
            Assert.InRange(maximum, Radians(90) - current - 1e-15, Radians(90) - current + 1e-15);
        }
    }
}
