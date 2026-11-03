using System;
using System.IO;

namespace VisualRoomComposer.Presentation
{
    /// <summary>Reads PNG/JPEG dimensions before a native decoder can allocate image memory.</summary>
    internal static class ImageHeaderGuard
    {
        internal static long DecodedBytes(byte[] bytes, int maximumDimension = 4096)
        {
            int width = 0, height = 0;
            if (bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
                bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10 &&
                bytes[12] == 73 && bytes[13] == 72 && bytes[14] == 68 && bytes[15] == 82)
            {
                width = Big32(bytes, 16); height = Big32(bytes, 20);
            }
            else if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
            {
                var cursor = 2;
                while (cursor < bytes.Length)
                {
                    if (bytes[cursor++] != 255) throw new InvalidDataException("Invalid JPEG marker.");
                    while (cursor < bytes.Length && bytes[cursor] == 255) cursor++;
                    if (cursor >= bytes.Length) break;
                    var marker = bytes[cursor++];
                    if (marker == 0xD9 || marker == 0xDA) break;
                    if (marker == 0x01 || marker >= 0xD0 && marker <= 0xD7) continue;
                    if (cursor + 2 > bytes.Length) break;
                    int length = bytes[cursor] * 256 + bytes[cursor + 1];
                    if (length < 2 || length > bytes.Length - cursor) throw new InvalidDataException("Invalid JPEG segment length.");
                    if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        if (length < 8) throw new InvalidDataException("Invalid JPEG frame.");
                        height = bytes[cursor + 3] * 256 + bytes[cursor + 4];
                        width = bytes[cursor + 5] * 256 + bytes[cursor + 6];
                        break;
                    }
                    cursor += length;
                }
            }
            if (width <= 0 || height <= 0 || width > maximumDimension || height > maximumDimension)
                throw new InvalidDataException("Image dimensions are invalid or exceed the preview budget.");
            return checked((long)width * height * 4);
        }

        private static int Big32(byte[] data, int offset)
        {
            var value = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
            return value <= int.MaxValue ? (int)value : throw new InvalidDataException("Image dimensions overflow.");
        }
    }
}
