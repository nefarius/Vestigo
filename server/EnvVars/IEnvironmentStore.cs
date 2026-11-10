namespace Vestigo.EnvVars;

public enum EnvironmentScope
{
    Machine,
    User,
    Process
}

/// <summary>A stored variable. <see cref="Expandable" /> marks REG_EXPAND_SZ (references like %SystemRoot% are kept unexpanded).</summary>
public sealed record EnvironmentValue(string Value, bool Expandable);

/// <summary>Raised when the user scope is requested but nobody is logged on at the console.</summary>
/// <param name="reason">Stable token describing why, when one is known.</param>
public sealed class UserNotAvailableException(string message, string? reason = null) : Exception(message)
{
    public string? Reason { get; } = reason;
}

/// <summary>Storage behind the environment tools. Implementations never log values.</summary>
public interface IEnvironmentStore
{
    /// <exception cref="UserNotAvailableException">User scope without an active console user.</exception>
    EnvironmentValue? Get(EnvironmentScope scope, string name);

    /// <exception cref="UserNotAvailableException">User scope without an active console user.</exception>
    IReadOnlyDictionary<string, EnvironmentValue> GetAll(EnvironmentScope scope);

    /// <exception cref="UserNotAvailableException">User scope without an active console user.</exception>
    void Set(EnvironmentScope scope, string name, EnvironmentValue value);

    /// <returns>True when the variable existed.</returns>
    /// <exception cref="UserNotAvailableException">User scope without an active console user.</exception>
    bool Remove(EnvironmentScope scope, string name);

    /// <summary>Tells running applications that persistent environment variables changed. Returns false on timeout or failure.</summary>
    bool BroadcastChange();
}
