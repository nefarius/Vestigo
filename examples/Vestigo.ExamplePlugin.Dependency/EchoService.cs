namespace Vestigo.ExamplePlugin.Dependency;

/// <summary>A private managed dependency, deliberately separate from the shared host API.</summary>
public sealed class EchoService
{
    public string Echo(string value) => value;
}
