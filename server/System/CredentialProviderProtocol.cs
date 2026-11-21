using System.Buffers.Binary;
using System.Text;

namespace Vestigo.SystemInfo;

/// <summary>Named-pipe framing between Vestigo and the Credential Provider DLL. Protocol version 1.</summary>
public static class CredentialProviderProtocol
{
    public const int HeaderSize = 12;

    public const int MaxPayloadBytes = 16 * 1024;

    public enum MessageType : ushort
    {
        Hello = 1,
        Dispatch = 2,
        Status = 3,
        Cancel = 4,
        Goodbye = 5
    }

    public enum ProviderStatus : uint
    {
        Connected = 0,
        Submitted = 1,
        Succeeded = 2,
        Failed = 3,
        Cancelled = 4
    }

    public enum UsageScenario : uint
    {
        Logon = 1,
        Unlock = 2
    }

    public sealed record Frame(MessageType Type, byte[] Payload);

    public sealed record HelloMessage(UsageScenario Scenario, uint SessionId, IReadOnlyList<string> UserSids);

    public sealed record DispatchMessage(string UserName, string Domain, char[] Password, string? TargetSid);

    public sealed record StatusMessage(ProviderStatus Status, int Win32Error, string? Message);

    public static byte[] WriteHeader(MessageType type, int payloadLength)
    {
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, CredentialProviderIdentity.ProtocolMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), CredentialProviderIdentity.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)type);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payloadLength);
        return header;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> header, out MessageType type, out int payloadLength, out string? error)
    {
        type = default;
        payloadLength = 0;
        error = null;
        if (header.Length < HeaderSize)
        {
            error = "truncated";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != CredentialProviderIdentity.ProtocolMagic)
        {
            error = "bad_magic";
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != CredentialProviderIdentity.ProtocolVersion)
        {
            error = "unsupported_version";
            return false;
        }

        type = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            error = "invalid_length";
            return false;
        }

        return true;
    }

    public static byte[] EncodeHello(HelloMessage hello)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write((uint)hello.Scenario);
        writer.Write(hello.SessionId);
        writer.Write(hello.UserSids.Count);
        foreach (string sid in hello.UserSids)
        {
            WriteUtf16(writer, sid);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static HelloMessage DecodeHello(ReadOnlySpan<byte> payload)
    {
        using BinaryReader reader = Reader(payload);
        UsageScenario scenario = (UsageScenario)reader.ReadUInt32();
        uint sessionId = reader.ReadUInt32();
        int count = reader.ReadInt32();
        if (count is < 0 or > 32)
        {
            throw new InvalidDataException("sid count");
        }

        List<string> sids = [];
        for (int index = 0; index < count; index++)
        {
            sids.Add(ReadUtf16(reader));
        }

        return new HelloMessage(scenario, sessionId, sids);
    }

    public static byte[] EncodeDispatch(string userName, string domain, ReadOnlySpan<char> password, string? targetSid)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.Unicode, leaveOpen: true);
        WriteUtf16(writer, userName);
        WriteUtf16(writer, domain);
        WriteUtf16(writer, password);
        WriteUtf16(writer, targetSid ?? "");
        writer.Flush();
        return stream.ToArray();
    }

    public static DispatchMessage DecodeDispatch(ReadOnlySpan<byte> payload)
    {
        using BinaryReader reader = Reader(payload);
        string userName = ReadUtf16(reader);
        string domain = ReadUtf16(reader);
        char[] password = ReadUtf16Chars(reader);
        string targetSid = ReadUtf16(reader);
        return new DispatchMessage(userName, domain, password, string.IsNullOrEmpty(targetSid) ? null : targetSid);
    }

    public static byte[] EncodeStatus(StatusMessage status)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write((uint)status.Status);
        writer.Write(status.Win32Error);
        WriteUtf16(writer, status.Message ?? "");
        writer.Flush();
        return stream.ToArray();
    }

    public static StatusMessage DecodeStatus(ReadOnlySpan<byte> payload)
    {
        using BinaryReader reader = Reader(payload);
        ProviderStatus status = (ProviderStatus)reader.ReadUInt32();
        int win32 = reader.ReadInt32();
        string message = ReadUtf16(reader);
        return new StatusMessage(status, win32, string.IsNullOrEmpty(message) ? null : message);
    }

    private static BinaryReader Reader(ReadOnlySpan<byte> payload) =>
        new(new MemoryStream(payload.ToArray()), Encoding.Unicode, leaveOpen: false);

    private static void WriteUtf16(BinaryWriter writer, string value) => WriteUtf16(writer, value.AsSpan());

    private static void WriteUtf16(BinaryWriter writer, ReadOnlySpan<char> value)
    {
        if (value.Length > 2048)
        {
            throw new InvalidDataException("string too long");
        }

        writer.Write((ushort)value.Length);
        foreach (char c in value)
        {
            writer.Write((ushort)c);
        }
    }

    private static string ReadUtf16(BinaryReader reader) => new(ReadUtf16Chars(reader));

    private static char[] ReadUtf16Chars(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        if (length > 2048)
        {
            throw new InvalidDataException("string too long");
        }

        char[] buffer = new char[length];
        for (int index = 0; index < length; index++)
        {
            buffer[index] = (char)reader.ReadUInt16();
        }

        return buffer;
    }
}
