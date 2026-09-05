using System.Buffers.Binary;

namespace PhotoOrganizer.Services;

/// <summary>
/// Minimal JPEG container walker. Used to find complete JPEG images embedded inside
/// RAW files and to locate the EXIF block without decoding any pixels.
/// </summary>
internal static class JpegStructure
{
    private const byte MarkerPrefix = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const byte Application1 = 0xE1;
    private const byte StuffedByte = 0x00;
    private const byte TemporaryMarker = 0x01;
    private const byte FirstRestartMarker = 0xD0;
    private const byte LastRestartMarker = 0xD7;

    private static readonly byte[] ExifSignature = "Exif\0\0"u8.ToArray();

    public static bool IsJpegStart(ReadOnlySpan<byte> data)
        => data.Length >= 3
            && data[0] == MarkerPrefix
            && data[1] == StartOfImage
            && data[2] == MarkerPrefix;

    public static List<int> FindJpegStarts(ReadOnlySpan<byte> data)
    {
        var starts = new List<int>();
        for (var index = 0; index + 2 < data.Length; index++)
        {
            if (data[index] == MarkerPrefix
                && data[index + 1] == StartOfImage
                && data[index + 2] == MarkerPrefix)
            {
                starts.Add(index);
            }
        }

        return starts;
    }

    /// <summary>
    /// Returns the exclusive end offset of the JPEG that begins at <paramref name="start"/>,
    /// or -1 when the image is truncated or malformed. Segment lengths are honoured so that
    /// thumbnails nested inside an EXIF block never terminate the outer image early.
    /// </summary>
    public static int FindJpegEnd(ReadOnlySpan<byte> data, int start)
    {
        if (start < 0 || start + 4 > data.Length)
        {
            return -1;
        }

        var position = start + 2;
        while (position + 1 < data.Length)
        {
            if (data[position] != MarkerPrefix)
            {
                position++;
                continue;
            }

            var marker = data[position + 1];
            if (marker == EndOfImage)
            {
                return position + 2;
            }

            if (marker == MarkerPrefix)
            {
                position++;
                continue;
            }

            if (marker == StuffedByte
                || marker == TemporaryMarker
                || marker == StartOfImage
                || (marker >= FirstRestartMarker && marker <= LastRestartMarker))
            {
                position += 2;
                continue;
            }

            if (position + 4 > data.Length)
            {
                return -1;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 2, 2));
            if (segmentLength < 2)
            {
                return -1;
            }

            position += 2 + segmentLength;
            if (marker != StartOfScan)
            {
                continue;
            }

            position = SkipEntropyCodedData(data, position);
            if (position < 0)
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Advances past compressed scan data to the next real marker. Inside scan data an
    /// 0xFF byte is either stuffed with 0x00, a restart marker, or a fill byte.
    /// </summary>
    private static int SkipEntropyCodedData(ReadOnlySpan<byte> data, int position)
    {
        while (position + 1 < data.Length)
        {
            if (data[position] != MarkerPrefix)
            {
                position++;
                continue;
            }

            var next = data[position + 1];
            if (next == MarkerPrefix)
            {
                position++;
                continue;
            }

            if (next == StuffedByte || (next >= FirstRestartMarker && next <= LastRestartMarker))
            {
                position += 2;
                continue;
            }

            return position;
        }

        return -1;
    }

    /// <summary>
    /// Returns the offset of the TIFF header inside the APP1 EXIF segment of the JPEG
    /// starting at <paramref name="jpegStart"/>, or -1 when the image carries no EXIF.
    /// </summary>
    public static int FindExifTiffOffset(ReadOnlySpan<byte> data, int jpegStart)
    {
        if (!IsJpegStart(data.Slice(Math.Min(jpegStart, data.Length))))
        {
            return -1;
        }

        var position = jpegStart + 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != MarkerPrefix)
            {
                return -1;
            }

            var marker = data[position + 1];
            if (marker == MarkerPrefix)
            {
                position++;
                continue;
            }

            if (marker == StartOfScan || marker == EndOfImage)
            {
                return -1;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 2, 2));
            if (segmentLength < 2)
            {
                return -1;
            }

            var payloadOffset = position + 4;
            var payloadLength = segmentLength - 2;
            if (marker == Application1
                && payloadLength > ExifSignature.Length
                && payloadOffset + ExifSignature.Length <= data.Length
                && data.Slice(payloadOffset, ExifSignature.Length).SequenceEqual(ExifSignature))
            {
                return payloadOffset + ExifSignature.Length;
            }

            position += 2 + segmentLength;
        }

        return -1;
    }

    /// <summary>
    /// Finds a bare "Exif\0\0" signature followed by a TIFF header. HEIF-family containers
    /// store EXIF as an item rather than a JPEG segment, and this locates it cheaply.
    /// </summary>
    public static int FindLooseExifTiffOffset(ReadOnlySpan<byte> data)
    {
        var searchFrom = 0;
        while (searchFrom < data.Length)
        {
            var found = data.Slice(searchFrom).IndexOf(ExifSignature);
            if (found < 0)
            {
                return -1;
            }

            var tiffOffset = searchFrom + found + ExifSignature.Length;
            if (LooksLikeTiffHeader(data, tiffOffset))
            {
                return tiffOffset;
            }

            searchFrom = searchFrom + found + 1;
        }

        return -1;
    }

    public static bool LooksLikeTiffHeader(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        var isLittleEndian = data[offset] == 0x49 && data[offset + 1] == 0x49;
        var isBigEndian = data[offset] == 0x4D && data[offset + 1] == 0x4D;
        return isLittleEndian || isBigEndian;
    }
}
