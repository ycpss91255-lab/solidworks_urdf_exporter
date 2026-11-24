using SW2URDF.UI;
using SW2URDF.URDF;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks: the text the joint form shows for the origin of the limits.</summary>
    public class TestLimitMateInfo
    {
        private static LimitMateInfo Distance(bool measured, string reason = null)
        {
            // limit mate 0..30 mm, reference 10 mm: variation -10..+20 mm, written as -0.01..0.02 m
            return new LimitMateInfo
            {
                MateName = "Distance1",
                IsAngle = false,
                Reference = 0.010,
                MinimumVariation = -0.010,
                MaximumVariation = 0.020,
                Lower = -0.010,
                Upper = 0.020,
                DirectionMeasured = measured,
                NotMeasuredReason = reason
            };
        }

        [Fact]
        public void DistanceMateShowsSolidWorksRangeAndRelativeRange()
        {
            string text = Distance(true).Describe(new UiText("english"));
            Assert.Contains("Limit mate Distance1 (distance limit mate)", text);
            Assert.Contains("SolidWorks range 0 mm to 30 mm, reference 10 mm", text);
            Assert.Contains("URDF (relative): -10 mm to 20 mm = -0.01 m to 0.02 m", text);
            Assert.Contains("Direction: measured on the model", text);
        }

        [Fact]
        public void AngleMateShowsDegreesAndRadians()
        {
            LimitMateInfo info = new LimitMateInfo
            {
                MateName = "Angle1",
                IsAngle = true,
                Reference = System.Math.PI / 2,
                MinimumVariation = -System.Math.PI / 6,
                MaximumVariation = System.Math.PI / 2,
                Lower = -System.Math.PI / 2,
                Upper = System.Math.PI / 6,
                DirectionMeasured = true
            };
            string text = info.Describe(new UiText("english"));
            Assert.Contains("(angle limit mate)", text);
            Assert.Contains("SolidWorks range 60 deg to 180 deg, reference 90 deg", text);
            Assert.Contains("URDF (relative): -90 deg to 30 deg = -1.5708 rad to 0.5236 rad", text);
        }

        [Fact]
        public void RuleFallbackSaysWhy()
        {
            string text = Distance(false, "the dimension is controlled by an equation or a global variable")
                .Describe(new UiText("english"));
            Assert.Contains("Direction: taken from the mate type rule (the dimension is controlled by an equation or a global variable)", text);
        }

        [Fact]
        public void UnknownReferenceShowsTheVariation()
        {
            LimitMateInfo info = Distance(true);
            info.Reference = double.NaN;
            string text = info.Describe(new UiText("english"));
            Assert.Contains("SolidWorks variation -10 mm to 20 mm", text);
        }

        [Fact]
        public void ManualLimitsAreSaidToBeManual()
        {
            UiText ui = new UiText("english");
            Assert.Contains("entered manually", LimitMateInfo.Describe(null, 0, 1, ui));
        }

        [Fact]
        public void EditedLimitsAreNoLongerAttributedToTheMate()
        {
            UiText ui = new UiText("english");
            LimitMateInfo info = Distance(true);
            Assert.Contains("Distance1", LimitMateInfo.Describe(info, -0.010, 0.020, ui));
            Assert.Contains("entered manually", LimitMateInfo.Describe(info, -0.010, 0.025, ui));
        }

        [Fact]
        public void TraditionalChineseTextIsUsed()
        {
            string text = Distance(false, "the dimension is controlled by an equation or a global variable")
                .Describe(new UiText("chinese"));
            Assert.Contains("距離極限配合", text);
            Assert.Contains("方程式", text);
            Assert.Contains("-0.01 m", text);
        }

        [Fact]
        public void CloneIsIndependent()
        {
            LimitMateInfo info = Distance(true);
            LimitMateInfo clone = info.Clone();
            clone.Lower = 5;
            Assert.Equal(-0.010, info.Lower);
        }

        [Fact]
        public void JointCopiesTheInfoWithoutSerialisingIt()
        {
            Joint joint = new Joint();
            joint.LimitInfo = Distance(true);
            Joint copy = new Joint();
            copy.SetElement(joint);
            Assert.NotNull(copy.LimitInfo);
            Assert.NotSame(joint.LimitInfo, copy.LimitInfo);
        }
    }
}