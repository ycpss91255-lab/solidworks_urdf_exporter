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
        public static void Variations(bool angle, double current, double minimum, double maximum,
            out double minimumVariation, out double maximumVariation)
        {
            minimumVariation = minimum - current;
            maximumVariation = maximum - current;
        }
    }
}
