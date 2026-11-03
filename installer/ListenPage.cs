using System.Xml.Linq;

using Vestigo.Installer.Actions.Logic;

using WixSharp;

namespace Vestigo.Installer;

/// <summary>
///     Adds the access, authentication, and file-system roots pages, their managed custom actions, the secure
///     properties, and the conditional firewall rule to the MSI. The pages are native WiX dialogs; only the token
///     generator, existing-settings reader, and the elevated settings merge run as managed (net48 DTF) custom actions.
/// </summary>
static class ListenPage
{
    const string WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
    internal const string BinaryId = "VestigoActionsBin";
    const string FirewallComponentId = "VestigoFirewallRule";
    const string AccessDialogId = "VestigoAccessDlg";
    const string AuthDialogId = "VestigoAuthDlg";
    const string RootsDialogId = "VestigoRootsDlg";
    const string RootsAccessDialogId = "VestigoRootsAccessDlg";
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
        package.Add(
            SecureProperty("VESTIGO_LISTEN_MODE", null),
            SecureProperty("VESTIGO_AUTH_MODE", null),
            SecureProperty("VESTIGO_PORT", null),
            SecureProperty("VESTIGO_ROOTS_MODE", null),
            SecureProperty("VESTIGO_ROOTS_ACCESS", null));
        foreach (string name in ModuleSelection.All.Select(m => m.PropertyName))
        {
            package.Add(SecureProperty(name, null));
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
    ///     Titles, descriptions, install levels, and the existing-settings conditions of the optional updater and
    ///     module features. The updater is level 1 (selected). Default-off modules sit at level 1000 (unchecked but
    ///     selectable). VESTIGO_MOD_* ("1" or "0", set from the existing settings or on the command line) override the
    ///     default module level.
    /// </summary>
    static void ConfigureFeatures(XElement package)
    {
        XElement server = package.Elements(Wix + "Feature")
            .First(f => (string?)f.Attribute("Id") == ModuleSelection.ServerFeature);
        server.SetAttributeValue("Title", "Vestigo MCP Server");
        server.SetAttributeValue("AllowAbsent", "no");
        server.SetAttributeValue("Display", "expand");

        XElement? updater = server.Descendants(Wix + "Feature")
            .FirstOrDefault(f => (string?)f.Attribute("Id") == Program.UpdaterFeature);
        if (updater is null)
        {
            throw new InvalidOperationException($"The generated WiX source has no feature '{Program.UpdaterFeature}'.");
        }

        updater.SetAttributeValue("Title", "Automatic updates");
        updater.SetAttributeValue(
            "Description",
            "Installs the Vestigo updater and registers it for the installing user. Uncheck to skip automatic updates.");
        updater.SetAttributeValue("Level", "1");
        updater.SetAttributeValue("Display", "collapse");

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
        const string noToken = "VESTIGO_AUTH_MODE=\"" + ListenSettings.AuthNone + "\"";
        const string listenAny = "VESTIGO_LISTEN_MODE=\"" + ListenSettings.ListenAny + "\"";
        const string listenLocalhost = "VESTIGO_LISTEN_MODE=\"" + ListenSettings.ListenLocalhost + "\"";

        XElement accessDialog = WizardDialog(
            AccessDialogId,
            "Server access",
            "Choose where the Vestigo MCP server listens.",
            ListenRadioGroup(),
            Control("PortLabel", "Text", 20, 110, 40, 12, "Port:", ("Transparent", "yes"), ("NoPrefix", "yes")),
            Control("PortEdit", "Edit", 62, 107, 50, 15, null, ("Property", "VESTIGO_PORT"), ("Text", "{5}")),
            WithShow(
                Control(
                    "AnyNote",
                    "Text",
                    20,
                    136,
                    330,
                    36,
                    "Any network address adds an inbound Windows Firewall rule for this port. The rule is removed on uninstall.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                listenAny,
                startHidden: false),
            WithShow(
                Control(
                    "LocalNote",
                    "Text",
                    20,
                    136,
                    330,
                    24,
                    "The server will only accept connections from this PC. No firewall rule is added.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                listenLocalhost));
        WireNavigation(accessDialog, "LicenseAgreementDlg", AuthDialogId);

        XElement authDialog = WizardDialog(
            AuthDialogId,
            "Authentication",
            "Choose whether the server requires an API token.",
            AuthRadioGroup(),
            WithHide(Control("TokenLabel", "Text", 20, 110, 40, 12, "Token:", ("Transparent", "yes"), ("NoPrefix", "yes")), noToken),
            WithHide(Control("TokenEdit", "Edit", 62, 107, 288, 15, null, ("Property", "VESTIGO_API_TOKEN"), ("Text", "{200}")), noToken),
            WithHide(
                Control(
                    "TokenNote",
                    "Text",
                    20,
                    130,
                    330,
                    70,
                    "A suggested token is filled in; you can replace it. Stored in clear text under %ProgramData%, readable only by SYSTEM and Administrators.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                noToken),
            WithShow(
                Control(
                    "OpenWarning",
                    "Text",
                    20,
                    110,
                    330,
                    70,
                    "No token: anyone who can reach this PC on the network can use the server and every module you enable next.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                noToken + " AND " + listenAny),
            WithShow(
                Control(
                    "LocalOpenNote",
                    "Text",
                    20,
                    110,
                    330,
                    70,
                    "No token: any program on this PC can use every module you enable next.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                noToken + " AND " + listenLocalhost));
        WireNavigation(authDialog, AccessDialogId, RootsDialogId);

        const string customRoots = "VESTIGO_ROOTS_MODE=\"" + RootSettings.ModeCustom + "\"";
        const string liveRoots = "VESTIGO_ROOTS_MODE=\"" + RootSettings.ModeLive + "\"";
        const string volumeRoots = "NOT (" + customRoots + ")";
        const string writeOrExecute = "VESTIGO_ROOTS_ACCESS <> \"" + RootSettings.AccessRead + "\"";

        XElement rootsDialog = WizardDialog(
            RootsDialogId,
            "File system access",
            "Choose which volumes clients may access as named roots.",
            RootsRadioGroup(),
            WithShow(
                Control(
                    "VolumeNote",
                    "Text",
                    20,
                    154,
                    330,
                    48,
                    "These volumes become named roots (drive-c, drive-d, ...) and are merged with any Vestigo:Roots already in appsettings.json.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                volumeRoots + " AND NOT (" + liveRoots + ")",
                startHidden: false),
            WithShow(
                Control(
                    "LiveNote",
                    "Text",
                    20,
                    154,
                    330,
                    48,
                    "Includes volumes that appear after the service starts. Merged with any Vestigo:Roots already in appsettings.json.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                liveRoots),
            WithShow(
                Control(
                    "CustomNote",
                    "Text",
                    20,
                    154,
                    330,
                    60,
                    "Leave this empty here. Add Vestigo:Roots in %ProgramData%\\Nefarius Software Solutions\\Vestigo\\appsettings.json.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                customRoots));
        WireRootsNavigation(rootsDialog);

        XElement rootsAccessDialog = WizardDialog(
            RootsAccessDialogId,
            "Volume access",
            "Choose the access granted to every volume root.",
            RootsAccessRadioGroup(),
            WithShow(
                Control(
                    "AccessWarning",
                    "Text",
                    20,
                    130,
                    330,
                    70,
                    "Write or execute on whole volumes lets clients replace drivers, services and scripts. Use API tokens on shared machines.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                writeOrExecute,
                startHidden: false),
            WithShow(
                Control(
                    "ReadNote",
                    "Text",
                    20,
                    130,
                    330,
                    48,
                    "Clients can list and read files on those volumes. Write and execute still need a custom Vestigo:Roots entry.",
                    ("Transparent", "yes"),
                    ("NoPrefix", "yes")),
                "NOT (" + writeOrExecute + ")"));
        WireNavigation(rootsAccessDialog, RootsDialogId, "CustomizeDlg");

        XElement errorDialog = new(
            Wix + "Dialog",
            new XAttribute("Id", ErrorDialogId),
            new XAttribute("Width", "260"),
            new XAttribute("Height", "85"),
            new XAttribute("Title", "Vestigo settings"),
            Control("Message", "Text", 48, 15, 195, 40, "[VESTIGO_INPUT_ERROR]", ("NoPrefix", "yes")),
            Control("Ok", "PushButton", 102, 62, 56, 17, "!(loc.WixUIOK)", ("Default", "yes"), ("Cancel", "yes")));
        errorDialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Ok").Add(Publish("EndDialog", "Return", 1, null));

        // License continues to access, then authentication, then roots (and volume access unless custom), then features.
        // Order 10 overrides the stock WixUI publish events for the same controls.
        return new XElement(
            Wix + "UI",
            accessDialog,
            authDialog,
            rootsDialog,
            rootsAccessDialog,
            errorDialog,
            new XElement(Wix + "DialogRef", new XAttribute("Id", "CancelDlg")),
            PublishOn("LicenseAgreementDlg", "Next", "NewDialog", AccessDialogId, 10, "LicenseAccepted = \"1\""),
            PublishOn(
                "CustomizeDlg",
                "Back",
                "NewDialog",
                RootsAccessDialogId,
                10,
                "NOT Installed AND VESTIGO_ROOTS_MODE <> \"" + RootSettings.ModeCustom + "\""),
            PublishOn(
                "CustomizeDlg",
                "Back",
                "NewDialog",
                RootsDialogId,
                11,
                "NOT Installed AND VESTIGO_ROOTS_MODE = \"" + RootSettings.ModeCustom + "\""));
    }

    static XElement WizardDialog(string id, string title, string description, params XElement[] body)
    {
        XElement dialog = new(
            Wix + "Dialog",
            new XAttribute("Id", id),
            new XAttribute("Width", "370"),
            new XAttribute("Height", "270"),
            new XAttribute("Title", "[ProductName] Setup"),
            Control("BannerBitmap", "Bitmap", 0, 0, 370, 44, "!(loc.LicenseAgreementDlgBannerBitmap)", ("TabSkip", "no")),
            Control("BannerLine", "Line", 0, 44, 370, 0, null),
            Control("BottomLine", "Line", 0, 234, 370, 0, null),
            Control("Title", "Text", 15, 6, 340, 15, "{\\WixUI_Font_Title}" + title, ("Transparent", "yes"), ("NoPrefix", "yes")),
            Control("Description", "Text", 25, 23, 340, 15, description, ("Transparent", "yes"), ("NoPrefix", "yes")));
        foreach (XElement control in body)
        {
            dialog.Add(control);
        }

        dialog.Add(
            Control("Back", "PushButton", 180, 243, 56, 17, "!(loc.WixUIBack)"),
            Control("Next", "PushButton", 236, 243, 56, 17, "!(loc.WixUINext)", ("Default", "yes")),
            Control("Cancel", "PushButton", 304, 243, 56, 17, "!(loc.WixUICancel)", ("Cancel", "yes")));
        return dialog;
    }

    static void WireNavigation(XElement dialog, string backDialog, string nextDialog)
    {
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Next").Add(
            Publish("DoAction", "VestigoApplyUiChoice", 1, null),
            Publish("SpawnDialog", ErrorDialogId, 2, "VESTIGO_INPUT_ERROR <> \"\""),
            Publish("NewDialog", nextDialog, 3, "VESTIGO_INPUT_ERROR = \"\""));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Back").Add(
            Publish("NewDialog", backDialog, 1, null));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Cancel").Add(
            Publish("SpawnDialog", "CancelDlg", 1, null));
    }

    static void WireRootsNavigation(XElement dialog)
    {
        const string noError = "VESTIGO_INPUT_ERROR = \"\"";
        const string custom = "VESTIGO_ROOTS_MODE=\"" + RootSettings.ModeCustom + "\"";
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Next").Add(
            Publish("DoAction", "VestigoApplyUiChoice", 1, null),
            Publish("SpawnDialog", ErrorDialogId, 2, "VESTIGO_INPUT_ERROR <> \"\""),
            Publish("NewDialog", RootsAccessDialogId, 3, noError + " AND NOT (" + custom + ")"),
            Publish("NewDialog", "CustomizeDlg", 4, noError + " AND " + custom));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Back").Add(
            Publish("NewDialog", AuthDialogId, 1, null));
        dialog.Elements(Wix + "Control").First(c => (string?)c.Attribute("Id") == "Cancel").Add(
            Publish("SpawnDialog", "CancelDlg", 1, null));
    }

    static XElement ListenRadioGroup()
    {
        XElement group = new(
            Wix + "RadioButtonGroup",
            new XAttribute("Property", "VESTIGO_LISTEN_MODE"),
            Radio(ListenSettings.ListenAny, 0, "Any network address (recommended)"),
            Radio(ListenSettings.ListenLocalhost, 17, "Localhost only"));
        XElement control = Control("ListenChoice", "RadioButtonGroup", 20, 58, 330, 40, null, ("Property", "VESTIGO_LISTEN_MODE"));
        control.Add(group);
        return control;
    }

    static XElement AuthRadioGroup()
    {
        XElement group = new(
            Wix + "RadioButtonGroup",
            new XAttribute("Property", "VESTIGO_AUTH_MODE"),
            Radio(ListenSettings.AuthToken, 0, "Require an API token (recommended)"),
            Radio(ListenSettings.AuthNone, 17, "No API token"));
        XElement control = Control("AuthChoice", "RadioButtonGroup", 20, 58, 330, 40, null, ("Property", "VESTIGO_AUTH_MODE"));
        control.Add(group);
        return control;
    }

    static XElement RootsRadioGroup()
    {
        XElement group = new(
            Wix + "RadioButtonGroup",
            new XAttribute("Property", "VESTIGO_ROOTS_MODE"),
            Radio(RootSettings.ModeSystem, 0, "System drive only"),
            Radio(RootSettings.ModeFixed, 17, "Local fixed disks (recommended)"),
            Radio(RootSettings.ModeAll, 34, "All current volumes"),
            Radio(RootSettings.ModeLive, 51, "All volumes, including later plug-and-play"),
            Radio(RootSettings.ModeCustom, 68, "Custom paths (edit appsettings.json)"));
        XElement control = Control("RootsChoice", "RadioButtonGroup", 20, 58, 330, 90, null, ("Property", "VESTIGO_ROOTS_MODE"));
        control.Add(group);
        return control;
    }

    static XElement RootsAccessRadioGroup()
    {
        XElement group = new(
            Wix + "RadioButtonGroup",
            new XAttribute("Property", "VESTIGO_ROOTS_ACCESS"),
            Radio(RootSettings.AccessRead, 0, "Read only"),
            Radio(RootSettings.AccessReadWrite, 17, "Read and write"),
            Radio(RootSettings.AccessReadWriteExecute, 34, "Read, write and execute (recommended)"));
        XElement control = Control("AccessChoice", "RadioButtonGroup", 20, 58, 330, 56, null, ("Property", "VESTIGO_ROOTS_ACCESS"));
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

    static XElement SecureProperty(string name, string? value)
    {
        XElement property = new(Wix + "Property", new XAttribute("Id", name), new XAttribute("Secure", "yes"));
        if (value is not null)
        {
            property.Add(new XAttribute("Value", value));
        }

        return property;
    }

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
        control.Add(new XAttribute("ShowCondition", "NOT (" + condition + ")"));
        return control;
    }

    static XElement WithShow(XElement control, string condition, bool startHidden = true)
    {
        if (startHidden)
        {
            control.Add(new XAttribute("Hidden", "yes"));
        }

        control.Add(new XAttribute("ShowCondition", condition));
        control.Add(new XAttribute("HideCondition", "NOT (" + condition + ")"));
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