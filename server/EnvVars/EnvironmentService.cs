using System.Security;

using Vestigo.Configuration;

namespace Vestigo.EnvVars;

public sealed record EnvironmentVariableInfo(string Name, string Scope, string Value, string Kind);

public sealed record EnvironmentListing(string Scope, IReadOnlyList<EnvironmentVariableInfo> Variables, bool Truncated);

/// <summary>Result of a set operation. The value is deliberately not echoed.</summary>
public sealed record EnvironmentChange(string Name, string Scope, string Operation, bool Changed, string Kind, string Broadcast);

/// <summary>Validation and list logic for environment tools on top of an <see cref="IEnvironmentStore" />. Never logs values.</summary>
public sealed class EnvironmentService(IEnvironmentStore store, VestigoOptions options)
{
    private const int MaxValueLength = 32_767;
    private const int MaxListed = 1000;

    public OperationResult<EnvironmentVariableInfo> Get(string scope, string name)
    {
        if (!TryScope(scope, out EnvironmentScope parsed, out string? error))
        {
            return OperationResult<EnvironmentVariableInfo>.Fail("invalid_scope", error!);
        }

        string? nameError = ValidateName(name);
        if (nameError is not null)
        {
            return OperationResult<EnvironmentVariableInfo>.Fail("invalid_name", nameError);
        }

        return Guard<EnvironmentVariableInfo>(() =>
        {
            EnvironmentValue? value = store.Get(parsed, name);
            return value is null
                ? OperationResult<EnvironmentVariableInfo>.Fail("not_found", $"Variable '{name}' is not defined in {parsed} scope.")
                : OperationResult<EnvironmentVariableInfo>.Ok(Info(parsed, name, value));
        });
    }

    public OperationResult<EnvironmentListing> List(string scope, string? nameContains, bool includeValues)
    {
        if (!TryScope(scope, out EnvironmentScope parsed, out string? error))
        {
            return OperationResult<EnvironmentListing>.Fail("invalid_scope", error!);
        }

        return Guard<EnvironmentListing>(() =>
        {
            List<KeyValuePair<string, EnvironmentValue>> all = store.GetAll(parsed)
                .Where(pair => string.IsNullOrEmpty(nameContains) || pair.Key.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<EnvironmentVariableInfo> variables = all
                .Take(MaxListed)
                .Select(pair => includeValues
                    ? Info(parsed, pair.Key, pair.Value)
                    : new EnvironmentVariableInfo(pair.Key, parsed.ToString().ToLowerInvariant(), string.Empty, Kind(parsed, pair.Value)))
                .ToList();
            return OperationResult<EnvironmentListing>.Ok(
                new EnvironmentListing(parsed.ToString().ToLowerInvariant(), variables, all.Count > MaxListed));
        });
    }

    public OperationResult<EnvironmentChange> Set(string scope, string name, string operation, string? value, bool expand)
    {
        if (!TryScope(scope, out EnvironmentScope parsed, out string? error))
        {
            return OperationResult<EnvironmentChange>.Fail("invalid_scope", error!);
        }

        string? nameError = ValidateName(name);
        if (nameError is not null)
        {
            return OperationResult<EnvironmentChange>.Fail("invalid_name", nameError);
        }

        if (options.Environment.ProtectedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return OperationResult<EnvironmentChange>.Fail("protected_name", $"Variable '{name}' is protected and cannot be changed.");
        }

        string op = (operation ?? string.Empty).Trim().ToLowerInvariant();
        if (op is not ("set" or "unset" or "append" or "prepend"))
        {
            return OperationResult<EnvironmentChange>.Fail("invalid_operation", "Operation must be set, unset, append, or prepend.");
        }

        if (op != "unset" && value is null)
        {
            return OperationResult<EnvironmentChange>.Fail("invalid_value", $"A value is required for '{op}'.");
        }

        if (value is { Length: > MaxValueLength } || value is not null && (value.Contains('\0', StringComparison.Ordinal)))
        {
            return OperationResult<EnvironmentChange>.Fail("invalid_value", "Value is too long or contains a NUL character.");
        }

        return Guard<EnvironmentChange>(() =>
        {
            string scopeName = parsed.ToString().ToLowerInvariant();
            bool broadcastNeeded = parsed != EnvironmentScope.Process;
            EnvironmentValue? current = store.Get(parsed, name);

            if (op == "unset")
            {
                bool existed = current is not null && store.Remove(parsed, name);
                return Result(existed, current?.Expandable ?? false);
            }

            EnvironmentValue next;
            if (op == "set")
            {
                next = new EnvironmentValue(value!, expand);
                if (current is not null && current.Value == next.Value && current.Expandable == next.Expandable)
                {
                    return Result(false, next.Expandable);
                }
            }
            else
            {
                string merged = EnvironmentListEditor.Add(current?.Value, value!, op == "prepend", out bool listChanged);
                bool kind = expand || (current?.Expandable ?? false);
                if (!listChanged)
                {
                    return Result(false, kind);
                }

                next = new EnvironmentValue(merged, kind);
            }

            store.Set(parsed, name, next);
            return Result(true, next.Expandable);

            OperationResult<EnvironmentChange> Result(bool changed, bool expandable)
            {
                string broadcast = "skipped";
                if (changed && broadcastNeeded)
                {
                    broadcast = store.BroadcastChange() ? "sent" : "timeout";
                }

                return OperationResult<EnvironmentChange>.Ok(
                    new EnvironmentChange(name, scopeName, op, changed, Kind(parsed, expandable), broadcast));
            }
        });
    }

    private static EnvironmentVariableInfo Info(EnvironmentScope scope, string name, EnvironmentValue value) =>
        new(name, scope.ToString().ToLowerInvariant(), value.Value, Kind(scope, value));

    private static string Kind(EnvironmentScope scope, EnvironmentValue value) => Kind(scope, value.Expandable);

    private static string Kind(EnvironmentScope scope, bool expandable) =>
        scope == EnvironmentScope.Process ? "process" : expandable ? "REG_EXPAND_SZ" : "REG_SZ";

    private static bool TryScope(string scope, out EnvironmentScope parsed, out string? error)
    {
        error = null;
        switch ((scope ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "machine":
                parsed = EnvironmentScope.Machine;
                return true;
            case "user":
                parsed = EnvironmentScope.User;
                return true;
            case "process":
                parsed = EnvironmentScope.Process;
                return true;
            default:
                parsed = default;
                error = "Scope must be machine, user, or process.";
                return false;
        }
    }

    private static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
        {
            return "Name is required and at most 255 characters.";
        }

        if (name.Contains('=', StringComparison.Ordinal) || name.Any(char.IsControl) || name.Contains('\\', StringComparison.Ordinal))
        {
            return "Name cannot contain '=', '\\' or control characters.";
        }

        return null;
    }

    private static OperationResult<T> Guard<T>(Func<OperationResult<T>> action)
    {
        try
        {
            return action();
        }
        catch (UserNotAvailableException exception)
        {
            return OperationResult<T>.Fail("user_not_available", exception.Message);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return OperationResult<T>.Fail("access_denied", "Windows denied access to the environment store.");
        }
        catch (IOException exception)
        {
            return OperationResult<T>.Fail("io_error", exception.Message);
        }
    }
}
