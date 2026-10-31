namespace Vestigo.Software;

/// <summary>Read-only source of installed software entries.</summary>
public interface ISoftwareInventory
{
    IReadOnlyList<InstalledSoftware> List();
}
