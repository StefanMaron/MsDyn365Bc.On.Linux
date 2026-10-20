// Regression guard for issue #135: the bytes Media and MediaSet store for an imported PNG.
//
// BC re-encodes an imported image through System.Drawing. On Windows, GDI+ writes the corpus's
// 1x1 grayscale PNG back as 8-bit RGBA with an sRGB and a gAMA chunk; the expected value below
// is what Windows returned (BC 27.5 and 29.0, official containers). DrawingStub re-encoded it
// with Skia on BC 27 and 28 (still grayscale, an sBIT chunk). BC 29 kept the bytes, because off
// Windows it uses its own NavMediaImageSkia, whose Bytes() returns the source. DrawingStub now
// writes PNG the way GDI+ does, and Patch #40 sends BC 29's bytes through the same encoder.
// Only this image was compared with Windows.
//
// Issue #161: a 1x1 RGBA PNG one byte short (its IDAT holds 4 of the 5 bytes the image needs).
// Windows BC 27.5, 28.5 and 29.0 accept it and store 99 bytes. BC 29 refused it here, because
// Skia cannot decode it for the thumbnail (Patch #42); BC 27 and 28 kept the 69 bytes, because
// GdiPlusPng refused short image data. Only the length was compared with Windows.
codeunit 70053 "BC Linux Media Bytes"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";
        ImportedTxt: Label 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', Locked = true;
        ShortTxt: Label 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4//8/AAX+Av6nNYGEAAAAAElFTkSuQmCC', Locked = true;
        WindowsTxt: Label 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAANSURBVBhXY2BgYPgPAAEEAQBwIGULAAAAAElFTkSuQmCC', Locked = true;

    [Test]
    procedure MediaStoresTheWindowsBytes()
    var
        Probe: Record "BC Linux Media Probe";
        TempBlob: Codeunit "Temp Blob";
        Base64: Codeunit "Base64 Convert";
        InStr: InStream;
        OutStr: OutStream;
    begin
        Probe.DeleteAll();
        WritePng(TempBlob);
        TempBlob.CreateInStream(InStr);
        Probe.Init();
        Probe.Id := 1;
        Probe.Picture.ImportStream(InStr, 'bc-linux probe', 'image/png');
        Probe.Insert();

        Clear(TempBlob);
        TempBlob.CreateOutStream(OutStr);
        Probe.Picture.ExportStream(OutStr);
        TempBlob.CreateInStream(InStr);
        LibraryAssert.AreEqual(WindowsTxt, Base64.ToBase64(InStr), 'Media.ExportStream bytes');
    end;

    [Test]
    procedure MediaSetStoresTheWindowsBytes()
    var
        Probe: Record "BC Linux Media Probe";
        TenantMedia: Record "Tenant Media";
        TempBlob: Codeunit "Temp Blob";
        Base64: Codeunit "Base64 Convert";
        InStr: InStream;
    begin
        Probe.DeleteAll();
        WritePng(TempBlob);
        TempBlob.CreateInStream(InStr);
        Probe.Init();
        Probe.Id := 1;
        Probe.Pictures.ImportStream(InStr, 'bc-linux probe', 'image/png');
        Probe.Insert();

        TenantMedia.Get(Probe.Pictures.Item(1));
        TenantMedia.CalcFields(Content);
        TenantMedia.Content.CreateInStream(InStr);
        LibraryAssert.AreEqual(WindowsTxt, Base64.ToBase64(InStr), 'MediaSet content bytes');
    end;

    [Test]
    procedure ShortPngIsStoredAsWindowsDoes()
    var
        Probe: Record "BC Linux Media Probe";
        TenantMedia: Record "Tenant Media";
        TempBlob: Codeunit "Temp Blob";
        Base64: Codeunit "Base64 Convert";
        InStr: InStream;
        OutStr: OutStream;
    begin
        Probe.DeleteAll();
        TempBlob.CreateOutStream(OutStr);
        Base64.FromBase64(ShortTxt, OutStr);
        TempBlob.CreateInStream(InStr);
        Probe.Init();
        Probe.Id := 1;
        Probe.Picture.ImportStream(InStr, 'bc-linux probe', 'image/png');
        Probe.Insert();

        TenantMedia.Get(Probe.Picture.MediaId());
        TenantMedia.CalcFields(Content);
        LibraryAssert.AreEqual(99, TenantMedia.Content.Length(), 'Tenant Media content length');
    end;

    local procedure WritePng(var TempBlob: Codeunit "Temp Blob")
    var
        Base64: Codeunit "Base64 Convert";
        OutStr: OutStream;
    begin
        TempBlob.CreateOutStream(OutStr);
        Base64.FromBase64(ImportedTxt, OutStr);
    end;
}
