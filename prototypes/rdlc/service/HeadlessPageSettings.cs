using System;
using System.Drawing.Printing;
using System.Reflection;

public static class HeadlessPageSettings
{
    private static FieldInfo Field(string name, Type type)
    {
        var field = typeof(PageSettings).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null || field.FieldType != type)
            throw new PlatformNotSupportedException("Unsupported Mono PageSettings field: " + name);
        return field;
    }

    private static readonly FieldInfo MarginsField = Field("margins", typeof(Margins));
    private static readonly FieldInfo PaperSizeField = Field("paperSize", typeof(PaperSize));
    private static readonly FieldInfo LandscapeField = Field("landscape", typeof(bool));

    // Mono's public getters require an installed printer even for RDLC-defined PDF geometry.
    // Read exactly the state written by BC's original RDLC parser and public setters.
    public static Margins GetMargins(PageSettings page) { return (Margins)MarginsField.GetValue(page); }
    public static PaperSize GetPaperSize(PageSettings page) { return (PaperSize)PaperSizeField.GetValue(page); }
    public static bool GetLandscape(PageSettings page) { return (bool)LandscapeField.GetValue(page); }
}
