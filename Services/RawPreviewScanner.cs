using System.Buffers;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Services;

/// <summary>
/// Last-resort preview finder for RAW containers with no public directory structure,
/// such as Sigma X3F or firmware variants that the structured parsers do not recognise.
/// It locates complete JPEG images by walking marker segments from each start-of-image.
/// </summary>
internal static class RawPreviewScanner
{
    private const int MaxScanLength = 24 * 1024 * 1024;
    private const int MinimumPreviewLength = 1024;
    private const int MaxCandidates = 12;

    public static IReadOnlyList<PreviewLocation> Scan(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1 << 16,
                FileOptions.SequentialScan);

            var windowLength = (int)Math.Min(stream.Length, MaxScanLength);
            if (windowLength < MinimumPreviewLength)
            {
                return [];
            }

            var buffer = ArrayPool<byte>.Shared.Rent(windowLength);
            try
            {
                stream.ReadExactly(buffer, 0, windowLength);
                return FindPreviews(buffer.AsSpan(0, windowLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<PreviewLocation> FindPreviews(ReadOnlySpan<byte> data)
    {
        var starts = JpegStructure.FindJpegStarts(data);
        var previews = new List<PreviewLocation>();

        for (var index = 0; index < starts.Count && previews.Count < MaxCandidates; index++)
        {
            var start = starts[index];
            var length = MeasureJpeg(data, starts, index);
            if (length >= MinimumPreviewLength)
            {
                previews.Add(new PreviewLocation(start, length, PixelWidth: 0));
            }
        }

        previews.Sort((left, right) => left.Length.CompareTo(right.Length));
        return previews;
    }

    /// <summary>
    /// Prefers the exact end marker. When the image runs past the scanned window the gap to
    /// the next embedded image is used instead, which is safe because JPEG decoders stop at
    /// the end-of-image marker and ignore trailing bytes.
    /// </summary>
    private static int MeasureJpeg(ReadOnlySpan<byte> data, List<int> starts, int index)
    {
        var start = starts[index];
        var end = JpegStructure.FindJpegEnd(data, start);
        if (end > start)
        {
            return end - start;
        }

        var boundary = index + 1 < starts.Count ? starts[index + 1] : data.Length;
        return boundary - start;
    }
}
