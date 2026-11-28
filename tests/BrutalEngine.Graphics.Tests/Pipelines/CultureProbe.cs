using System;
using System.Globalization;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>Runs code under a culture whose number symbols all differ from the invariant culture's.</summary>
internal static class CultureProbe
{
    /// <summary>
    /// Returns <paramref name="format"/>'s result under a clone of the invariant culture with every symbol a number format
    /// uses changed (no OS culture data needed), so any culture-sensitive formatting shows in the output.
    /// </summary>
    public static string Format(Func<string> format)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = "'";
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            // Proves the probe culture is in effect: the current culture formats -3 with its own sign.
            if ((-3).ToString(CultureInfo.CurrentCulture) != "~3")
            {
                throw new InvalidOperationException("The probe culture isn't in effect.");
            }

            return format();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
