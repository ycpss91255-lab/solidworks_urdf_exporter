using System;
using System.Collections.Generic;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks. When the dimension of a limit mate may be moved, and when it counts as put back.</summary>
    public class TestLimitMateProbe
    {
        [Fact]
        public void AnEquationOnTheLeftSideDrivesTheDimension()
        {
            var equations = new List<string> { "\"A\"= 60", "\"D1@LimitAngle1\"= \"A\" * 2" };
            Assert.True(LimitMateProbe.EquationDrives(equations, "D1@LimitAngle1"));
        }

        [Fact]
        public void TheFullNameWithTheDocumentIsRecognised()
        {
            Assert.True(LimitMateProbe.EquationDrives(new[] { "\"D1@LimitAngle1@HINGE.SLDASM\" = 45deg" }, "D1@LimitAngle1"));
        }

        [Fact]
        public void AUseOnTheRightSideOrAnotherDimensionOrACommentDoesNotCount()
        {
            Assert.False(LimitMateProbe.EquationDrives(new[]
            {
                "\"D1@Sketch1\" = \"D1@LimitAngle1\" * 2",
                "\"D1@LimitAngle10\" = 5",
                "'\"D1@LimitAngle1\" = 5",
                "",
                null
            }, "D1@LimitAngle1"));
        }

        [Fact]
        public void AnEqualsSignInsideAQuotedNameIsNotTheAssignment()
        {
            Assert.False(LimitMateProbe.EquationDrives(new[] { "\"a=b\" = \"D1@LimitAngle1\"" }, "D1@LimitAngle1"));
        }

        [Fact]
        public void NoEquationsMeansNotDriven()
        {
            Assert.False(LimitMateProbe.EquationDrives(null, "D1@LimitAngle1"));
            Assert.False(LimitMateProbe.EquationDrives(new string[0], "D1@LimitAngle1"));
        }

        [Fact]
        public void ADimensionThatCanBePutBackMayBeMoved()
        {
            Assert.Null(LimitMateProbe.Blocker(true, false, false, false, false, 1.5707963267948966));
        }

        [Fact]
        public void EveryReasonBlocksTheMove()
        {
            Assert.NotNull(LimitMateProbe.Blocker(false, false, false, false, false, 1.0));
            Assert.NotNull(LimitMateProbe.Blocker(true, true, false, false, false, 1.0));
            Assert.NotNull(LimitMateProbe.Blocker(true, false, true, false, false, 1.0));
            Assert.NotNull(LimitMateProbe.Blocker(true, false, false, true, false, 1.0));
            Assert.NotNull(LimitMateProbe.Blocker(true, false, false, false, true, 1.0));
            Assert.NotNull(LimitMateProbe.Blocker(true, false, false, false, false, double.NaN));
            Assert.NotNull(LimitMateProbe.Blocker(true, false, false, false, false, double.PositiveInfinity));
        }

        [Fact]
        public void ValuesMatchWithinTheTolerance()
        {
            Assert.True(LimitMateProbe.ValuesMatch(1.5707963267948966, 1.5707963267948966));
            Assert.True(LimitMateProbe.ValuesMatch(0.01 + 1e-12, 0.01));
            Assert.False(LimitMateProbe.ValuesMatch(0.01 + 1e-6, 0.01));
            Assert.False(LimitMateProbe.ValuesMatch(double.NaN, 0.01));
        }

        [Fact]
        public void PosesMatchWhenEveryNumberIsClose()
        {
            double[][] a = { new[] { 0.0, 0.0, 0.02 }, new[] { 1.0, 0.0, 0.0 } };
            double[][] same = { new[] { 0.0, 1e-9, 0.02 }, new[] { 1.0, 0.0, 0.0 } };
            double[][] moved = { new[] { 0.0, 0.0, 0.021 }, new[] { 1.0, 0.0, 0.0 } };
            double[][] turned = { new[] { 0.0, 0.0, 0.02 }, new[] { 0.99, 0.1, 0.0 } };
            Assert.True(LimitMateProbe.PointsMatch(a, same));
            Assert.False(LimitMateProbe.PointsMatch(a, moved));
            Assert.False(LimitMateProbe.PointsMatch(a, turned));
            Assert.False(LimitMateProbe.PointsMatch(a, null));
            Assert.False(LimitMateProbe.PointsMatch(a, new[] { a[0] }));
            Assert.False(LimitMateProbe.PointsMatch(a, new[] { new[] { 0.0 }, a[1] }));
            Assert.False(LimitMateProbe.PointsMatch(a, new[] { new[] { 0.0, 0.0, double.NaN }, a[1] }));
        }

        [Fact]
        public void RestoreStopsAtTheFirstSuccess()
        {
            int calls = 0;
            Assert.True(LimitMateProbe.RestoreVerified(attempt => { calls++; return true; }, 2));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void RestoreTriesOnceMoreAfterAFailure()
        {
            var attempts = new List<int>();
            Assert.True(LimitMateProbe.RestoreVerified(attempt => { attempts.Add(attempt); return attempt == 2; }, 2));
            Assert.Equal(new[] { 1, 2 }, attempts.ToArray());
        }

        [Fact]
        public void RestoreGivesUpAfterTheLastAttempt()
        {
            int calls = 0;
            Assert.False(LimitMateProbe.RestoreVerified(attempt => { calls++; return false; }, 2));
            Assert.Equal(2, calls);
        }

        [Fact]
        public void ARestoreThatThrowsCountsAsFailedAndIsRetried()
        {
            int calls = 0;
            bool ok = LimitMateProbe.RestoreVerified(attempt =>
            {
                calls++;
                if (attempt == 1) throw new InvalidOperationException("COM error");
                return true;
            }, 2);
            Assert.True(ok);
            Assert.Equal(2, calls);
        }

        [Fact]
        public void ValuesAreShownInDegreesAndMillimetres()
        {
            Assert.Equal("90 deg", LimitMateProbe.FormatValue(false, Math.PI / 2));
            Assert.Equal("10 mm", LimitMateProbe.FormatValue(true, 0.010));
            Assert.Equal("12.5 mm", LimitMateProbe.FormatValue(true, 0.0125));
        }
    }
}
