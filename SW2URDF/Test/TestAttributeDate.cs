using System;
using System.Globalization;
using System.Threading;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>Does not need SolidWorks. The date of the URDF attributes must not depend on the regional settings (issue 131).</summary>
    public class TestAttributeDate
    {
        private static readonly DateTimeOffset Time = new DateTimeOffset(2026, 3, 4, 17, 6, 7, TimeSpan.FromHours(8));

        private static string Written(string culture)
        {
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(culture, false);
                return ConfigurationSerialization.AttributeDate(Time);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Fact]
        public void IsIso8601()
        {
            Assert.Equal("2026-03-04T17:06:07+08:00", Written("en-US"));
        }

        // Other separators (da-DK, de-DE), calendars (ar-SA, th-TH) and day parts (zh-TW)
        [Fact]
        public void IsTheSameForEveryCulture()
        {
            foreach (string culture in new[] { "zh-TW", "de-DE", "da-DK", "th-TH", "ar-SA", "fa-IR" })
            {
                Assert.Equal("2026-03-04T17:06:07+08:00", Written(culture));
            }
        }

        [Fact]
        public void CanBeReadBack()
        {
            Assert.Equal(Time, DateTimeOffset.Parse(Written("zh-TW"), CultureInfo.InvariantCulture));
        }
    }
}
