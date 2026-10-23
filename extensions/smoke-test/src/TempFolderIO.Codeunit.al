// Regression guard for issues #175, #176 and #181: AL's File functions and .NET System.IO
// must see the same folder under TemporaryPath().
//
// BC builds TemporaryPath() as Path.Combine(userFolder, 'TEMP\'), which ends in a backslash on
// every system. AL's File functions turn '\' into '/', but a DotNet DirectoryInfo or
// Directory.GetFiles got the raw string on Linux and found nothing, and MediaSet.ImportFile
// stored 'TEMP\name.png' as the file name because Path.GetFileName only splits at '/'. The
// PatchTempPath IL patch fixes both and leaves TemporaryPath() as it is.
//
// Issue #183: Windows matches '*.xml' against 'b.XML' and lists in name order. The listing
// methods of Directory and DirectoryInfo are routed through BcLinuxFileListing (the filelisting
// patch) to do the same.
//
// Expected values are what Windows BC 27.5, 28.5 and 29.0 containers reported.
dotnet
{
    assembly("mscorlib")
    {
        type("System.IO.Directory"; BcLinuxDirectory) { }
        type("System.IO.DirectoryInfo"; BcLinuxDirectoryInfo) { }
        type("System.IO.FileInfo"; BcLinuxFileInfo) { }
        type("System.IO.SearchOption"; BcLinuxSearchOption) { }
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
        Folder := TemporaryPath() + 'bclinux-io-' + DelChr(Format(CreateGuid()), '=', '{}-');
        WriteFile(Folder + '\a.xml');
        WriteFile(Folder + '\b.XML');
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
        Found := Files.GetValue(0);
        LibraryAssert.AreEqual(Folder + '\a.xml', Found.FullName, 'FileInfo.FullName');
        LibraryAssert.AreEqual(Folder, Dir.FullName, 'DirectoryInfo.FullName');
        LibraryAssert.AreEqual('|a.xml|b.XML|c.xml', Names, 'GetFiles(*.xml) names in order');
    end;

    [Test]
    procedure DirectoryGetFilesIsCaseInsensitiveAndSorted()
    var
        Directory: DotNet BcLinuxDirectory;
        Folder: Text;
        Files: DotNet Array;
    begin
        Folder := TemporaryPath() + 'bclinux-io-' + DelChr(Format(CreateGuid()), '=', '{}-');
        WriteFile(Folder + '\c.xml');
        WriteFile(Folder + '\B.xml');
        WriteFile(Folder + '\a.XML');
        WriteFile(Folder + '\d.txt');

        Files := Directory.GetFiles(Folder, '*.xml');
        LibraryAssert.AreEqual(3, Files.Length, 'Directory.GetFiles(*.xml) count');
        LibraryAssert.AreEqual(Folder + '\a.XML', Format(Files.GetValue(0)), 'first');
        LibraryAssert.AreEqual(Folder + '\B.xml', Format(Files.GetValue(1)), 'second');
        LibraryAssert.AreEqual(Folder + '\c.xml', Format(Files.GetValue(2)), 'third');
    end;

    // .NET walks a tree folder by folder: the entries of a folder first, then its subfolders in
    // the order they were found. A sort of the full paths would put 'sub1\m.xml' between
    // 'a.xml' and 'z.xml'.
    [Test]
    procedure RecursiveListingListsAFolderBeforeItsSubfolders()
    var
        Directory: DotNet BcLinuxDirectory;
        Option: DotNet BcLinuxSearchOption;
        Folder: Text;
        Files: DotNet Array;
    begin
        Folder := TemporaryPath() + 'bclinux-io-' + DelChr(Format(CreateGuid()), '=', '{}-');
        WriteFile(Folder + '\z.xml');
        WriteFile(Folder + '\a.xml');
        WriteFile(Folder + '\sub2\a.xml');
        WriteFile(Folder + '\sub1\M.xml');
        WriteFile(Folder + '\sub1\deeper\k.xml');

        Files := Directory.GetFiles(Folder, '*.xml', Option.AllDirectories);
        LibraryAssert.AreEqual(5, Files.Length, 'count');
        LibraryAssert.AreEqual(Folder + '\a.xml', Format(Files.GetValue(0)), '1');
        LibraryAssert.AreEqual(Folder + '\z.xml', Format(Files.GetValue(1)), '2');
        LibraryAssert.AreEqual(Folder + '\sub1\M.xml', Format(Files.GetValue(2)), '3');
        LibraryAssert.AreEqual(Folder + '\sub2\a.xml', Format(Files.GetValue(3)), '4');
        LibraryAssert.AreEqual(Folder + '\sub1\deeper\k.xml', Format(Files.GetValue(4)), '5');
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
