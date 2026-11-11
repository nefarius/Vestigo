namespace Vestigo.PluginFixture;

public sealed class PrivateService
{
    public static int Major => typeof(PrivateService).Assembly.GetName().Version!.Major;
}
