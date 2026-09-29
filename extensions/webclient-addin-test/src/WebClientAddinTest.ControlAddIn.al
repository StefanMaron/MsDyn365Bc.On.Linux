controladdin WebClientAddinTest
{
    Scripts = 'Resources/WebClientAddinTest/script.js';
    StartupScript = 'Resources/WebClientAddinTest/script.js';
    RequestedHeight = 100;
    RequestedWidth = 300;
    VerticalStretch = true;
    HorizontalStretch = true;

    event ControlAddInReady(controlId: Text);
}
