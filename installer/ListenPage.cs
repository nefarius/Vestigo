using System.Xml.Linq;

using Vestigo.Installer.Actions.Logic;

using WixSharp;

namespace Vestigo.Installer;

/// <summary>
///     Adds the listen/security page, its managed custom actions, the secure properties, and the conditional firewall
///     rule to the MSI. The page is a native WiX dialog; only the token generator, existing-settings reader, and the
///     elevated settings merge run as managed (net48 DTF) custom actions.
/// </summary>
static class ListenPage
{
    const string WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
    const string BinaryId = "VestigoActionsBin";
    const string FirewallComponentId = "VestigoFirewallRule";
    const string DialogId = "VestigoListenDlg";
    const string ErrorDialogId = "VestigoInputErrorDlg";
    const string NotRemoving = "NOT (REMOVE~=\"ALL\")";

    static readonly XNamespace Wix = WixNamespace;
    static readonly XNamespace Fire = WixExtension.FireNamespace;

    public static void Apply(Project project, string actionsPackagePath)
    {
        WixExtension.Fire.PreferredVersion = "5.0.2";
        project.Include(WixExtension.Fire);
        project.WixSourceGenerated += document => Generate(document, actionsPackagePath);
    }

    static void Generate(XDocument document, string actionsPackagePath)
    {
        XElement package = document.Root!.Element(Wix + "Package")
            ?? throw new InvalidOperationException("The generated WiX source has no Package element.");

        package.Add(
            new XElement(Wix + "Binary", new XAttribute("Id", BinaryId), new XAttribute("SourceFile", actionsPackagePath)),
            ImmediateAction("VestigoLoadSettings"),
            ImmediateAction("VestigoApplyUiChoice"),
            ImmediateAction("VestigoPrepareWriteSettings"),
            new XElement(
                Wix + "CustomAction",
                new XAttribute("Id", "VestigoWriteSettings"),
                new XAttribute("BinaryRef", BinaryId),
                new XAttribute("DllEntry", "VestigoWriteSettings"),
                new XAttribute("Execute", "deferred"),
                new XAttribute("Impersonate", "no"),
                new XAttribute("Return", "check")));

        // Public, secure properties so silent installs (and the UI-to-server hand-off) can carry the choices.
        // Hidden keeps the token out of the MSI log by adding it to MsiHiddenProperties.
        foreach (string name in new[] { "VESTIGO_LISTEN_MODE", "VESTIGO_AUTH_MODE", "VESTIGO_PORT" }
                     .Concat(ModuleSelection.All.Select(m => m.PropertyName)))
        {
            package.Add(new XElement(Wix + "Property", new XAttribute("Id", name), new XAttribute("Secure", "yes")));
        }

        foreach (string name in new[] { "VESTIGO_API_TOKEN", "VESTIGO_TOKEN_SUGGESTED" })
        {
            package.Add(new XElement(
                Wix + "Property",
                new XAttribute("Id", name),
                new XAttribute("Secure", "yes"),
                new XAttribute("Hidden", "yes")));
        }

        // CustomActionData of the deferred action carries the token.
        package.Add(new XElement(Wix + "Property", new XAttribute("Id", "VestigoWriteSettings"), new XAttribute("Hidden", "yes")));
        package.Add(new XElement(Wix + "Property", new XAttribute("Id", "VESTIGO_UI_MODE"), new XAttribute("Value", "4")));

        package.Add(new XElement(
            Wix + "InstallExecuteSequence",
            Sequence("VestigoLoadSettings", "After", "AppSearch", NotRemoving),
            Sequence("VestigoPrepareWriteSettings", "Before", "VestigoWriteSettings", NotRemoving),
            Sequence("VestigoWriteSettings", "Before", "StartServices", NotRemoving)));
        package.Add(new XElement(
            Wix + "InstallUISequence",
            Sequence("VestigoLoadSettings", "After", "AppSearch", NotRemoving)));

        AddFirewallComponent(package);
        ConfigureFeatures(package);
        package.Add(BuildUi());
    }

