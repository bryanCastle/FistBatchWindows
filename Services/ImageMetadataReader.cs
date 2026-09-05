using System.Buffers;
using System.Buffers.Binary;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Services;

/// <summary>
/// Reads the capture date, and where relevant the embedded preview offsets, from a picture
/// file by inspecting only its header. This replaces the Windows shell property lookup,
/// which required a round trip per file and dominated folder scanning time.
/// </summary>
internal static class ImageMetadataReader
{
    private const int HeaderWindowLength = 128 * 1024;
    private const int EmbeddedJpegWindowLength = 64 * 1024;
    private const int FujiJpegOffsetPosition = 84;
    private const int FujiJpegLengthPosition = 88;
    private const int BoxTypePosition = 4;
    private const int MaxBytesAfterBoxHeader = 96;
    private const int MinimumPreviewLength = 1024;

    private static readonly byte[] FujiSignature = "FUJIFILMCCD-RAW"u8.ToArray();
    private static readonly byte[] IsoMediaBoxType = "ftyp"u8.ToArray();
    private static readonly byte[] CanonExifBoxType = "CMT2"u8.ToArray();
    private static readonly byte[] CanonRootExifBoxType = "CMT1"u8.ToArray();
    private static readonly byte[] CanonThumbnailBoxType = "THMB"u8.ToArray();
    private static readonly byte[] CanonPreviewBoxType = "PRVW"u8.ToArray();

