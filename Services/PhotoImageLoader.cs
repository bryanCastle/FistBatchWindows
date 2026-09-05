using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoOrganizer.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoOrganizer.Services;

/// <summary>
/// Produces displayable bitmaps for timeline tiles and the large viewer. Standard formats are
/// handed straight to Windows; RAW files are shown from their embedded JPEG preview, which is
/// both codec independent and far faster than decoding sensor data.
/// </summary>
internal static class PhotoImageLoader
{
    private const int MinimumThumbnailLength = 12 * 1024;
    private const int MinimumThumbnailPixelWidth = 240;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<PreviewLocation>> ScannedPreviews =
        new(StringComparer.OrdinalIgnoreCase);

    public static void ResetPreviewCache() => ScannedPreviews.Clear();

    /// <summary>
    /// A timeline thumbnail. Decoding is left to the point of rendering, which is what keeps a
    /// large folder responsive while tiles scroll past.
    /// </summary>
    public static Task<ImageSource?> CreateThumbnailAsync(
        PhotoItem photo,
        int decodePixelWidth,
        CancellationToken cancellationToken)
        => CreateImageAsync(photo, decodePixelWidth, preferLargestPreview: false, decodeNow: false, cancellationToken);

    /// <summary>
    /// A picture for the large viewer, decoded to finished pixels before this returns. The
    /// caller keeps this bitmap in a small cache and wraps a copy in a new
    /// <see cref="SoftwareBitmapSource"/> each time it is shown. Reusing one source across
    /// pictures is what crashed the viewer: XAML closes the COM object when Source changes,
    /// and handing that same object back later raises <c>RO_E_CLOSED</c>.
    /// </summary>
    public static async Task<SoftwareBitmap?> CreateViewerBitmapAsync(
        PhotoItem photo,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!photo.IsRawFormat)
            {
                using var stream = await FileRandomAccessStream.OpenAsync(photo.Path, FileAccessMode.Read);
                return await DecodeToSoftwareBitmapAsync(stream, decodePixelWidth, photo.Orientation);
            }

            var previews = await ResolvePreviewsAsync(photo, cancellationToken);
            foreach (var preview in OrderCandidates(previews, preferLargest: true))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytes = await Task.Run(() => ReadPreviewBytes(photo.Path, preview), cancellationToken);
                if (bytes is null)
                {
                    continue;
                }

                var width = ResolveDecodeWidth(preview, decodePixelWidth);
                var stream = await CreateMemoryStreamAsync(bytes);
                using (stream)
                {
                    var bitmap = await DecodeToSoftwareBitmapAsync(stream, width, photo.Orientation);
                    if (bitmap is not null)
                    {
                        return bitmap;
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<ImageSource?> CreateImageAsync(
        PhotoItem photo,
        int decodePixelWidth,
        bool preferLargestPreview,
        bool decodeNow,
        CancellationToken cancellationToken)
    {
        if (!photo.IsRawFormat)
        {
            return decodeNow || RequiresRotation(photo.Orientation)
                ? await DecodeFileWithOrientationAsync(photo.Path, decodePixelWidth, photo.Orientation)
                : CreateFileBackedBitmap(photo.Path, decodePixelWidth);
        }

        var previews = await ResolvePreviewsAsync(photo, cancellationToken);
        foreach (var preview in OrderCandidates(previews, preferLargestPreview))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = await Task.Run(() => ReadPreviewBytes(photo.Path, preview), cancellationToken);
            if (bytes is null)
            {
                continue;
            }

            var width = ResolveDecodeWidth(preview, decodePixelWidth);
            var bitmap = await TryDecodeAsync(bytes, width, photo.Orientation, decodeNow);
            if (bitmap is not null)
            {
                return bitmap;
            }
        }

        return null;
    }

    private static bool RequiresRotation(int orientation)
        => orientation is > ImageFileMetadata.NormalOrientation and <= 8;

    private static BitmapImage CreateFileBackedBitmap(string path, int decodePixelWidth) => new()
    {
        DecodePixelType = DecodePixelType.Logical,
        DecodePixelWidth = decodePixelWidth,
        UriSource = new Uri(path, UriKind.Absolute),
    };

    private static async Task<IReadOnlyList<PreviewLocation>> ResolvePreviewsAsync(
        PhotoItem photo,
        CancellationToken cancellationToken)
    {
        if (photo.Previews.Count > 0)
        {
            return photo.Previews;
        }

        if (ScannedPreviews.TryGetValue(photo.Path, out var cached))
        {
            return cached;
        }

        var scanned = await Task.Run(() => RawPreviewScanner.Scan(photo.Path), cancellationToken);
        ScannedPreviews[photo.Path] = scanned;
        return scanned;
    }

    private static IEnumerable<PreviewLocation> OrderCandidates(
        IReadOnlyList<PreviewLocation> previews,
        bool preferLargest)
    {
        if (preferLargest)
        {
            return previews.OrderByDescending(preview => preview.Length);
        }

        // Smallest preview that still fills a tile decodes fastest; tiny thumbnails are a fallback.
        return previews
            .OrderBy(preview => IsAdequateForThumbnail(preview) ? 0 : 1)
            .ThenBy(preview => preview.Length);
    }

    private static bool IsAdequateForThumbnail(PreviewLocation preview)
        => preview.Length >= MinimumThumbnailLength || preview.PixelWidth >= MinimumThumbnailPixelWidth;

    private static int ResolveDecodeWidth(PreviewLocation preview, int requestedWidth)
        => preview.PixelWidth > 0 ? Math.Min(requestedWidth, preview.PixelWidth) : requestedWidth;

    private static byte[]? ReadPreviewBytes(string path, PreviewLocation preview)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1 << 16,
                FileOptions.RandomAccess);

