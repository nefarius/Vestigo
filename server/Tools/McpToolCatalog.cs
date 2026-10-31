using System.ComponentModel;
using System.Reflection;
using System.Text;

using ModelContextProtocol.Server;

namespace Vestigo.Tools;

/// <summary>
///     Renders the checked-in MCP tool catalog from the attributes on this assembly.
/// </summary>
public static class McpToolCatalog
{
    public static string Markdown()
    {
        StringBuilder builder = new();
        builder.AppendLine("# MCP tools");
        builder.AppendLine();
        builder.AppendLine("Generated from `McpServerTool` attributes. Do not edit by hand.");
        builder.AppendLine();

        IEnumerable<MethodInfo> tools = typeof(McpToolCatalog).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Select(method => new { method, tool = method.GetCustomAttribute<McpServerToolAttribute>() })
            .Where(item => item.tool is not null)
            .OrderBy(item => item.tool!.Name, StringComparer.Ordinal)
            .Select(item => item.method);

        foreach (MethodInfo method in tools)
        {
            McpServerToolAttribute tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
            string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            builder.Append("## `").Append(tool.Name).AppendLine("`");
            builder.AppendLine();
            builder.AppendLine(description);
            builder.AppendLine();
            ParameterInfo[] parameters = method.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .ToArray();
            if (parameters.Length == 0)
            {
                builder.AppendLine("No parameters.");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine("| Parameter | Type | Description |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (ParameterInfo parameter in parameters)
            {
                string parameterDescription = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
                builder.Append("| `")
                    .Append(parameter.Name)
                    .Append("` | `")
                    .Append(DisplayType(parameter.ParameterType))
                    .Append("` | ")
                    .Append(parameterDescription.Replace("|", "\\|", StringComparison.Ordinal))
                    .AppendLine(" |");
            }

            builder.AppendLine();
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string DisplayType(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return DisplayType(underlying) + "?";
        }

        if (type.IsArray)
        {
            return DisplayType(type.GetElementType()!) + "[]";
        }

        return type.Name switch
        {
            "String" => "string",
            "Boolean" => "bool",
            "Int32" => "int",
            "Int64" => "long",
            _ => type.Name
        };
    }
}
