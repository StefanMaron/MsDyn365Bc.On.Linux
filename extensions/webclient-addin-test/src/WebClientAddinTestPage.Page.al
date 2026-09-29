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
                begin
                end;
            }
        }
    }
}
