using System.Buffers.Binary;
using System.Globalization;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Services;

/// <summary>
/// Reads capture dates and embedded JPEG preview offsets from TIFF-structured files.
/// Most RAW formats (CR2, NEF, ARW, DNG, ORF, PEF, RW2, SRW, IIQ and others) are TIFF
/// containers, so a single directory walk serves nearly every camera.
/// </summary>
internal sealed class TiffMetadataParser
{
    private const ushort TagPanasonicJpegFromRaw = 0x002E;
    private const ushort TagImageWidth = 0x0100;
    private const ushort TagCompression = 0x0103;
    private const ushort TagPhotometricInterpretation = 0x0106;
    private const ushort TagStripOffsets = 0x0111;
    private const ushort TagOrientation = 0x0112;
    private const ushort TagStripByteCounts = 0x0117;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagSubIfds = 0x014A;
    private const ushort TagJpegInterchangeFormat = 0x0201;
    private const ushort TagJpegInterchangeFormatLength = 0x0202;
    private const ushort TagExifIfdPointer = 0x8769;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagDateTimeDigitized = 0x9004;

    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeUndefined = 7;

    private const ushort CompressionOldJpeg = 6;
    private const ushort CompressionJpeg = 7;
    private const int PhotometricColorFilterArray = 32803;
    private const int PhotometricLinearRaw = 34892;

    private const int EntryLength = 12;
    private const int MaxEntriesPerDirectory = 512;
    private const int MaxDirectoryCount = 48;
    private const int MaxDirectoryDepth = 6;
    private const int MinimumPreviewLength = 1024;
    private const int DateTextLength = 19;

    private const int PriorityDateTimeOriginal = 0;
    private const int PriorityDateTimeDigitized = 1;
    private const int PriorityDateTime = 2;

    private readonly Stream _stream;
    private readonly long _streamLength;
    private readonly long _baseOffset;
    private readonly List<PreviewLocation> _previews = [];
    private readonly HashSet<long> _visitedDirectories = new();
    private readonly byte[] _entry = new byte[EntryLength];
    private readonly byte[] _dateBuffer = new byte[DateTextLength];

    private bool _isLittleEndian = true;
    private DateTimeOffset? _dateTaken;
    private int _datePriority = int.MaxValue;
    private int _orientation = ImageFileMetadata.NormalOrientation;
    private bool _hasOrientation;
    private int _directoryCount;
    private long _currentEntryOffset;

    private TiffMetadataParser(Stream stream, long baseOffset)
    {
        _stream = stream;
        _streamLength = stream.Length;
        _baseOffset = baseOffset;
    }

    public static ImageFileMetadata Parse(Stream stream, long baseOffset)
    {
        var parser = new TiffMetadataParser(stream, baseOffset);
        return parser.Run();
    }

    private ImageFileMetadata Run()
    {
        if (!TryReadHeader(out var firstDirectoryOffset))
        {
            return ImageFileMetadata.Empty;
        }

        try
        {
            ReadDirectoryChain(firstDirectoryOffset, depth: 0);
        }
        catch (Exception)
        {
            // A malformed directory must still yield whatever was already collected.
        }

        return new ImageFileMetadata(_dateTaken, ValidatePreviews(), _orientation);
    }

    private bool TryReadHeader(out long firstDirectoryOffset)
    {
        firstDirectoryOffset = 0;
        if (!TryReadInto(_baseOffset, _entry, 8))
        {
            return false;
        }

        if (_entry[0] == 0x49 && _entry[1] == 0x49)
        {
            _isLittleEndian = true;
        }
        else if (_entry[0] == 0x4D && _entry[1] == 0x4D)
        {
            _isLittleEndian = false;
        }
        else
        {
            return false;
        }

        // 42 is standard TIFF. Olympus ("OR"/"RS") and Panasonic (0x55) use private magic values.
        var magic = ToUInt16(_entry, 2);
        if (magic is not (42 or 0x4F52 or 0x5352 or 0x0055))
        {
            return false;
        }

        firstDirectoryOffset = ToUInt32(_entry, 4);
        return firstDirectoryOffset > 0;
    }

    private void ReadDirectoryChain(long directoryOffset, int depth)
    {
        if (depth > MaxDirectoryDepth)
        {
            return;
        }

        while (directoryOffset > 0 && _directoryCount < MaxDirectoryCount)
        {
            var absoluteOffset = _baseOffset + directoryOffset;
            if (!_visitedDirectories.Add(absoluteOffset))
            {
                return;
            }

            _directoryCount++;
            directoryOffset = ReadDirectory(absoluteOffset, depth);
        }
    }

