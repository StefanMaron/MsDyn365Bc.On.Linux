codeunit 70072 "BC Linux Format Region Log"
{
    SingleInstance = true;

    var
        CurrentRegion: Text;
        Formatted: Text;

    procedure SetRegion(NewRegion: Text)
    begin
        CurrentRegion := NewRegion;
        Formatted := '';
    end;

    procedure Region(): Text
    begin
        exit(CurrentRegion);
    end;

    procedure Record(Value: Text)
    begin
        Formatted := Value;
    end;

    procedure Result(): Text
    begin
        exit(Formatted);
    end;
}
