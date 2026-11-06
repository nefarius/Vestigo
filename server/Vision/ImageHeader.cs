using System.Buffers.Binary;

namespace Vestigo.Vision;

/// <summary>Reads image dimensions from the encoded header so a file can be bounded before it is decoded.</summary>
internal static class ImageHeader
{
    /// <summary>Reads width and height of a PNG, BMP or JPEG without decoding. False when unsupported or malformed.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        }
        else if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..]));
        }
        else if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            int index = 2;
            while (index + 9 < data.Length)
            {
                if (data[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                byte marker = data[index + 1];
                if (marker is 0xFF)
                {
                    index++;
                    continue;
                }

                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 5)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 7)..]);
                    break;
                }

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    index += 2;
                    continue;
                }

                index += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[(index + 2)..]);
            }
        }

        return width > 0 && height > 0;
    }
}