    public static ImageFileMetadata Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 8192,
                FileOptions.RandomAccess);

            return ReadFromStream(stream);
        }
        catch (Exception)
        {
            return ImageFileMetadata.Empty;
        }
    }

    private static ImageFileMetadata ReadFromStream(FileStream stream)
    {
        var windowLength = (int)Math.Min(stream.Length, HeaderWindowLength);
        if (windowLength < 16)
        {
            return ImageFileMetadata.Empty;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            stream.ReadExactly(buffer, 0, windowLength);
            var header = buffer.AsSpan(0, windowLength);

            if (JpegStructure.IsJpegStart(header))
            {
                return ReadStandardJpeg(stream, header);
            }

            if (JpegStructure.LooksLikeTiffHeader(header, 0))
            {
                return TiffMetadataParser.Parse(stream, 0);
            }

            if (header.StartsWith(FujiSignature))
            {
                return ReadFujiRaw(stream, header);
            }

            if (header.Length > BoxTypePosition + IsoMediaBoxType.Length
                && header.Slice(BoxTypePosition, IsoMediaBoxType.Length).SequenceEqual(IsoMediaBoxType))
            {
                return ReadIsoMediaContainer(stream, header);
            }

            return ReadLooseExifOnly(stream, header);
        }
        catch (Exception)
        {
            return ImageFileMetadata.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Windows decodes ordinary JPEGs directly from disk, so only the date is needed here.
    /// </summary>
    private static ImageFileMetadata ReadStandardJpeg(FileStream stream, ReadOnlySpan<byte> header)
    {
        var exifOffset = JpegStructure.FindExifTiffOffset(header, 0);
        if (exifOffset < 0)
        {
            return ImageFileMetadata.Empty;
        }

        var exif = TiffMetadataParser.Parse(stream, exifOffset);
        return new ImageFileMetadata(exif.DateTaken, [], exif.Orientation);
    }

    /// <summary>
    /// Fujifilm RAF files record the offset and length of a full JPEG in their fixed header.
    /// </summary>
    private static ImageFileMetadata ReadFujiRaw(FileStream stream, ReadOnlySpan<byte> header)
    {
        if (header.Length < FujiJpegLengthPosition + 4)
        {
            return ImageFileMetadata.Empty;
        }

        var jpegOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(FujiJpegOffsetPosition, 4));
        var jpegLength = (long)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(FujiJpegLengthPosition, 4));
        if (jpegOffset == 0 || jpegLength < MinimumPreviewLength || jpegOffset + jpegLength > stream.Length)
        {
            return ImageFileMetadata.Empty;
        }

        var previews = new List<PreviewLocation>
        {
            new(jpegOffset, (int)jpegLength, PixelWidth: 0),
        };

        // RAF headers carry no EXIF, so the date and orientation come from the embedded JPEG.
        var embedded = ReadEmbeddedJpegMetadata(stream, jpegOffset);
        return new ImageFileMetadata(embedded.DateTaken, previews, embedded.Orientation);
    }

    /// <summary>
    /// Handles ISO base media containers: Canon CR3 and the HEIF family. Canon stores EXIF in
    /// CMT boxes and a small thumbnail in a THMB box, both near the start of the file.
    /// </summary>
    private static ImageFileMetadata ReadIsoMediaContainer(FileStream stream, ReadOnlySpan<byte> header)
    {
        var exif = ReadCanonExifBox(stream, header, CanonExifBoxType)
            ?? ReadCanonExifBox(stream, header, CanonRootExifBoxType);

        var previews = new List<PreviewLocation>();
        AddBoxedJpeg(previews, header, CanonThumbnailBoxType);
        AddBoxedJpeg(previews, header, CanonPreviewBoxType);
        previews.Sort((left, right) => left.Length.CompareTo(right.Length));

        // The largest preview is used because small thumbnails often omit EXIF entirely.
        if (exif?.DateTaken is null && previews.Count > 0)
        {
            exif = ReadEmbeddedJpegMetadata(stream, previews[^1].Offset);
        }

        if (exif?.DateTaken is null)
        {
            var looseExifOffset = JpegStructure.FindLooseExifTiffOffset(header);
            if (looseExifOffset >= 0)
            {
                exif = TiffMetadataParser.Parse(stream, looseExifOffset);
            }
        }

        return new ImageFileMetadata(
            exif?.DateTaken,
            previews,
            exif?.Orientation ?? ImageFileMetadata.NormalOrientation);
    }

    private static ImageFileMetadata ReadLooseExifOnly(FileStream stream, ReadOnlySpan<byte> header)
    {
        var exifOffset = JpegStructure.FindLooseExifTiffOffset(header);
        if (exifOffset < 0)
        {
            return ImageFileMetadata.Empty;
        }

        var exif = TiffMetadataParser.Parse(stream, exifOffset);
        return new ImageFileMetadata(exif.DateTaken, [], exif.Orientation);
    }

    private static ImageFileMetadata? ReadCanonExifBox(
        FileStream stream,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> boxType)
    {
        var boxIndex = header.IndexOf(boxType);
        if (boxIndex < 0)
        {
            return null;
        }

        var tiffOffset = boxIndex + boxType.Length;
        return JpegStructure.LooksLikeTiffHeader(header, tiffOffset)
            ? TiffMetadataParser.Parse(stream, tiffOffset)
            : null;
    }

    /// <summary>
    /// Locates the JPEG that follows a named box header. The exact field layout varies
    /// between Canon firmware versions, so the JPEG marker is searched for instead.
    /// </summary>
    private static void AddBoxedJpeg(
        List<PreviewLocation> previews,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> boxType)
    {
        var boxIndex = header.IndexOf(boxType);
        if (boxIndex < 0)
        {
            return;
        }

        var searchStart = boxIndex + boxType.Length;
        var searchEnd = Math.Min(searchStart + MaxBytesAfterBoxHeader, header.Length);
        for (var index = searchStart; index + 3 < searchEnd; index++)
        {
            if (!JpegStructure.IsJpegStart(header.Slice(index)))
            {
                continue;
            }

            var end = JpegStructure.FindJpegEnd(header, index);
            if (end > index + MinimumPreviewLength)
            {
                previews.Add(new PreviewLocation(index, end - index, PixelWidth: 0));
            }

            return;
        }
    }

    private static ImageFileMetadata ReadEmbeddedJpegMetadata(FileStream stream, long jpegOffset)
    {
        var windowLength = (int)Math.Min(stream.Length - jpegOffset, EmbeddedJpegWindowLength);
        if (windowLength < 16)
        {
            return ImageFileMetadata.Empty;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            stream.Seek(jpegOffset, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, windowLength);

            var relativeExifOffset = JpegStructure.FindExifTiffOffset(buffer.AsSpan(0, windowLength), 0);
            return relativeExifOffset < 0
                ? ImageFileMetadata.Empty
                : TiffMetadataParser.Parse(stream, jpegOffset + relativeExifOffset);
        }
        catch (Exception)
        {
            return ImageFileMetadata.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
