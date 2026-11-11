using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>Keep executable deployments and their activation settings outside client write roots.</summary>
internal static class PluginPathGuard
{
    internal static PathCheck Check(PathCheck check, RootAccess access, VestigoOptions options)
    {
        if (!check.Success || (access & (RootAccess.Write | RootAccess.Execute)) == 0) return check;
        foreach (string protectedPath in new[] { VestigoPaths.PluginDirectory, options.Plugins.EffectiveRoot, VestigoPaths.SettingsFilePath })
        {
            if (!PathPolicy.TryCanonicalize(protectedPath, out string canonical))
                return PathCheck.Fail("access_denied", "Cannot validate the protected plugin deployment boundary.");
            if (PathPolicy.IsInsideRoot(check.FullPath!, canonical) || PathPolicy.IsInsideRoot(canonical, check.FullPath!))
                return PathCheck.Fail("access_denied", "Plugin deployments, activation settings and their ancestors cannot be modified or executed through client path tools.");
        }
        return check;
    }
}
