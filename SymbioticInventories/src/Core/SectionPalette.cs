namespace SymbioticInventories.Core
{
    /// <summary>
    /// Accent colours for section shading.
    ///
    /// Chosen to stay distinguishable against Vintage Story's dark parchment GUI and to
    /// remain separable for the common red/green colour vision deficiencies - hues are
    /// spread across the wheel rather than clustered, and each entry differs from its
    /// neighbours in lightness as well as hue, so the shading still reads if hue does not.
    /// The number badge is the authoritative cue; colour is the fast secondary cue.
    /// </summary>
    public static class SectionPalette
    {
        // r, g, b in 0..1
        private static readonly double[][] Wheel =
        {
            new[] { 0.36, 0.62, 0.94 }, // 1 - blue
            new[] { 0.95, 0.68, 0.24 }, // 2 - amber
            new[] { 0.44, 0.80, 0.46 }, // 3 - green
            new[] { 0.85, 0.42, 0.78 }, // 4 - magenta
            new[] { 0.40, 0.83, 0.85 }, // 5 - cyan
            new[] { 0.93, 0.49, 0.36 }, // 6 - coral
            new[] { 0.68, 0.60, 0.92 }, // 7 - violet
            new[] { 0.78, 0.80, 0.36 }, // 8 - olive
        };

        /// <summary>Accent for the n-th numbered section (1-based). Wraps past the end of the wheel.</summary>
        public static double[] ForNumber(int number)
        {
            if (number < 1) number = 1;
            return Wheel[(number - 1) % Wheel.Length];
        }

        /// <summary>Neutral accent for sections that carry no badge (crafting grid, hotbar, bag slots).</summary>
        public static double[] Neutral => new[] { 0.62, 0.60, 0.55 };
    }
}
