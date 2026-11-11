using System.Reflection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Vestigo.Modules;

namespace Vestigo.Plugins;

internal static class PluginToolValidation
{
    public static void Validate(IReadOnlyList<VestigoModule> modules)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (VestigoModule module in modules)
        {
            string owner = module is ExternalModuleAdapter external ? $"plugin '{external.PluginId}', module '{module.Name}'" : $"module '{module.Name}'";
            foreach (Type type in module.ToolTypes)
            {
                if (!type.IsClass || !type.IsPublic || type.ContainsGenericParameters ||
                    (type.IsAbstract && !type.IsSealed) || type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
                    throw new InvalidOperationException($"Invalid MCP tool type '{type.FullName}' in {owner}.");
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
                    string name;
                    try
                    {
                        // Let the SDK resolve attributes, async suffixes and naming conventions.
                        McpServerTool tool = method.IsStatic
                            ? McpServerTool.Create(method, (object?)null)
                            : McpServerTool.Create(method, (RequestContext<CallToolRequestParams> _) => throw new InvalidOperationException("Validation never invokes a tool."));
                        name = tool.ProtocolTool.Name;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"MCP tool '{type.FullName}.{method.Name}' in {owner} is invalid ({ex.GetType().Name}).");
                    }
                    if (!names.TryAdd(name, owner))
                        throw new InvalidOperationException($"Duplicate MCP tool name '{name}' in {owner} and {names[name]}.");
                }
            }
        }
    }
}
