using PhotoOrganizer.Models;

namespace PhotoOrganizer.Services;

internal sealed record ImageFileMetadata(
    DateTimeOffset? DateTaken,
    IReadOnlyList<PreviewLocation> Previews,
    int Orientation = ImageFileMetadata.NormalOrientation)
{
    /// <summary>
    /// EXIF orientation 1, meaning the stored pixels are already the right way up.
    /// </summary>
    public const int NormalOrientation = 1;

    public static readonly ImageFileMetadata Empty = new(null, []);

    public bool RequiresRotation => Orientation is > NormalOrientation and <= 8;
}