    private long ReadDirectory(long directoryOffset, int depth)
    {
        if (!TryReadInto(directoryOffset, _entry, 2))
        {
            return 0;
        }

        var entryCount = ToUInt16(_entry, 0);
        if (entryCount == 0 || entryCount > MaxEntriesPerDirectory)
        {
            return 0;
        }

        var descriptor = new DirectoryDescriptor();
        var childDirectories = new List<long>();

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = directoryOffset + 2 + ((long)index * EntryLength);
            if (!TryReadInto(entryOffset, _entry, EntryLength))
            {
                break;
            }

            _currentEntryOffset = entryOffset;
            var tag = ToUInt16(_entry, 0);
            var type = ToUInt16(_entry, 2);
            var count = ToUInt32(_entry, 4);
            ApplyEntry(tag, type, count, ref descriptor, childDirectories);
        }

        AddDescriptorPreviews(descriptor);

        foreach (var childOffset in childDirectories)
        {
            ReadDirectoryChain(childOffset, depth + 1);
        }

        var nextOffsetPosition = directoryOffset + 2 + ((long)entryCount * EntryLength);
        if (!TryReadInto(nextOffsetPosition, _entry, 4))
        {
            return 0;
        }

        return ToUInt32(_entry, 0);
    }

    private void ApplyEntry(
        ushort tag,
        ushort type,
        uint count,
        ref DirectoryDescriptor descriptor,
        List<long> childDirectories)
    {
        switch (tag)
        {
            case TagImageWidth:
                descriptor.PixelWidth = (int)ReadScalar(type, count);
                break;
            case TagCompression:
                descriptor.Compression = (int)ReadScalar(type, count);
                break;
            case TagPhotometricInterpretation:
                descriptor.PhotometricInterpretation = (int)ReadScalar(type, count);
                break;
            case TagStripOffsets when count == 1:
                descriptor.StripOffset = ReadScalar(type, count);
                break;
            case TagStripByteCounts when count == 1:
                descriptor.StripByteCount = ReadScalar(type, count);
                break;
            // The first directory holds the main image, so its orientation wins over any
            // that preview sub-directories may declare for themselves.
            case TagOrientation when !_hasOrientation:
                CaptureOrientation(type, count);
                break;
            case TagJpegInterchangeFormat:
                descriptor.JpegOffset = ReadScalar(type, count);
                break;
            case TagJpegInterchangeFormatLength:
                descriptor.JpegLength = ReadScalar(type, count);
                break;
            case TagPanasonicJpegFromRaw when (type is TypeUndefined or TypeByte) && count > MinimumPreviewLength:
                AddPreview(GetValueOffset(type, count), (long)count, pixelWidth: 0);
                break;
            case TagExifIfdPointer:
                childDirectories.Add(ReadScalar(type, count));
                break;
            case TagSubIfds:
                CollectSubDirectories(type, count, childDirectories);
                break;
            case TagDateTimeOriginal:
                TryCaptureDate(type, count, PriorityDateTimeOriginal);
                break;
            case TagDateTimeDigitized:
                TryCaptureDate(type, count, PriorityDateTimeDigitized);
                break;
            case TagDateTime:
                TryCaptureDate(type, count, PriorityDateTime);
                break;
        }
    }

    private void AddDescriptorPreviews(DirectoryDescriptor descriptor)
    {
        if (descriptor.JpegOffset > 0 && descriptor.JpegLength > MinimumPreviewLength)
        {
            AddPreview(_baseOffset + descriptor.JpegOffset, descriptor.JpegLength, pixelWidth: 0);
        }

        var isJpegCompressed = descriptor.Compression is CompressionOldJpeg or CompressionJpeg;
        var isRawSensorData = descriptor.PhotometricInterpretation is PhotometricColorFilterArray or PhotometricLinearRaw;
        if (isJpegCompressed
            && !isRawSensorData
            && descriptor.StripOffset > 0
            && descriptor.StripByteCount > MinimumPreviewLength)
        {
            AddPreview(_baseOffset + descriptor.StripOffset, descriptor.StripByteCount, descriptor.PixelWidth);
        }
    }

    private void CollectSubDirectories(ushort type, uint count, List<long> childDirectories)
    {
        if (type != TypeLong || count == 0 || count > MaxEntriesPerDirectory)
        {
            return;
        }

        var valueOffset = GetValueOffset(type, count);
        for (var index = 0u; index < count; index++)
        {
            if (!TryReadInto(valueOffset + (index * 4), _entry, 4))
            {
                return;
            }

            var childOffset = ToUInt32(_entry, 0);
            if (childOffset > 0)
            {
                childDirectories.Add(childOffset);
            }
        }
    }

    private void CaptureOrientation(ushort type, uint count)
    {
        var value = (int)ReadScalar(type, count);
        if (value is >= 1 and <= 8)
        {
            _orientation = value;
            _hasOrientation = true;
        }
    }

    private void TryCaptureDate(ushort type, uint count, int priority)
    {
        if (type != TypeAscii || count < DateTextLength || priority >= _datePriority)
        {
            return;
        }

        var valueOffset = GetValueOffset(type, count);
        if (!TryReadInto(valueOffset, _dateBuffer, DateTextLength))
        {
            return;
        }

        var text = System.Text.Encoding.ASCII.GetString(_dateBuffer, 0, DateTextLength);
        if (!DateTime.TryParseExact(
                text,
                "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return;
        }

        // EXIF stores camera wall-clock time with no zone, so it is treated as local time.
        _dateTaken = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
        _datePriority = priority;
    }

    private void AddPreview(long offset, long length, int pixelWidth)
    {
        if (offset <= 0 || length is < MinimumPreviewLength or > int.MaxValue)
        {
            return;
        }

        _previews.Add(new PreviewLocation(offset, (int)length, pixelWidth));
    }

    private IReadOnlyList<PreviewLocation> ValidatePreviews()
    {
        if (_previews.Count == 0)
        {
            return [];
        }

        var validated = new List<PreviewLocation>();
        var seenOffsets = new HashSet<long>(_previews.Count);

        foreach (var preview in _previews)
        {
            if (preview.Offset + preview.Length > _streamLength || !seenOffsets.Add(preview.Offset))
            {
                continue;
            }

            if (TryReadInto(preview.Offset, _entry, 3) && JpegStructure.IsJpegStart(_entry.AsSpan(0, 3)))
            {
                validated.Add(preview);
            }
        }

        validated.Sort((left, right) => left.Length.CompareTo(right.Length));
        return validated;
    }

    private long GetValueOffset(ushort type, uint count)
    {
        var byteCount = (long)GetTypeSize(type) * count;
        return byteCount <= 4
            ? _currentEntryOffset + 8
            : _baseOffset + ToUInt32(_entry, 8);
    }

    private long ReadScalar(ushort type, uint count)
    {
        if (count == 0)
        {
            return 0;
        }

        var valueOffset = GetValueOffset(type, count);
        var size = GetTypeSize(type);
        if (!TryReadInto(valueOffset, _entry, size))
        {
            return 0;
        }

        return size switch
        {
            1 => (long)_entry[0],
            2 => (long)ToUInt16(_entry, 0),
            _ => (long)ToUInt32(_entry, 0),
        };
    }

    private static int GetTypeSize(ushort type) => type switch
    {
        TypeShort or 8 => 2,
        TypeLong or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 1,
    };

    private bool TryReadInto(long offset, byte[] buffer, int length)
    {
        if (offset < 0 || length > buffer.Length)
        {
            return false;
        }

        if (offset + length > _streamLength)
        {
            return false;
        }

        try
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(buffer, 0, length);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ushort ToUInt16(byte[] buffer, int offset)
    {
        var span = buffer.AsSpan(offset, 2);
        return _isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(span)
            : BinaryPrimitives.ReadUInt16BigEndian(span);
    }

    private uint ToUInt32(byte[] buffer, int offset)
    {
        var span = buffer.AsSpan(offset, 4);
        return _isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(span)
            : BinaryPrimitives.ReadUInt32BigEndian(span);
    }

    /// <summary>
    /// Tags of one directory are gathered before previews are derived, because a JPEG
    /// preview is only identifiable once its offset, length and compression are all known.
    /// </summary>
    private struct DirectoryDescriptor
    {
        public int PixelWidth;
        public int Compression;
        public int PhotometricInterpretation;
        public long StripOffset;
        public long StripByteCount;
        public long JpegOffset;
        public long JpegLength;
    }
}
