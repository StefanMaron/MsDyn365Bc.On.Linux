using System;
using System.Globalization;

public static class CultureNameProbe
{
    public static void Main()
    {
        foreach (string name in new[] { "de-DE_phoneb", "es-ES_tradnl", "zh-CHS", "zh-CHT" })
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(name);
                Console.WriteLine("{0} -> {1} -> {2} -> {3}", name, culture.Name, culture.CompareInfo.Name, culture.IetfLanguageTag);
            }
            catch (CultureNotFoundException)
            {
                Console.WriteLine(name + " -> rejected by runtime");
            }
        }
    }
}