            if (preview.Offset < 0 || preview.Offset + preview.Length > stream.Length)
            {
                return null;
            }

            var bytes = new byte[preview.Length];
            stream.Seek(preview.Offset, SeekOrigin.Begin);
            stream.ReadExactly(bytes, 0, preview.Length);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Must run on the UI thread because the bitmap types are bound to it. Returns null when the
    /// bytes are not a decodable image so the next preview candidate can be tried.
    /// </summary>
    private static async Task<ImageSource?> TryDecodeAsync(
        byte[] bytes,
        int decodePixelWidth,
        int orientation,
        bool decodeNow)
    {
        try
        {
            var stream = await CreateMemoryStreamAsync(bytes);
            if (decodeNow || RequiresRotation(orientation))
            {
                // The decoder reads the pixels out in full here, so the stream is finished with.
                using (stream)
                {
                    return await DecodeWithOrientationAsync(stream, decodePixelWidth, orientation);
                }
            }

            // Deliberately left open. A BitmapImage decodes only when it is first rendered, so
            // closing the stream now would fault that later render for any tile that is off screen
            // or in a hidden panel. The stream is collected along with the bitmap holding it.
            return await DecodeUprightAsync(stream, decodePixelWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<InMemoryRandomAccessStream> CreateMemoryStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        return stream;
    }

    private static async Task<ImageSource> DecodeUprightAsync(IRandomAccessStream stream, int decodePixelWidth)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = decodePixelWidth,
        };

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static async Task<ImageSource?> DecodeFileWithOrientationAsync(
        string path,
        int decodePixelWidth,
        int orientation)
    {
        try
        {
            using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            return await DecodeWithOrientationAsync(stream, decodePixelWidth, orientation);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Rotates during decode rather than at render time, so the corrected image measures and
    /// stretches naturally in the timeline tile and the viewer.
    /// </summary>
    private static async Task<ImageSource?> DecodeWithOrientationAsync(
        IRandomAccessStream stream,
        int decodePixelWidth,
        int orientation)
    {
        var pixels = await DecodeToSoftwareBitmapAsync(stream, decodePixelWidth, orientation);
        if (pixels is null)
        {
            return null;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(pixels);
        return source;
    }

    private static async Task<SoftwareBitmap?> DecodeToSoftwareBitmapAsync(
        IRandomAccessStream stream,
        int decodePixelWidth,
        int orientation)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var (flip, rotation) = ResolveTransform(orientation, decoder.PixelWidth, decoder.PixelHeight);
        var (scaledWidth, scaledHeight) = ResolveScaledSize(decoder.PixelWidth, decoder.PixelHeight, decodePixelWidth);

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            Flip = flip,
            Rotation = rotation,
            InterpolationMode = BitmapInterpolationMode.Linear,
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    /// <summary>
    /// Maps an EXIF orientation onto the flip and rotation that correct it. The transform is
    /// skipped when a quarter turn is requested but the pixels are already portrait, which
    /// means something has rotated them already and left a stale tag behind.
    /// </summary>
    private static (BitmapFlip Flip, BitmapRotation Rotation) ResolveTransform(
        int orientation,
        uint pixelWidth,
        uint pixelHeight)
    {
        var (flip, rotation) = orientation switch
        {
            2 => (BitmapFlip.Horizontal, BitmapRotation.None),
            3 => (BitmapFlip.None, BitmapRotation.Clockwise180Degrees),
            4 => (BitmapFlip.Vertical, BitmapRotation.None),
            5 => (BitmapFlip.Vertical, BitmapRotation.Clockwise90Degrees),
            6 => (BitmapFlip.None, BitmapRotation.Clockwise90Degrees),
            7 => (BitmapFlip.Horizontal, BitmapRotation.Clockwise90Degrees),
            8 => (BitmapFlip.None, BitmapRotation.Clockwise270Degrees),
            _ => (BitmapFlip.None, BitmapRotation.None),
        };

        var isQuarterTurn = rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;
        return isQuarterTurn && pixelHeight > pixelWidth
            ? (BitmapFlip.None, BitmapRotation.None)
            : (flip, rotation);
    }

    /// <summary>
    /// Scales the longest edge, because after a quarter turn the width and height swap.
    /// </summary>
    private static (uint Width, uint Height) ResolveScaledSize(uint pixelWidth, uint pixelHeight, int maximumEdge)
    {
        var longestEdge = Math.Max(pixelWidth, pixelHeight);
        if (maximumEdge <= 0 || longestEdge <= (uint)maximumEdge)
        {
            return (pixelWidth, pixelHeight);
        }

        var scale = (double)maximumEdge / longestEdge;
        return (
            (uint)Math.Max(1, Math.Round(pixelWidth * scale)),
            (uint)Math.Max(1, Math.Round(pixelHeight * scale)));
    }
}
