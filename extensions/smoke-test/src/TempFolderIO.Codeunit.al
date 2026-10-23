// Regression guard for issues #175, #176 and #181: AL's File functions and .NET System.IO
// must see the same folder under TemporaryPath().
//
// BC builds TemporaryPath() as Path.Combine(userFolder, 'TEMP\'), which ends in a backslash on
// every system. AL's File functions turn '\' into '/', but a DotNet DirectoryInfo or
// Directory.GetFiles got the raw string on Linux and found nothing, and MediaSet.ImportFile
// stored 'TEMP\name.png' as the file name because Path.GetFileName only splits at '/'. The
// PatchTempPath IL patch fixes both and leaves TemporaryPath() as it is.
//
// Expected values are what Windows BC 27.5, 28.5 and 29.0 containers reported, except that the
// files here are all lower case and the order is not checked: Windows matches '*.xml' against
// 'b.XML' and lists in name order, System.IO on Linux does neither (see KNOWN-LIMITATIONS.md).
dotnet
{
    assembly("mscorlib")
    {
        type("System.IO.DirectoryInfo"; BcLinuxDirectoryInfo) { }
        type("System.IO.FileInfo"; BcLinuxFileInfo) { }
    }
}

codeunit 70075 "BC Linux Temp Folder IO"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        LibraryAssert: Codeunit "Library Assert";
        ImportedTxt: Label 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', Locked = true;

    [Test]
    procedure DirectoryInfoListsFilesFileCreateWrote()
    var
        Folder: Text;
        Dir: DotNet BcLinuxDirectoryInfo;
        Files: DotNet Array;
        Found: DotNet BcLinuxFileInfo;
        i: Integer;
        Names: Text;
    begin
        Folder := TemporaryPath() + 'bclinux-io-175';
        WriteFile(Folder + '\a.xml');
        WriteFile(Folder + '\b.xml');
        WriteFile(Folder + '\c.xml');
        WriteFile(Folder + '\d.txt');
        WriteFile(Folder + '\e.xml.bak');

        Dir := Dir.DirectoryInfo(Folder);
        LibraryAssert.IsTrue(Dir.Exists, 'DirectoryInfo.Exists');
        Files := Dir.GetFiles('*.xml');
        LibraryAssert.AreEqual(3, Files.Length, 'GetFiles(*.xml) count');
        for i := 0 to Files.Length - 1 do begin
            Found := Files.GetValue(i);
            Names += '|' + Found.Name;
        end;
        LibraryAssert.IsTrue(Names.Contains('|a.xml') and Names.Contains('|b.xml') and Names.Contains('|c.xml'), 'file names ' + Names);
    end;

    [Test]
    procedure TemporaryPathStillEndsInABackslash()
    var
        Temp: Text;
    begin
        Temp := TemporaryPath();
        LibraryAssert.IsTrue(Temp.EndsWith('TEMP\'), 'TemporaryPath() ends with TEMP\, got ' + Temp);
    end;

    [Test]
    procedure MediaSetImportFileStoresTheBareFileName()
    var
        Probe: Record "BC Linux Media Probe";
        TenantMedia: Record "Tenant Media";
        FileName: Text;
    begin
        Probe.DeleteAll();
        FileName := TemporaryPath() + 'bclinux-mediaset.png';
        WritePng(FileName);

        Probe.Init();
        Probe.Id := 1;
        Probe.Pictures.ImportFile(FileName, 'bc-linux probe');
        Probe.Insert();

        TenantMedia.Get(Probe.Pictures.Item(1));
        LibraryAssert.AreEqual('bclinux-mediaset.png', TenantMedia."File Name", 'Tenant Media File Name');
    end;

    local procedure WriteFile(FileName: Text)
    var
        OutFile: File;
        OutStr: OutStream;
    begin
        OutFile.Create(FileName);
        OutFile.CreateOutStream(OutStr);
        OutStr.WriteText('<x/>');
        OutFile.Close();
    end;

    local procedure WritePng(FileName: Text)
    var
        TempBlob: Codeunit "Temp Blob";
        Base64: Codeunit "Base64 Convert";
        InStr: InStream;
        OutStr: OutStream;
        OutFile: File;
        FileOutStr: OutStream;
    begin
        TempBlob.CreateOutStream(OutStr);
        Base64.FromBase64(ImportedTxt, OutStr);
        TempBlob.CreateInStream(InStr);
        OutFile.Create(FileName);
        OutFile.CreateOutStream(FileOutStr);
        CopyStream(FileOutStr, InStr);
        OutFile.Close();
    end;
}
