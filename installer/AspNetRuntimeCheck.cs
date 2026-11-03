using System.Xml.Linq;

using Vestigo.Installer.Actions.Logic;

using WixSharp;

namespace Vestigo.Installer;

/// <summary>
///     Adds the x64 .NET Runtime 10 and ASP.NET Core 10 launch condition: WiX hostfxr
///     compatibility on every machine, plus Microsoft's official x64 sharedfx registry
///     markers on native ARM64 only.
/// </summary>
static class AspNetRuntimeCheck
{
    const string WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
    const string NotInstalled = "NOT Installed";
    const string QueryNativeMachineAction = "Wix4QueryNativeMachine_X64";

    static readonly XNamespace Wix = WixNamespace;
    static readonly XNamespace NetFx = WixExtension.NetFxNamespace;
    static readonly XNamespace Util = WixExtension.UtilNamespace;

    public static void Apply(Project project)
    {
        WixExtension.NetFx.PreferredVersion = "5.0.2";
        WixExtension.Util.PreferredVersion = "5.0.2";
        project.Include(WixExtension.NetFx);
        project.Include(WixExtension.Util);
        project.WixSourceGenerated += Generate;
    }

    static void Generate(XDocument document)
    {
        XElement package = document.Root!.Element(Wix + "Package")
            ?? throw new InvalidOperationException("The generated WiX source has no Package element.");

        package.Add(
            new XElement(
                NetFx + "DotNetCompatibilityCheck",
                new XAttribute("Id", "VestigoAspNetRuntimeCheck"),
                new XAttribute("Property", AspNetRuntimePrerequisite.CheckProperty),
                new XAttribute("RuntimeType", AspNetRuntimePrerequisite.RuntimeType),
                new XAttribute("Version", AspNetRuntimePrerequisite.RuntimeVersion),
                new XAttribute("RollForward", AspNetRuntimePrerequisite.RollForward),
                new XAttribute("Platform", AspNetRuntimePrerequisite.Platform)),
            new XElement(Util + "QueryNativeMachine"),
            new XElement(
                Wix + "Property",
                new XAttribute("Id", AspNetRuntimePrerequisite.RegistryProperty),
                new XAttribute("Secure", "yes")),
            new XElement(
                Wix + "CustomAction",
                new XAttribute("Id", AspNetRuntimePrerequisite.DetectAction),
                new XAttribute("BinaryRef", ListenPage.BinaryId),
                new XAttribute("DllEntry", AspNetRuntimePrerequisite.DetectAction),
                new XAttribute("Execute", "immediate"),
                new XAttribute("Return", "check")),
            new XElement(
                Wix + "Launch",
                new XAttribute("Condition", AspNetRuntimePrerequisite.LaunchCondition),
                new XAttribute("Message", AspNetRuntimePrerequisite.MissingMessage)));

        package.Add(RuntimeSequence("InstallExecuteSequence"), RuntimeSequence("InstallUISequence"));
    }

    static XElement RuntimeSequence(string name) =>
        new(
            Wix + name,
            Sequence("override " + QueryNativeMachineAction, "After", "AppSearch"),
            Sequence(AspNetRuntimePrerequisite.DetectAction, "After", QueryNativeMachineAction, NotInstalled),
            new XElement(
                Wix + "LaunchConditions",
                new XAttribute("After", AspNetRuntimePrerequisite.DetectAction)));

    static XElement Sequence(string action, string placement, string anchor, string? condition = null)
    {
        XElement custom = new(
            Wix + "Custom",
            new XAttribute("Action", action),
            new XAttribute(placement, anchor));
        if (condition is not null)
        {
            custom.Add(new XAttribute("Condition", condition));
        }

        return custom;
    }
}
