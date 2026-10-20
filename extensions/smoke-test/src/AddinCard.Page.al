// A page with a control add-in, for AddinNotCreated.Codeunit.al (issue #133).
page 70050 "BC Linux Addin Card"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            usercontrol(Ctl; "BC Linux Probe Addin")
            {
                ApplicationArea = All;
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(SayHello)
            {
                ApplicationArea = All;
                Caption = 'Say hello';
                ToolTip = 'Calls a procedure of the control add-in.';
                Image = Action;

                trigger OnAction()
                begin
                    CurrPage.Ctl.Say('hello');
                end;
            }
        }
    }
}

controladdin "BC Linux Probe Addin"
{
    Scripts = 'src/addin/ProbeAddin.js';
    RequestedHeight = 50;
    RequestedWidth = 50;

    procedure Say(Message: Text);
}
