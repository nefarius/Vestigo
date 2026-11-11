using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;

namespace Vestigo.Diagnostics;

/// <summary>
///     One STUN Binding request and the XOR-MAPPED-ADDRESS in a Binding Success response (RFC 8489).
///     The exchange is not authenticated.
/// </summary>
internal static class StunBinding
{
    public const int HeaderLength = 20;
    public const int TransactionLength = 12;

    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort MappedAddress = 0x0001;
    private const ushort XorMappedAddress = 0x0020;
    private const ushort Fingerprint = 0x8028;
    private const uint MagicCookie = 0x2112A442;
    private const uint FingerprintXor = 0x5354554E;

    public static byte[] CreateRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != TransactionLength)
        {
            throw new ArgumentException("A STUN transaction id is 12 bytes.", nameof(transactionId));
        }

        byte[] message = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(message, BindingRequest);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8));
        return message;
    }

    public static byte[] CreateSuccess(ReadOnlySpan<byte> transactionId, IPAddress address, int port)
    {
        if (transactionId.Length != TransactionLength)
        {
            throw new ArgumentException("A STUN transaction id is 12 bytes.", nameof(transactionId));
        }

        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        int valueLength = address.AddressFamily == AddressFamily.InterNetworkV6 ? 20 : 8;
        byte[] message = new byte[HeaderLength + 4 + valueLength];
        BinaryPrimitives.WriteUInt16BigEndian(message, BindingSuccess);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(message.Length - HeaderLength));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8));
        Span<byte> attribute = message.AsSpan(HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(attribute, XorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(attribute[2..], (ushort)valueLength);
        attribute[5] = address.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x02 : (byte)0x01;
        BinaryPrimitives.WriteUInt16BigEndian(attribute[6..], (ushort)(port ^ (MagicCookie >> 16)));
        WriteAddress(attribute.Slice(8, valueLength - 4), address, transactionId, xor: true);
        return message;
    }

    public static byte[] WithFingerprint(byte[] message)
    {
        byte[] full = new byte[message.Length + 8];
        message.AsSpan(0, HeaderLength).CopyTo(full);
        BinaryPrimitives.WriteUInt16BigEndian(full.AsSpan(2), (ushort)(full.Length - HeaderLength));
        message.AsSpan(HeaderLength).CopyTo(full.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt16BigEndian(full.AsSpan(full.Length - 8), Fingerprint);
        BinaryPrimitives.WriteUInt16BigEndian(full.AsSpan(full.Length - 6), 4);
        // HashToUInt32 keeps byte ordering explicit: Crc32.Hash returns little-endian bytes,
        // whereas STUN writes the XOR'd fingerprint in network byte order.
        BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(full.Length - 4), Crc32.HashToUInt32(full.AsSpan(0, full.Length - 8)) ^ FingerprintXor);
        return full;
    }

    public static StunDecode Read(ReadOnlySpan<byte> message, ReadOnlySpan<byte> transactionId)
    {
        if (message.Length < HeaderLength || transactionId.Length != TransactionLength || (message[0] & 0xC0) != 0)
        {
            return default;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(message) != BindingSuccess
            || BinaryPrimitives.ReadUInt32BigEndian(message[4..]) != MagicCookie
            || !message.Slice(8, TransactionLength).SequenceEqual(transactionId))
        {
            return default;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if (length % 4 != 0 || HeaderLength + length > message.Length)
        {
            return default;
        }

        IPAddress? mapped = null;
        int? port = null;
        bool reflexive = false;
        int offset = HeaderLength;
        int end = HeaderLength + length;
        while (offset + 4 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
            int value = offset + 4;
            int padded = (valueLength + 3) & ~3;
            if (value + padded > end)
            {
                return default;
            }

            ReadOnlySpan<byte> body = message.Slice(value, valueLength);
            if (type == Fingerprint)
            {
                if (valueLength != 4 || value + padded != end || Crc32.HashToUInt32(message[..offset]) != (BinaryPrimitives.ReadUInt32BigEndian(body) ^ FingerprintXor))
                {
                    return default;
                }
            }
            else if (type is XorMappedAddress or MappedAddress)
            {
                if (!TryAddress(body, transactionId, type == XorMappedAddress, out IPAddress? address, out int decodedPort))
                {
                    return default;
                }

                if (type == XorMappedAddress || mapped is null)
                {
                    mapped = address;
                    port = decodedPort;
                    reflexive = type == XorMappedAddress;
                }
            }
            else if (type < 0x8000 && !Known(type))
            {
                return default;
            }

            offset = value + padded;
        }

        return offset == end ? new StunDecode(true, mapped, port, reflexive) : default;
    }

    private static bool Known(ushort type) => type is 0x0006 or 0x0008 or 0x0009 or 0x000A or 0x000C or 0x0014 or 0x0015 or 0x001C;

    private static bool TryAddress(ReadOnlySpan<byte> body, ReadOnlySpan<byte> transactionId, bool xor, out IPAddress? address, out int port)
    {
        address = null;
        port = 0;
        if (body.Length < 8 || body[0] != 0)
        {
            return false;
        }

        int xport = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
        port = xor ? xport ^ (int)(MagicCookie >> 16) : xport;
        if (body[1] == 0x01 && body.Length >= 8)
        {
            Span<byte> raw = stackalloc byte[4];
            body.Slice(4, 4).CopyTo(raw);
            if (xor)
            {
                BinaryPrimitives.WriteUInt32BigEndian(raw, BinaryPrimitives.ReadUInt32BigEndian(raw) ^ MagicCookie);
            }

            address = new IPAddress(raw);
            return true;
        }

        if (body[1] == 0x02 && body.Length >= 20)
        {
            Span<byte> raw = stackalloc byte[16];
            body.Slice(4, 16).CopyTo(raw);
            if (xor)
            {
                Span<byte> mask = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
                transactionId.CopyTo(mask[4..]);
                for (int index = 0; index < raw.Length; index++)
                {
                    raw[index] ^= mask[index];
                }
            }

            address = new IPAddress(raw);
            return true;
        }

        return false;
    }

    private static void WriteAddress(Span<byte> destination, IPAddress address, ReadOnlySpan<byte> transactionId, bool xor)
    {
        byte[] raw = address.GetAddressBytes();
        if (xor && raw.Length == 4)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, BinaryPrimitives.ReadUInt32BigEndian(raw) ^ MagicCookie);
            return;
        }

        raw.CopyTo(destination);
        if (!xor)
        {
            return;
        }

        Span<byte> mask = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
        transactionId.CopyTo(mask[4..]);
        for (int index = 0; index < raw.Length; index++)
        {
            destination[index] ^= mask[index];
        }
    }
}

/// <summary>A Binding Success decode. <see cref="Success" /> is false when the datagram is not that response.</summary>
internal readonly record struct StunDecode(bool Success, IPAddress? MappedAddress, int? MappedPort, bool Reflexive);
