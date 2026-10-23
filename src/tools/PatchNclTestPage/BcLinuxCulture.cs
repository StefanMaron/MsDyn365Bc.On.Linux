using System;
using System.Collections.Concurrent;
using System.Globalization;

/// <summary>
/// Source of the culture helper the "culture" patch copies into BC's assemblies (issue #113).
///
/// This class is never called from the patcher. PatchCulture reads this assembly with Cecil and
/// copies every method of it into Nav.Ncl.dll / Nav.Types.dll, so it has to stay self-contained:
/// static methods that call only the framework, no lambdas, no closures, no static fields, no
/// try/catch (nothing a plain IL copy cannot carry over).
///
/// What differs between a Windows tier (NLS culture data) and this one (ICU/CLDR):
///  * Every culture: ICU 72+ puts a narrow no-break space (U+202F) before AM/PM in time
///    patterns; NLS uses a plain space. "2:30:05 PM" came out with a character no Windows
///    tier writes, and showed up in Format(), report datasets, CSV length and page values.
///  * da-DK: the short date is dd-MM-yyyy on Windows and dd.MM.y in CLDR.
///  * de-DE, es-ES: the abbreviated month names ("MMM", which Format(Date, 0, 7) uses) differ
///    (issue #134). Windows: Jan Feb Mrz ... / ene. feb. mar. ...; ICU: Jan. Feb. März ... /
///    ene feb mar ... sept .... Only the two cultures whose Windows values were measured are set.
///  * Report FormatRegion and Format() data (issue #157): the da-DK time separator (':' on
///    Windows, '.' in CLDR), the es-ES and nl-NL short dates (dd/MM/yyyy and d-M-yyyy), the
///    en-GB long date (dd MMMM yyyy), the sv-SE long date ('den ' d MMMM yyyy) and the sv-SE
///    and nb-NO minus sign (U+002D, CLDR U+2212).
///  * fr-FR, issues #167 and #174: the number group separator is U+00A0 on a Windows BC 27.5, 28.5
///    and 29.0 container and U+202F in CLDR.
///  * An unknown region such as zz-ZZ (issue #167): the dataset's typed dates are zero-padded
///    (01/31/2025) on a Windows container and not (1/31/2025) here. The short date of a custom
///    culture (LCID 4096) whose region is ZZ, the CLDR "unknown region", is set to MM/dd/yyyy.
/// The rest of the ICU/NLS gap (collation, special-casing) is not reachable from here.
/// </summary>
internal static class BcLinuxCulture
{
    internal static CultureInfo Fix(CultureInfo culture)
    {
        if (culture == null || culture.IsNeutralCulture)
            return culture;

        // One fixed clone per culture name. The cache lives in the AppDomain's data slots because
        // a plain IL copy cannot carry a static field over. Two threads racing on the first call
        // each build a cache and one wins; both answers are equal, so nothing depends on which.
        string name = culture.Name;
        ConcurrentDictionary<string, CultureInfo> cache =
            (ConcurrentDictionary<string, CultureInfo>)AppDomain.CurrentDomain.GetData("BcLinux.WindowsCulture");
        if (cache == null)
        {
            cache = new ConcurrentDictionary<string, CultureInfo>();
            AppDomain.CurrentDomain.SetData("BcLinux.WindowsCulture", cache);
        }
        CultureInfo cached;
        if (cache.TryGetValue(name, out cached))
            return cached;

        CultureInfo result = Build(culture);
        cache[name] = result;
        return result;
    }

    private static CultureInfo Build(CultureInfo culture)
    {
        DateTimeFormatInfo source = culture.DateTimeFormat;
        bool danish = culture.Name.StartsWith("da", StringComparison.Ordinal);
        string[] months = WindowsAbbreviatedMonths(culture.Name);
        if (!HasNarrowSpace(source) && !danish && months == null && !HasWindowsData(culture.Name))
            return culture;

        CultureInfo copy = (CultureInfo)culture.Clone();
        DateTimeFormatInfo d = copy.DateTimeFormat;
        d.ShortTimePattern = PlainSpace(source.ShortTimePattern);
        d.LongTimePattern = PlainSpace(source.LongTimePattern);
        d.ShortDatePattern = PlainSpace(source.ShortDatePattern);
        d.LongDatePattern = PlainSpace(source.LongDatePattern);
        d.FullDateTimePattern = PlainSpace(source.FullDateTimePattern);
        d.YearMonthPattern = PlainSpace(source.YearMonthPattern);
        d.MonthDayPattern = PlainSpace(source.MonthDayPattern);
        d.AMDesignator = PlainSpace(source.AMDesignator);
        d.PMDesignator = PlainSpace(source.PMDesignator);
        if (danish)
        {
            d.DateSeparator = "-";
            d.ShortDatePattern = source.ShortDatePattern.Replace('.', '-');
        }
        if (months != null)
        {
            d.AbbreviatedMonthNames = months;
            d.AbbreviatedMonthGenitiveNames = months;
        }
        ApplyWindowsData(copy);
        return CultureInfo.ReadOnly(copy);
    }

