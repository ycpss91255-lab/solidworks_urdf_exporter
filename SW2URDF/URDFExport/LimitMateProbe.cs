using System;
using System.Collections.Generic;
using System.Globalization;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Decisions around moving the dimension of a limit mate to see which way the child moves. None of
    /// it calls SolidWorks, so it can be tested: when moving the dimension is allowed at all, whether
    /// it has been put back, and how its value is shown to the user.
    /// </summary>
    public static class LimitMateProbe
    {
        // Values are in metres and radians; the solver puts a child back within a fraction of a micrometre.
        public const double ValueTolerance = 1e-9;
        public const double PointTolerance = 1e-7;

        /// <summary>
        /// True when one of the equations of the model has the dimension on its left side, that is when
        /// an equation or a global variable decides its value and a value that is set is thrown away
        /// (or fought over) at the next rebuild. Comment lines (starting with ') are ignored.
        /// </summary>
        public static bool EquationDrives(IEnumerable<string> equations, string dimensionName)
        {
            if (equations == null || string.IsNullOrEmpty(dimensionName))
            {
                return false;
            }
            foreach (string equation in equations)
            {
                if (string.IsNullOrWhiteSpace(equation))
                {
                    continue;
                }
                string text = equation.Trim();
                if (text.StartsWith("'"))
                {
                    continue;
                }
                int equals = IndexOfAssignment(text);
                if (equals <= 0)
                {
                    continue;
                }
                string left = text.Substring(0, equals).Trim().Trim('"');
                if (string.Equals(left, dimensionName, StringComparison.OrdinalIgnoreCase) ||
                    left.StartsWith(dimensionName + "@", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // The first '=' that is not inside a quoted name
        private static int IndexOfAssignment(string text)
        {
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (text[i] == '=' && !quoted)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Why the dimension must not be moved, or null when it may be: it is not there, cannot be
        /// changed, is a driven (reference) dimension, belongs to a design table, is decided by an
        /// equation, or its value cannot be read back.
        /// </summary>
        public static string Blocker(bool found, bool readOnly, bool drivenDimension, bool designTable,
            bool equation, double value)
        {
            if (!found) return "the dimension of the mate was not found";
            if (readOnly) return "the dimension is read-only";
            if (drivenDimension) return "the dimension is a driven (reference) dimension";
            if (designTable) return "the dimension is controlled by a design table";
            if (equation) return "the dimension is controlled by an equation or a global variable";
            if (double.IsNaN(value) || double.IsInfinity(value)) return "the value of the dimension cannot be read back";
            return null;
        }

        public static bool ValuesMatch(double now, double start)
        {
            return Math.Abs(now - start) <= ValueTolerance * (1 + Math.Abs(start));
        }

        /// <summary>Both lists hold arrays of numbers (points, directions); they match when every number is within the tolerance.</summary>
        public static bool PointsMatch(double[][] before, double[][] after)
        {
            if (before == null || after == null || before.Length != after.Length)
            {
                return false;
            }
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == null || after[i] == null || before[i].Length != after[i].Length)
                {
                    return false;
                }
                for (int j = 0; j < before[i].Length; j++)
                {
                    if (!(Math.Abs(before[i][j] - after[i][j]) <= PointTolerance))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Runs the restore step (it puts the dimension back and returns whether the dimension and the
        /// child are back where they were) until it succeeds, at most the given number of times. A step
        /// that throws counts as failed. Returns whether the model is back as it was.
        /// </summary>
        public static bool RestoreVerified(Func<int, bool> restoreAndVerify, int attempts)
        {
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (restoreAndVerify(attempt))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // counted as a failed attempt
                }
            }
            return false;
        }

        /// <summary>A dimension value as the user sees it: degrees for an angle, millimetres for a distance.</summary>
        public static string FormatValue(bool prismatic, double systemValue)
        {
            return prismatic
                ? (systemValue * 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " mm"
                : (systemValue * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " deg";
        }
    }
}