    static XElement ImmediateAction(string name) =>
        new(
            Wix + "CustomAction",
            new XAttribute("Id", name),
            new XAttribute("BinaryRef", BinaryId),
            new XAttribute("DllEntry", name),
            new XAttribute("Execute", "immediate"),
            new XAttribute("Return", "check"));

    static XElement Sequence(string action, string placement, string anchor, string condition) =>
        new(
            Wix + "Custom",
            new XAttribute("Action", action),
            new XAttribute(placement, anchor),
            new XAttribute("Condition", condition));

    static void AddFirewallComponent(XElement package)
    {
        XElement feature = package.Elements(Wix + "Feature")
                .FirstOrDefault(f => (string?)f.Attribute("Id") == ModuleSelection.ServerFeature)
            ?? throw new InvalidOperationException("The generated WiX source has no Feature element.");

        // Registry-only component so Windows Installer has a key path. The rule exists only for the "any" modes
        // and is removed with the component on uninstall, upgrade to localhost, or repair.
        XElement component = new(
            Wix + "Component",
            new XAttribute("Id", FirewallComponentId),
            new XAttribute("Directory", "TARGETDIR"),
            new XAttribute("Guid", "5d0f8b3a-2e64-4c19-9a7b-31c8e6f2d405"),
            new XAttribute("Condition", "VESTIGO_LISTEN_MODE=\"any\""),
            new XElement(
                Wix + "RegistryValue",
                new XAttribute("Root", "HKLM"),
                new XAttribute("Key", @"SOFTWARE\Nefarius Software Solutions\Vestigo"),
                new XAttribute("Name", "FirewallPort"),
                new XAttribute("Type", "string"),
                new XAttribute("Value", "[VESTIGO_PORT]"),
                new XAttribute("KeyPath", "yes")),
            new XElement(
                Fire + "FirewallException",
                new XAttribute("Id", "VestigoFirewallException"),
                new XAttribute("Name", "Vestigo MCP Server"),
                new XAttribute("Description", "Inbound TCP port for the Vestigo MCP server."),
                new XAttribute("Port", "[VESTIGO_PORT]"),
                new XAttribute("Protocol", "tcp"),
                new XAttribute("Scope", "any"),
                new XAttribute("Profile", "all"),
                new XAttribute("IgnoreFailure", "yes")));

        package.Parent!.Add(new XElement(
            Wix + "Fragment",
            new XElement(Wix + "ComponentGroup", new XAttribute("Id", "VestigoFirewallGroup"), component)));
        feature.Add(new XElement(Wix + "ComponentGroupRef", new XAttribute("Id", "VestigoFirewallGroup")));
    }

    /// <summary>
    ///     Titles, descriptions, install levels, and the existing-settings conditions of the module features. Default-off
    ///     modules sit at level 1000 (unchecked but selectable). VESTIGO_MOD_* ("1" or "0", set from the existing
    ///     settings or on the command line) override the default level.
    /// </summary>
    static void ConfigureFeatures(XElement package)
    {
        XElement server = package.Elements(Wix + "Feature")
            .First(f => (string?)f.Attribute("Id") == ModuleSelection.ServerFeature);
        server.SetAttributeValue("Title", "Vestigo MCP Server");
        server.SetAttributeValue("AllowAbsent", "no");
        server.SetAttributeValue("Display", "expand");

        foreach (ModuleDefinition module in ModuleSelection.All)
        {
            XElement? feature = server.Descendants(Wix + "Feature")
                .FirstOrDefault(f => (string?)f.Attribute("Id") == module.Name);
            if (feature is null)
            {
                throw new InvalidOperationException($"The generated WiX source has no feature '{module.Name}'.");
            }

            feature.SetAttributeValue("Title", module.Title);
            feature.SetAttributeValue("Description", module.Description);
            feature.SetAttributeValue("Level", module.DefaultOn ? "1" : "1000");
            // WixSharp marks every feature expanded; only features that have children should be.
            feature.SetAttributeValue("Display", ModuleSelection.All.Any(m => m.Parent == module.Name) ? "expand" : "collapse");

            feature.Add(
                new XElement(
                    Wix + "Level",
                    new XAttribute("Value", "1"),
                    new XAttribute("Condition", $"{module.PropertyName}=\"1\"")),
                new XElement(
                    Wix + "Level",
                    new XAttribute("Value", "1000"),
                    new XAttribute("Condition", $"{module.PropertyName}=\"0\"")));
        }
    }

