// Runs as the add-in's StartupScript. The web client gives every control add-in
// a single <div id="controlAddIn"> inside its own iframe.
(function () {
    var el = document.getElementById("controlAddIn") || document.body;
    el.style.background = "#2ecc71";
    el.style.color = "#ffffff";
    el.style.fontSize = "20px";
    el.style.display = "flex";
    el.style.alignItems = "center";
    el.style.justifyContent = "center";
    el.style.height = "100%";
    el.innerText = "RENDERED OK";
    if (window.Microsoft && Microsoft.Dynamics && Microsoft.Dynamics.NAV &&
        Microsoft.Dynamics.NAV.InvokeExtensibilityMethod) {
        Microsoft.Dynamics.NAV.InvokeExtensibilityMethod("ControlAddInReady", ["controlAddIn"]);
    }
})();
