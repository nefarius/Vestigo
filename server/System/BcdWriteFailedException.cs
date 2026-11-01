namespace Vestigo.SystemInfo;

/// <summary>Raised when writing the BCD test-signing flag fails.</summary>
internal sealed class BcdWriteFailedException(string message, Exception? inner = null) : Exception(message, inner);
