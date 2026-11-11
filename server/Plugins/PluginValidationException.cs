namespace Vestigo.Plugins;

/// <summary>A host-authored, safe diagnostic about a deployment or manifest field.</summary>
internal sealed class PluginValidationException(string message) : Exception(message);
