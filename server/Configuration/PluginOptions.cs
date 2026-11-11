namespace Vestigo.Configuration;

public enum PluginTrustMode { Disabled, AdministratorManaged, PinnedSha256 }

public sealed class PluginOptions
{
    public bool Enabled { get; set; }
    public string? Root { get; set; }
    public PluginTrustMode TrustMode { get; set; } = PluginTrustMode.Disabled;
    /// <summary>Plugin ID to SHA-256 of the entire deployment tree, including the manifest.</summary>
    public Dictionary<string, string> PackageHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string EffectiveRoot => string.IsNullOrWhiteSpace(Root) ? VestigoPaths.PluginDirectory : Path.GetFullPath(Root);

    public void Validate()
    {
        if (!Enum.IsDefined(TrustMode) || (Enabled && TrustMode == PluginTrustMode.Disabled))
            throw new InvalidOperationException("Vestigo:Plugins requires an explicit TrustMode when enabled.");
        if (Root is not null && !Path.IsPathFullyQualified(Root))
            throw new InvalidOperationException("Vestigo:Plugins:Root must be an absolute path.");
        if (Root is not null && (EffectiveRoot.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                                 EffectiveRoot.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new InvalidOperationException("Vestigo:Plugins:Root must not use a device-path prefix.");
        if (Root is not null && Security.PathPolicy.TryCanonicalize(EffectiveRoot, out string pluginRoot) &&
            Security.PathPolicy.TryCanonicalize(VestigoPaths.SettingsDirectory, out string stateRoot) &&
            (Security.PathPolicy.IsInsideRoot(pluginRoot, stateRoot) || Security.PathPolicy.IsInsideRoot(stateRoot, pluginRoot)) &&
            !string.Equals(Path.TrimEndingDirectorySeparator(EffectiveRoot), VestigoPaths.PluginDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Vestigo:Plugins:Root must be the default plugins directory or outside the mutable settings tree; state ACL repair must never repair executable deployments.");
        foreach ((string id, string hash) in PackageHashes)
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"Vestigo:Plugins:PackageHashes:{id} must be a SHA-256 hex digest.");
    }
}