    static XElement BuildUi()
    {
        const string hideForNoToken = "VESTIGO_UI_MODE=\"1\" OR VESTIGO_UI_MODE=\"3\"";

        XElement dialog = new(
            Wix + "Dialog",
            new XAttribute("Id", DialogId),
            new XAttribute("Width", "370"),
            new XAttribute("Height", "270"),
            new XAttribute("Title", "[ProductName] Setup"),
            Control("BannerBitmap", "Bitmap", 0, 0, 370, 44, "!(loc.LicenseAgreementDlgBannerBitmap)", ("TabSkip", "no")),
            Control("BannerLine", "Line", 0, 44, 370, 0, null),
            Control("BottomLine", "Line", 0, 234, 370, 0, null),
            Control("Title", "Text", 15, 6, 340, 15, "{\\WixUI_Font_Title}Server access", ("Transparent", "yes"), ("NoPrefix", "yes")),
            Control(
                "Description",
                "Text",
                25,
                23,
                340,
                15,
                "Choose where the Vestigo MCP server listens and whether it needs an API token.",
                ("Transparent", "yes"),
                ("NoPrefix", "yes")),
            RadioGroup(),
            Control("PortLabel", "Text", 20, 130, 40, 12, "Port:", ("Transparent", "yes"), ("NoPrefix", "yes")),
            Control("PortEdit", "Edit", 62, 127, 50, 15, null, ("Property", "VESTIGO_PORT"), ("Text", "{5}")),
            WithHide(Control("TokenLabel", "Text", 20, 150, 40, 12, "Token:", ("Transparent", "yes"), ("NoPrefix", "yes")), hideForNoToken),
            WithHide(Control("TokenEdit", "Edit", 62, 147, 288, 15, null, ("Property", "VESTIGO_API_TOKEN"), ("Text", "{200}")), hideForNoToken),
            WithShow(
                Control(
                    "OpenWarning",
                    "Text",
                    20,
                    150,
                    330,
                    30,
                    "{\\WixUI_Font_Title}No token: anyone who can reach this PC on the network can use the server and every module you enable next, including processes, services, drivers and uninstalls.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                "VESTIGO_UI_MODE=\"3\""),
            WithShow(
                Control(
                    "LocalOpenNote",
                    "Text",
                    20,
                    150,
                    330,
                    24,
                    "{\\WixUI_Font_Title}No token: any program on this PC can use every module you enable next, including processes, services, drivers and uninstalls.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                "VESTIGO_UI_MODE=\"1\""),
            Control(
                "Note",
                "Text",
                20,
                182,
                330,
                48,
                "A suggested token is filled in; you can replace it. The token is stored in clear text in %ProgramData%\\Nefarius Software Solutions\\Vestigo\\appsettings.json, readable only by SYSTEM and Administrators. " +
                "Modes that listen on any address add an inbound Windows Firewall rule for the port, removed on uninstall.",
                ("Transparent", "yes"),
                ("NoPrefix", "yes")),
            Control("Back", "PushButton", 180, 243, 56, 17, "!(loc.WixUIBack)"),
            Control("Next", "PushButton", 236, 243, 56, 17, "!(loc.WixUINext)", ("Default", "yes")),
            Control("Cancel", "PushButton", 304, 243, 56, 17, "!(loc.WixUICancel)", ("Cancel", "yes")));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Next").Add(
            Publish("DoAction", "VestigoApplyUiChoice", 1, null),
            Publish("SpawnDialog", ErrorDialogId, 2, "VESTIGO_INPUT_ERROR <> \"\""),
            Publish("NewDialog", "CustomizeDlg", 3, "VESTIGO_INPUT_ERROR = \"\""));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Back").Add(
            Publish("NewDialog", "LicenseAgreementDlg", 1, null));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Cancel").Add(
            Publish("SpawnDialog", "CancelDlg", 1, null));

