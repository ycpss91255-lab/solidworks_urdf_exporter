using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The numbers are what a live run on a minimal hinge and a minimal slide
    /// measured: the variation SolidWorks reports for the limit mate, and the range the child really
    /// moves along the exported axis (axis = world +Z in all of them).
    /// </summary>
    public class TestLimitFromMate
    {
        private const double Tolerance = 1e-9;

        private static void Check(bool prismatic, bool flipped, double minimum, double maximum,
            double expectedLower, double expectedUpper)
        {
            ExportHelper.LimitFromMate(prismatic, flipped, minimum, maximum, out double lower, out double upper);
            Assert.InRange(lower, expectedLower - Tolerance, expectedLower + Tolerance);
            Assert.InRange(upper, expectedUpper - Tolerance, expectedUpper + Tolerance);
        }

        [Fact]
        public void HingeAngleLimitsAreNegated()
        {
            // angle mate, reference 90 deg, limits 60..180: variation -30..+90 deg, arm turns +30..-90 deg
            Check(false, false, -0.5235987755982988, 1.5707963267948966, -1.5707963267948966, 0.5235987755982988);
        }

        [Fact]
        public void SymmetricHingeKeepsItsRange()
        {
            Check(false, false, -1.5707963267948966, 1.5707963267948966, -1.5707963267948966, 1.5707963267948966);
        }

        [Fact]
        public void FlippedHingeIsCopied()
        {
            // the same angle mate with Flip: the arm turns -30..+90 deg
            Check(false, true, -0.5235987755982988, 1.5707963267948966, -0.5235987755982988, 1.5707963267948966);
        }

        [Fact]
        public void SlideDistanceLimitsAreCopied()
        {
            // distance mate, reference 10 mm, limits 0..30 mm: variation -10..+20 mm, arm moves -10..+20 mm
            Check(true, false, -0.010, 0.020, -0.010, 0.020);
        }

        [Fact]
        public void FlippedSlideIsNegated()
        {
            // the same distance mate with Flip: the arm moves -20..+10 mm
            Check(true, true, -0.010, 0.020, -0.020, 0.010);
        }

        [Fact]
        public void LowerIsNeverAboveUpper()
        {
            foreach (bool prismatic in new[] { false, true })
            {
                foreach (bool flipped in new[] { false, true })
                {
                    ExportHelper.LimitFromMate(prismatic, flipped, -0.3, 0.7, out double lower, out double upper);
                    Assert.True(lower <= upper);
                }
            }
        }
    }
}
