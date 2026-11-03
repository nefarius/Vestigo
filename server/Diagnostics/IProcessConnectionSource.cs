namespace Vestigo.Diagnostics;

/// <summary>Reads the TCP and UDP endpoints owned by one process.</summary>
public interface IProcessConnectionSource
{
    ConnectionQuery Query(int pid, int limit);
}
