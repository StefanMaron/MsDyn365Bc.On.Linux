page 99850 "WebClient AddIn Test Page"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = Administration;
    Caption = 'WebClient AddIn Test';

    layout
    {
        area(Content)
        {
            usercontrol(TestAddin; WebClientAddinTest)
            {
                ApplicationArea = All;

                trigger ControlAddInReady(controlId: Text)
                var
                    LogEntry: Record "WebClient AddIn Test Log";
                begin
                    LogEntry.Init();
                    LogEntry.Message := CopyStr('ControlAddInReady fired, controlId=' + controlId, 1, 250);
                    LogEntry.Insert(true);

                    CurrPage.TestAddin.Ping('hello-from-al');
                end;

                trigger Pong(message: Text)
                var
                    LogEntry: Record "WebClient AddIn Test Log";
                begin
                    LogEntry.Init();
                    LogEntry.Message := CopyStr('Pong received: ' + message, 1, 250);
                    LogEntry.Insert(true);
                end;
            }
        }
    }
}
