using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.EnvVars;

/// <summary>MCP tools for reading and changing persistent and process environment variables.</summary>
[McpServerToolType]
public sealed class EnvironmentTools(EnvironmentService service)
{
    [McpServerTool(Name = "get_environment_variable")]
    [Description("Reads one environment variable. Machine scope reads the Session Manager registry key and user scope reads the environment of the user logged on at the console; both return the unexpanded value and whether it is REG_EXPAND_SZ. Fails with user_not_available when nobody is logged on.")]
    public OperationResult<EnvironmentVariableInfo> GetEnvironmentVariable(
        [Description("Scope: machine, user, or process.")] string scope,
        [Description("Variable name.")] string name) => service.Get(scope, name);

    [McpServerTool(Name = "list_environment_variables")]
    [Description("Lists environment variables of a scope in name order, capped at 1000 entries.")]
    public OperationResult<EnvironmentListing> ListEnvironmentVariables(
        [Description("Scope: machine, user, or process.")] string scope,
        [Description("Only names containing this text (case-insensitive).")] string? nameContains = null,
        [Description("Include values. Disable to list names only.")] bool includeValues = true) =>
        service.List(scope, nameContains, includeValues);

    [McpServerTool(Name = "set_environment_variable")]
    [Description("Changes a persistent environment variable and broadcasts WM_SETTINGCHANGE. Machine scope is a machine-wide configuration change. Names in Vestigo:Environment:ProtectedNames are refused. Append and prepend treat the value as ';'-separated entries and skip entries already present. The value is not echoed back.")]
    public OperationResult<EnvironmentChange> SetEnvironmentVariable(
        [Description("Scope: machine, user, or process. Process only affects the Vestigo service and its children.")] string scope,
        [Description("Variable name.")] string name,
        [Description("Operation: set, unset, append, or prepend.")] string operation,
        [Description("Value or list entries. Ignored for unset.")] string? value = null,
        [Description("Write REG_EXPAND_SZ so %VAR% references are expanded by readers. List operations keep an existing REG_EXPAND_SZ kind.")] bool expand = false) =>
        service.Set(scope, name, operation, value, expand);
}
