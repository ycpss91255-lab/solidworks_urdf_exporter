namespace SW2URDF.UI
{
    /// <summary>
    /// How the export forms show numbers in their boxes. Twelve significant digits hide the binary
    /// floating-point noise of a stored value (-0.019999999999999997 reads -0.02) without hiding
    /// anything a user would type. The stored value itself is not rounded: the forms compare a box
    /// with the text they showed in it (URDFAttribute.IsUneditedShownText), so a box that was not
    /// edited gives back the exact stored value, and a typed value is taken as typed.
    /// </summary>
    public static class ValueFormat
    {
        public const string Box = "G12";
    }
}
