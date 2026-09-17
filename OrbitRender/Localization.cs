using System;
using System.Globalization;
using UnityEngine;

namespace OrbitRender
{
    internal static class Localization
    {
        // ADOFAI stores the selected game language in RDString. Do not use
        // Application.systemLanguage here: the game language can be changed
        // independently from the operating system language.
        internal static bool IsKorean
        {
            get
            {
                try { return RDString.language == SystemLanguage.Korean; }
                catch { return false; }
            }
        }

        internal static string Text(string english, string korean)
        {
            return IsKorean ? korean : english;
        }

        internal static string Format(string english, string korean, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, Text(english, korean), args);
        }

        internal static string FormatWithCurrentCulture(string english, string korean, params object[] args)
        {
            return string.Format(Text(english, korean), args);
        }
    }
}