        XElement errorDialog = new(
            Wix + "Dialog",
            new XAttribute("Id", ErrorDialogId),
            new XAttribute("Width", "260"),
            new XAttribute("Height", "85"),
            new XAttribute("Title", "Vestigo settings"),
            Control("Message", "Text", 48, 15, 195, 40, "[VESTIGO_INPUT_ERROR]", ("NoPrefix", "yes")),
            Control("Ok", "PushButton", 102, 62, 56, 17, "!(loc.WixUIOK)", ("Default", "yes"), ("Cancel", "yes")));
        errorDialog.Elements(Wix + "Control").First().Add(Publish("EndDialog", "Return", 1, null));

        // The license page continues to this page, and Back from the feature page returns here.
        // Order 10 overrides the stock WixUI publish events for the same controls.
        return new XElement(
            Wix + "UI",
            dialog,
            errorDialog,
            new XElement(Wix + "DialogRef", new XAttribute("Id", "CancelDlg")),
            PublishOn("LicenseAgreementDlg", "Next", "NewDialog", DialogId, 10, "LicenseAccepted = \"1\""),
            PublishOn("CustomizeDlg", "Back", "NewDialog", DialogId, 10, "NOT Installed"));
    }

    static XElement RadioGroup()
    {
        XElement group = new(
            Wix + "RadioButtonGroup",
            new XAttribute("Property", "VESTIGO_UI_MODE"),
            Radio("1", 0, "Localhost only, no API token"),
            Radio("2", 17, "Localhost only, require an API token"),
            Radio("3", 34, "Any network address, no API token (not recommended)"),
            Radio("4", 51, "Any network address, require an API token (recommended)"));
        XElement control = Control("ModeChoice", "RadioButtonGroup", 20, 52, 330, 70, null, ("Property", "VESTIGO_UI_MODE"));
        control.Add(group);
        return control;
    }

    static XElement Radio(string value, int y, string text) =>
        new(
            Wix + "RadioButton",
            new XAttribute("Value", value),
            new XAttribute("X", 0),
            new XAttribute("Y", y),
            new XAttribute("Width", 330),
            new XAttribute("Height", 17),
            new XAttribute("Text", text));

    static XElement Control(
        string id,
        string type,
        int x,
        int y,
        int width,
        int height,
        string? text,
        params (string Name, string Value)[] extra)
    {
        XElement control = new(
            Wix + "Control",
            new XAttribute("Id", id),
            new XAttribute("Type", type),
            new XAttribute("X", x),
            new XAttribute("Y", y),
            new XAttribute("Width", width),
            new XAttribute("Height", height));
        if (text is not null)
        {
            control.Add(new XAttribute("Text", text));
        }

        foreach ((string name, string value) in extra)
        {
            control.Add(new XAttribute(name, value));
        }

        return control;
    }

    static XElement WithHide(XElement control, string condition)
    {
        control.Add(new XAttribute("HideCondition", condition));
        return control;
    }

    static XElement WithShow(XElement control, string condition)
    {
        control.Add(new XAttribute("ShowCondition", condition));
        return control;
    }

    static XElement Publish(string eventName, string value, int order, string? condition)
    {
        XElement publish = new(
            Wix + "Publish",
            new XAttribute("Event", eventName),
            new XAttribute("Value", value),
            new XAttribute("Order", order));
        if (condition is not null)
        {
            publish.Add(new XAttribute("Condition", condition));
        }

        return publish;
    }

    static XElement PublishOn(string dialog, string control, string eventName, string value, int order, string condition) =>
        new(
            Wix + "Publish",
            new XAttribute("Dialog", dialog),
            new XAttribute("Control", control),
            new XAttribute("Event", eventName),
            new XAttribute("Value", value),
            new XAttribute("Order", order),
            new XAttribute("Condition", condition));
}