    /// <summary>
    /// Same rules for a culture BC has just constructed with `new CultureInfo(...)`. That instance
    /// is writable and BC fills in some of its patterns itself (NavSession.GetFormattingCulture),
    /// so it is changed in place and returned rather than cloned.
    /// </summary>
    internal static CultureInfo FixWritable(CultureInfo culture)
    {
        if (culture == null || culture.IsNeutralCulture || culture.IsReadOnly)
            return culture;

        DateTimeFormatInfo d = culture.DateTimeFormat;
        d.ShortTimePattern = PlainSpace(d.ShortTimePattern);
        d.LongTimePattern = PlainSpace(d.LongTimePattern);
        d.ShortDatePattern = PlainSpace(d.ShortDatePattern);
        d.LongDatePattern = PlainSpace(d.LongDatePattern);
        d.FullDateTimePattern = PlainSpace(d.FullDateTimePattern);
        d.YearMonthPattern = PlainSpace(d.YearMonthPattern);
        d.MonthDayPattern = PlainSpace(d.MonthDayPattern);
        d.AMDesignator = PlainSpace(d.AMDesignator);
        d.PMDesignator = PlainSpace(d.PMDesignator);
        if (culture.Name.StartsWith("da", StringComparison.Ordinal))
        {
            d.DateSeparator = "-";
            d.ShortDatePattern = d.ShortDatePattern.Replace('.', '-');
        }
        string[] months = WindowsAbbreviatedMonths(culture.Name);
        if (months != null)
        {
            d.AbbreviatedMonthNames = months;
            d.AbbreviatedMonthGenitiveNames = months;
        }
        ApplyWindowsData(culture);
        return culture;
    }

    /// <summary>
    /// The Windows (NLS) abbreviated month names for a culture whose ICU names differ, or null.
    /// Values from an official Windows BC container (27.5 and 29.0), issue #134. A new array on
    /// every call: the copied IL cannot hold a static field. No string switch either: the compiler
    /// turns one into a call to a hash helper that lives outside this class.
    /// </summary>
    private static string[] WindowsAbbreviatedMonths(string name)
    {
        if (string.Equals(name, "de-DE", StringComparison.Ordinal))
            return new string[] { "Jan", "Feb", "Mrz", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez", "" };
        if (string.Equals(name, "es-ES", StringComparison.Ordinal))
            return new string[] { "ene.", "feb.", "mar.", "abr.", "may.", "jun.", "jul.", "ago.", "sep.", "oct.", "nov.", "dic.", "" };
        return null;
    }

    /// <summary>
    /// True for a culture whose Windows (NLS) date, time or number data differs from ICU in a way
    /// <see cref="ApplyWindowsData"/> sets (issue #157).
    /// </summary>
    private static bool HasWindowsData(string name)
    {
        return string.Equals(name, "da-DK", StringComparison.Ordinal)
            || string.Equals(name, "es-ES", StringComparison.Ordinal)
            || string.Equals(name, "en-GB", StringComparison.Ordinal)
            || string.Equals(name, "nl-NL", StringComparison.Ordinal)
            || string.Equals(name, "sv-SE", StringComparison.Ordinal)
            || string.Equals(name, "nb-NO", StringComparison.Ordinal)
            || (string.Equals(name, "fr-FR", StringComparison.Ordinal))
            || name.EndsWith("-ZZ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Windows (NLS) values where they differ from ICU, set on a writable culture (issue #157):
    /// the da-DK time separator, the es-ES and nl-NL short dates, the en-GB and sv-SE long dates
    /// and the sv-SE and nb-NO minus sign (ICU uses U+2212). Measured on a Windows BC 28.5
    /// container. Runs after the narrow-space and da-DK date fixes above.
    /// </summary>
    private static void ApplyWindowsData(CultureInfo culture)
    {
        string name = culture.Name;
        DateTimeFormatInfo d = culture.DateTimeFormat;
        NumberFormatInfo n = culture.NumberFormat;
        if (string.Equals(name, "da-DK", StringComparison.Ordinal))
        {
            d.TimeSeparator = ":";
            d.ShortTimePattern = d.ShortTimePattern.Replace("HH.mm", "HH:mm");
            d.LongTimePattern = d.LongTimePattern.Replace("HH.mm.ss", "HH:mm:ss").Replace("HH.mm", "HH:mm");
            d.FullDateTimePattern = d.FullDateTimePattern.Replace("HH.mm.ss", "HH:mm:ss").Replace("HH.mm", "HH:mm");
        }
        else if (string.Equals(name, "es-ES", StringComparison.Ordinal))
        {
            d.ShortDatePattern = "dd/MM/yyyy";
        }
        else if (string.Equals(name, "en-GB", StringComparison.Ordinal))
        {
            d.LongDatePattern = "dd MMMM yyyy";
            d.FullDateTimePattern = "dd MMMM yyyy HH:mm:ss";
        }
        else if (string.Equals(name, "nl-NL", StringComparison.Ordinal))
        {
            d.ShortDatePattern = "d-M-yyyy";
        }
        else if (string.Equals(name, "sv-SE", StringComparison.Ordinal))
        {
            n.NegativeSign = "-";
            d.LongDatePattern = "'den 'd MMMM yyyy";
            d.FullDateTimePattern = "'den 'd MMMM yyyy HH:mm:ss";
        }
        else if (string.Equals(name, "nb-NO", StringComparison.Ordinal))
        {
            n.NegativeSign = "-";
        }
        else if (string.Equals(name, "fr-FR", StringComparison.Ordinal))
        {
            n.NumberGroupSeparator = "\u00A0";
        }
        else if (culture.LCID == 4096 && name.EndsWith("-ZZ", StringComparison.OrdinalIgnoreCase))
        {
            d.ShortDatePattern = "MM/dd/yyyy";
        }
    }

    private static bool HasNarrowSpace(DateTimeFormatInfo d)
    {
        return d.ShortTimePattern.IndexOf(' ') >= 0
            || d.LongTimePattern.IndexOf(' ') >= 0
            || d.FullDateTimePattern.IndexOf(' ') >= 0
            || d.AMDesignator.IndexOf(' ') >= 0
            || d.PMDesignator.IndexOf(' ') >= 0;
    }

    private static string PlainSpace(string value)
    {
        return value == null ? null : value.Replace(' ', ' ');
    }
}
